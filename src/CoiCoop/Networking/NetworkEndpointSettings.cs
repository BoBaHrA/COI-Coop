using System;
using System.Net;

namespace CoiCoop.Networking;

/// <summary>
/// Development endpoint settings shared by authoritative gameplay and all
/// presentation sidecars.
///
/// Defaults preserve the existing single-PC loopback workflow. LAN launchers may
/// override them per-process without persisting anything in the user's system:
/// - COI_COOP_BIND: host listener address (127.0.0.1 by default, 0.0.0.0 for LAN)
/// - COI_COOP_HOST: client target host/IP (127.0.0.1 by default)
/// </summary>
internal static class NetworkEndpointSettings {
    private const string DefaultBindAddress = "127.0.0.1";
    private const string DefaultClientHost = "127.0.0.1";

    public static string ClientHost => ReadNonEmpty("COI_COOP_HOST", DefaultClientHost);

    public static string HostBindText => ReadNonEmpty("COI_COOP_BIND", DefaultBindAddress);

    public static IPAddress ResolveHostBindAddress() {
        var value = HostBindText;
        if (string.Equals(value, "*", StringComparison.Ordinal)
            || string.Equals(value, "any", StringComparison.OrdinalIgnoreCase)) {
            return IPAddress.Any;
        }

        IPAddress address;
        if (IPAddress.TryParse(value, out address)) {
            return address;
        }

        throw new InvalidOperationException(
            "COI_COOP_BIND must be an IP address such as 127.0.0.1 or 0.0.0.0; got '"
            + value + "'.");
    }

    public static string DescribeHostBind(IPAddress address) {
        if (address == null) return HostBindText;
        if (Equals(address, IPAddress.Any)) return "0.0.0.0";
        if (Equals(address, IPAddress.IPv6Any)) return "::";
        return address.ToString();
    }

    private static string ReadNonEmpty(string name, string fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
