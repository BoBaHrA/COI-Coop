using System;
using System.Threading;
using CoiCoop.Networking;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Mods;

namespace CoiCoop;

public sealed class CoiCoopMod : DataOnlyMod {
    private static int s_probeStarted;

    public CoiCoopMod(ModManifest manifest) : base(manifest) {
        Log.Info("COI-Coop: constructed");
    }

    public override void RegisterPrototypes(ProtoRegistrator registrator) {
        if (Interlocked.Exchange(ref s_probeStarted, 1) != 0) {
            return;
        }

        var mode = ReadEnvironmentInt("COI_COOP_MODE", JsonConfig.GetInt("network_mode"));
        var port = ReadEnvironmentInt("COI_COOP_PORT", JsonConfig.GetInt("server_port"));

        if (mode == 0) {
            Log.Info("COI-Coop: networking is disabled (mode 0)");
            return;
        }

        if (port < 1024 || port > 65535) {
            Log.Info($"COI-Coop: invalid port {port}; expected 1024-65535");
            return;
        }

        var thread = new Thread(() => RunTransportProbe(mode, port)) {
            IsBackground = true,
            Name = "COI-Coop transport probe"
        };
        thread.Start();
    }

    private static int ReadEnvironmentInt(string name, int fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static void RunTransportProbe(int mode, int port) {
        try {
            if (mode == 1) {
                Log.Info($"COI-Coop: HOST waiting on 127.0.0.1:{port}");
                LocalTransportProbe.HostOnce(port);
                Log.Info("COI-Coop: HOST handshake + ping completed successfully");
                return;
            }

            if (mode == 2) {
                Log.Info($"COI-Coop: CLIENT connecting to 127.0.0.1:{port}");
                var connected = LocalTransportProbe.ClientOnce(port);
                Log.Info(connected
                    ? "COI-Coop: CLIENT handshake + ping completed successfully"
                    : "COI-Coop: CLIENT handshake was rejected");
                return;
            }

            Log.Info($"COI-Coop: unsupported network mode: {mode}");
        }
        catch (Exception ex) {
            Log.Info("COI-Coop: transport probe failed: " + ex);
        }
    }

    public override void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) {
    }
}
