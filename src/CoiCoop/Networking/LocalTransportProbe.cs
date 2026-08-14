using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoiCoop.Networking;

internal static class LocalTransportProbe {
    public static void HostOnce(int port) {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try {
            using (var client = listener.AcceptTcpClient())
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {
                var hello = reader.ReadLine();
                if (!NetworkProtocol.IsCompatibleHello(hello)) {
                    writer.WriteLine("REJECT|INCOMPATIBLE_PROTOCOL");
                    return;
                }

                writer.WriteLine(NetworkProtocol.Welcome());

                var ping = reader.ReadLine();
                if (!NetworkProtocol.TryReadPing(ping, out var nonce)) {
                    throw new IOException("Expected PING after handshake.");
                }

                writer.WriteLine(NetworkProtocol.Pong(nonce));
            }
        }
        finally {
            listener.Stop();
        }
    }

    public static bool ClientOnce(int port) {
        using (var client = new TcpClient()) {
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
}
