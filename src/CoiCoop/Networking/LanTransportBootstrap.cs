using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Mafi;

namespace CoiCoop.Networking;

/// <summary>
/// Transitional two-PC LAN transport.
///
/// The proven gameplay/preview sessions intentionally remain loopback-only during
/// the first physical-PC gate. This bridge exposes four external LAN ports on the
/// host and provides matching loopback forwarders on the client. As a result, the
/// authority protocol and preview protocol are completely unchanged while we prove
/// that the game works across two machines.
///
/// Host:   LAN:externalBase+n -> 127.0.0.1:internalBase+n
/// Client: 127.0.0.1:internalBase+n -> host:externalBase+n
///
/// This is a development stepping stone. The final internet transport will replace
/// these temporary per-lane tunnels with a multiplexed session transport.
/// </summary>
internal sealed class LanTransportBootstrap : IDisposable {
    private const int LaneCount = 4;
    private static readonly string[] LaneNames = { "gameplay", "preview", "path", "blueprint" };
    private static readonly object s_lock = new object();
    private static LanTransportBootstrap s_current;

    private readonly bool m_isHost;
    private readonly int m_internalBasePort;
    private readonly int m_externalBasePort;
    private readonly string m_remoteHost;
    private readonly IPAddress m_hostBindAddress;
    private readonly List<LanTcpTunnel> m_tunnels = new List<LanTcpTunnel>();

    private LanTransportBootstrap(
        bool isHost,
        int internalBasePort,
        int externalBasePort,
        string remoteHost,
        IPAddress hostBindAddress) {

        m_isHost = isHost;
        m_internalBasePort = internalBasePort;
        m_externalBasePort = externalBasePort;
        m_remoteHost = remoteHost;
        m_hostBindAddress = hostBindAddress;
    }

    public static void EnsureStarted() {
        if (!ReadEnvironmentBool("COI_COOP_LAN", false)) return;

        var mode = ReadEnvironmentInt("COI_COOP_MODE", 0);
        if (mode != 1 && mode != 2) return;

        var internalBase = ReadEnvironmentInt("COI_COOP_PORT", 27015);
        var externalBase = ReadEnvironmentInt("COI_COOP_LAN_BASE_PORT", internalBase + 100);
        if (internalBase < 1024 || internalBase > 65532) {
            Log.Info("COI-Coop: LAN bridge disabled - invalid internal base port " + internalBase);
            return;
        }
        if (externalBase < 1024 || externalBase > 65532) {
            Log.Info("COI-Coop: LAN bridge disabled - invalid external base port " + externalBase);
            return;
        }

        var isHost = mode == 1;
        IPAddress bindAddress = IPAddress.Loopback;
        string remoteHost = NetworkEndpointSettings.ClientHost;
        try {
            if (isHost) bindAddress = NetworkEndpointSettings.ResolveHostBindAddress();
        }
        catch (Exception ex) {
            Log.Info("COI-Coop: LAN bridge disabled - " + ex.Message);
            return;
        }

        lock (s_lock) {
            if (s_current != null) return;
            var next = new LanTransportBootstrap(
                isHost,
                internalBase,
                externalBase,
                remoteHost,
                bindAddress);
            s_current = next;
            next.Start();
        }
    }

    private void Start() {
        for (var i = 0; i < LaneCount; i++) {
            var lane = LaneNames[i];
            LanTcpTunnel tunnel;
            if (m_isHost) {
                tunnel = new LanTcpTunnel(
                    lane,
                    m_hostBindAddress,
                    m_externalBasePort + i,
                    "127.0.0.1",
                    m_internalBasePort + i,
                    message => Log.Info("COI-Coop: LAN " + message));
            }
            else {
                tunnel = new LanTcpTunnel(
                    lane,
                    IPAddress.Loopback,
                    m_internalBasePort + i,
                    m_remoteHost,
                    m_externalBasePort + i,
                    message => Log.Info("COI-Coop: LAN " + message));
            }
            m_tunnels.Add(tunnel);
            tunnel.Start();
        }

        Log.Info(
            m_isHost
                ? "COI-Coop: LAN HOST bridge enabled bind="
                    + NetworkEndpointSettings.DescribeHostBind(m_hostBindAddress)
                    + " external=" + m_externalBasePort + "-" + (m_externalBasePort + LaneCount - 1)
                    + " -> loopback=" + m_internalBasePort + "-" + (m_internalBasePort + LaneCount - 1)
                : "COI-Coop: LAN CLIENT bridge enabled host=" + m_remoteHost
                    + " external=" + m_externalBasePort + "-" + (m_externalBasePort + LaneCount - 1)
                    + " <- loopback=" + m_internalBasePort + "-" + (m_internalBasePort + LaneCount - 1));
    }

    public void Dispose() {
        for (var i = 0; i < m_tunnels.Count; i++) {
            try { m_tunnels[i].Dispose(); } catch { }
        }
        m_tunnels.Clear();
        lock (s_lock) {
            if (ReferenceEquals(s_current, this)) s_current = null;
        }
    }

    private static int ReadEnvironmentInt(string name, int fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        int parsed;
        return int.TryParse(value, out parsed) ? parsed : fallback;
    }

    private static bool ReadEnvironmentBool(string name, bool fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }

    private sealed class LanTcpTunnel : IDisposable {
        private const int ConnectTimeoutMs = 2000;
        private readonly string m_name;
        private readonly IPAddress m_listenAddress;
        private readonly int m_listenPort;
        private readonly string m_targetHost;
        private readonly int m_targetPort;
        private readonly Action<string> m_log;

        private Thread m_thread;
        private volatile bool m_stop;
        private TcpListener m_listener;
        private TcpClient m_accepted;
        private TcpClient m_target;

        public LanTcpTunnel(
            string name,
            IPAddress listenAddress,
            int listenPort,
            string targetHost,
            int targetPort,
            Action<string> log) {

            m_name = name;
            m_listenAddress = listenAddress;
            m_listenPort = listenPort;
            m_targetHost = targetHost;
            m_targetPort = targetPort;
            m_log = log;
        }

        public void Start() {
            if (m_thread != null) return;
            m_thread = new Thread(Run) {
                IsBackground = true,
                Name = "COI-Coop LAN " + m_name
            };
            m_thread.Start();
        }

        private void Run() {
            try {
                m_listener = new TcpListener(m_listenAddress, m_listenPort);
                m_listener.Start();
                m_log?.Invoke(
                    m_name + " tunnel listening on " + m_listenAddress + ":" + m_listenPort
                    + " -> " + m_targetHost + ":" + m_targetPort);

                while (!m_stop) {
                    TcpClient accepted = null;
                    TcpClient target = null;
                    try {
                        accepted = m_listener.AcceptTcpClient();
                        m_accepted = accepted;
                        Configure(accepted);

                        target = ConnectTarget();
                        if (target == null) {
                            m_log?.Invoke(
                                m_name + " tunnel target unavailable " + m_targetHost + ":" + m_targetPort);
                            continue;
                        }
                        m_target = target;
                        Configure(target);

                        m_log?.Invoke(m_name + " tunnel connected");
                        PumpBidirectional(accepted, target);
                    }
                    catch (SocketException ex) {
                        if (!m_stop) m_log?.Invoke(m_name + " tunnel socket closed: " + ex.Message);
                    }
                    catch (Exception ex) {
                        if (!m_stop) m_log?.Invoke(m_name + " tunnel failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    finally {
                        try { accepted?.Close(); } catch { }
                        try { target?.Close(); } catch { }
                        m_accepted = null;
                        m_target = null;
                    }
                }
            }
            catch (SocketException ex) {
                if (!m_stop) m_log?.Invoke(m_name + " listener failed: " + ex.Message);
            }
            catch (Exception ex) {
                if (!m_stop) m_log?.Invoke(m_name + " listener failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally {
                try { m_listener?.Stop(); } catch { }
                m_listener = null;
            }
        }

        private TcpClient ConnectTarget() {
            var client = new TcpClient();
            try {
                var async = client.BeginConnect(m_targetHost, m_targetPort, null, null);
                try {
                    if (!async.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) {
                        client.Close();
                        return null;
                    }
                    client.EndConnect(async);
                    return client;
                }
                finally {
                    try { async.AsyncWaitHandle.Close(); } catch { }
                }
            }
            catch {
                try { client.Close(); } catch { }
                return null;
            }
        }

        private void PumpBidirectional(TcpClient left, TcpClient right) {
            using (var finished = new ManualResetEvent(false)) {
                var leftToRight = new Thread(() => Pump(left, right, finished)) {
                    IsBackground = true,
                    Name = "COI-Coop LAN " + m_name + " A>B"
                };
                var rightToLeft = new Thread(() => Pump(right, left, finished)) {
                    IsBackground = true,
                    Name = "COI-Coop LAN " + m_name + " B>A"
                };

                leftToRight.Start();
                rightToLeft.Start();
                finished.WaitOne();

                try { left.Close(); } catch { }
                try { right.Close(); } catch { }
                leftToRight.Join(500);
                rightToLeft.Join(500);
                m_log?.Invoke(m_name + " tunnel disconnected");
            }
        }

        private static void Pump(TcpClient source, TcpClient destination, EventWaitHandle finished) {
            var buffer = new byte[32 * 1024];
            try {
                var input = source.GetStream();
                var output = destination.GetStream();
                while (true) {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    output.Flush();
                }
            }
            catch {
            }
            finally {
                try { finished.Set(); } catch { }
            }
        }

        private static void Configure(TcpClient client) {
            client.NoDelay = true;
            client.ReceiveTimeout = 0;
            client.SendTimeout = 5000;
        }

        public void Dispose() {
            m_stop = true;
            try { m_accepted?.Close(); } catch { }
            try { m_target?.Close(); } catch { }
            try { m_listener?.Stop(); } catch { }
            if (m_thread != null && m_thread.IsAlive) m_thread.Join(2000);
            m_thread = null;
            m_accepted = null;
            m_target = null;
            m_listener = null;
        }
    }
}
