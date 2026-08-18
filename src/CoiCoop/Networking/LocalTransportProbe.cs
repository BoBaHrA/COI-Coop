using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CoiCoop.Networking;

internal static class LocalTransportProbe {
    private const int IoTimeoutMs = 5000;

    public static bool HostOnce(int port, int acceptTimeoutMs = 30000) {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try {
            var timer = Stopwatch.StartNew();
            while (!listener.Pending()) {
                if (timer.ElapsedMilliseconds >= acceptTimeoutMs) {
                    return false;
                }
                Thread.Sleep(25);
            }

            using (var client = listener.AcceptTcpClient()) {
                ConfigureClient(client);
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {
                    var hello = reader.ReadLine();
                    if (!NetworkProtocol.IsCompatibleHello(hello)) {
                        writer.WriteLine("REJECT|INCOMPATIBLE_PROTOCOL");
                        return false;
                    }

                    writer.WriteLine(NetworkProtocol.Welcome());

                    var ping = reader.ReadLine();
                    if (!NetworkProtocol.TryReadPing(ping, out var nonce)) {
                        throw new IOException("Expected PING after handshake.");
                    }

                    writer.WriteLine(NetworkProtocol.Pong(nonce));
                    return true;
                }
            }
        }
        finally {
            listener.Stop();
        }
    }

    public static bool ClientOnce(int port) {
        using (var client = new TcpClient()) {
            ConfigureClient(client);
            client.Connect(IPAddress.Loopback, port);
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {
                writer.WriteLine(NetworkProtocol.Hello());
                if (!NetworkProtocol.IsCompatibleWelcome(reader.ReadLine())) {
                    return false;
                }

                const long nonce = 1;
                writer.WriteLine(NetworkProtocol.Ping(nonce));
                return NetworkProtocol.TryReadPong(reader.ReadLine(), out var returnedNonce)
                    && returnedNonce == nonce;
            }
        }
    }

    private static void ConfigureClient(TcpClient client) {
        client.NoDelay = true;
        client.ReceiveTimeout = IoTimeoutMs;
        client.SendTimeout = IoTimeoutMs;
    }
}
