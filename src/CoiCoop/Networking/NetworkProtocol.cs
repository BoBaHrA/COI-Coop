using System;

namespace CoiCoop.Networking;

internal static class NetworkProtocol {
    public const int ProtocolVersion = 1;
    public const string ModVersion = "0.0.1";

    public static string Hello() => $"HELLO|{ProtocolVersion}|{ModVersion}";
    public static string Welcome() => $"WELCOME|{ProtocolVersion}|{ModVersion}";
    public static string Ping(long nonce) => $"PING|{nonce}";
    public static string Pong(long nonce) => $"PONG|{nonce}";

    public static bool IsCompatibleHello(string line) {
        if (!TrySplit(line, "HELLO", out var parts) || parts.Length != 3) {
            return false;
        }

        return int.TryParse(parts[1], out var protocol)
            && protocol == ProtocolVersion
            && string.Equals(parts[2], ModVersion, StringComparison.Ordinal);
    }

    public static bool IsCompatibleWelcome(string line) {
        if (!TrySplit(line, "WELCOME", out var parts) || parts.Length != 3) {
            return false;
        }

        return int.TryParse(parts[1], out var protocol)
            && protocol == ProtocolVersion
            && string.Equals(parts[2], ModVersion, StringComparison.Ordinal);
    }

    public static bool TryReadPing(string line, out long nonce) => TryReadNonce(line, "PING", out nonce);
    public static bool TryReadPong(string line, out long nonce) => TryReadNonce(line, "PONG", out nonce);

    private static bool TryReadNonce(string line, string expectedType, out long nonce) {
        nonce = 0;
        return TrySplit(line, expectedType, out var parts)
            && parts.Length == 2
            && long.TryParse(parts[1], out nonce);
    }

    private static bool TrySplit(string line, string expectedType, out string[] parts) {
        parts = (line ?? string.Empty).Split('|');
        return parts.Length > 0 && string.Equals(parts[0], expectedType, StringComparison.Ordinal);
    }
}
