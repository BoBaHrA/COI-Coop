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
                var line = reader.ReadLine();
                writer.WriteLine(NetworkProtocol.IsCompatibleHello(line)
                    ? NetworkProtocol.Welcome()
                    : "REJECT|INCOMPATIBLE_PROTOCOL");
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
                return NetworkProtocol.IsCompatibleWelcome(reader.ReadLine());
            }
        }
    }
}
