using System.Net;
using System.Net.Sockets;
using System.Text;
using CoiCoop.Networking;

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static int Port() {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
static bool Wait(Func<bool> predicate, int ms = 5000) {
    var start = Environment.TickCount64;
    while (Environment.TickCount64 - start < ms) { if (predicate()) return true; Thread.Sleep(5); }
    return false;
}

static LiveWorldServer.Snapshot Download(int port, string key, string build, byte[] expected, LiveWorldServer server, long step) {
    var download = Task.Run(() => {
        using var peer = new TcpClient(); peer.Connect(IPAddress.Loopback, port); peer.ReceiveTimeout = 5000;
        var stream = peer.GetStream();
        stream.Write(Encoding.ASCII.GetBytes($"COI_LIVE|1|{key}|{build}\n"));
        using var reader = new BinaryReader(stream);
        Require(reader.ReadString() == "COI_LIVE_SNAPSHOT_1", "Snapshot header mismatch");
        Require(reader.ReadString() == build, "Build fingerprint changed");
        var result = new LiveWorldServer.Snapshot { Ticket=reader.ReadString(), Hash=reader.ReadString(), Step=reader.ReadInt64(), Paused=reader.ReadBoolean() };
        var count = reader.ReadInt32();
        result.Bytes = reader.ReadBytes(count);
        Require(result.Bytes.SequenceEqual(expected), "Snapshot changed in transit");
        Require(LiveWorldServer.Hash(result.Bytes) == result.Hash, "Snapshot hash mismatch");
        return result;
    });
    LiveWorldServer.Request request = null;
    Require(Wait(() => server.TryTakeRequest(out request)), "No snapshot request at host boundary");
    Require(!download.IsCompleted, "Server returned a stale disk snapshot before live capture");
    request.Complete(new LiveWorldServer.Snapshot { Bytes=expected, Hash=LiveWorldServer.Hash(expected),
        Ticket=LiveWorldServer.NewTicket(), Step=step, Paused=false });
    return download.GetAwaiter().GetResult();
}

var port = Port();
var key = LiveWorldServer.NewTicket();
var build = new string('B',64);
using var server = new LiveWorldServer(port,key,build,Console.WriteLine);
foreach (var hello in new[] { $"COI_LIVE|1|WRONG|{build}\n", $"COI_LIVE|1|{key}|WRONG\n" }) {
    using var peer = new TcpClient(); peer.Connect(IPAddress.Loopback,port); peer.ReceiveTimeout=3000;
    peer.GetStream().Write(Encoding.ASCII.GetBytes(hello));
    using var reader = new BinaryReader(peer.GetStream());
    Require(reader.ReadString().StartsWith("REJECT:"), "Wrong key/build was admitted");
    Require(!server.TryTakeRequest(out _), "Unauthorized request reached game thread");
}
Console.WriteLine("PASS: connection key and exact build identity checked before touching host state");

var firstBytes = Enumerable.Range(0,250000).Select(i => (byte)i).ToArray();
var first = Download(port,key,build,firstBytes,server,100);
var gamePort = Port();
using (var host = new PersistentCommandSession(true,gamePort,baselineId:first.Hash,bindAddress:IPAddress.Any,sessionKey:first.Ticket))
using (var guest = new PersistentCommandSession(false,gamePort,baselineId:first.Hash,sessionKey:first.Ticket)) {
    host.Start(); guest.Start();
    Require(Wait(() => host.IsConnected && guest.IsConnected), "Ticket-bound gameplay did not connect");
    host.MarkGameplayReady(); guest.MarkGameplayReady();
    Require(Wait(() => host.PeerGameplayReady), "Guest world was not accepted");
    Require(host.AdvanceHostAuthorityFrame()==0 && guest.WaitForAuthorityFrame(0,3000), "First frame not shared");
    Require(guest.SubmitLocalCommand(new byte[] { 7,8,9 }), "Guest command not sent");
    ReceivedAuthorityCommand command=null;
    Require(Wait(() => host.TryDequeueReceived(out command)), "Guest command not accepted by host");
    guest.Dispose();
    Require(Wait(() => !host.IsConnected), "Host did not detect departure");
    Require(command.Payload.SequenceEqual(new byte[]{7,8,9}), "Accepted command lost during guest departure");
}
Console.WriteLine("PASS: downloaded snapshot starts authenticated gameplay; accepted input survives departure");

// Keep the world server alive while the host's independent world changes. A
// later join must obtain that NEW state with a new gameplay ticket/epoch.
var nextBytes = Encoding.UTF8.GetBytes("host continued alone: current world at step 4500");
var next = Download(port,key,build,nextBytes,server,4500);
Require(next.Step==4500 && next.Hash!=first.Hash && next.Ticket!=first.Ticket, "Rejoin reused old world/epoch");
using (var host = new PersistentCommandSession(true,gamePort,baselineId:next.Hash,sessionKey:next.Ticket)) {
    host.Start();
    using var stale = new TcpClient();
    Require(Wait(() => { try { stale.Connect(IPAddress.Loopback,gamePort); return true; } catch(SocketException) { return false; } }), "New host did not listen");
    stale.ReceiveTimeout=3000;
    using var writer = new StreamWriter(stale.GetStream(),new UTF8Encoding(false)){AutoFlush=true};
    using var reader = new StreamReader(stale.GetStream());
    writer.WriteLine(NetworkProtocol.Hello()+"|"+first.Ticket);
    Require(reader.ReadLine().StartsWith("REJECT|"), "Old snapshot ticket attached to new epoch");
    stale.Close();
    using var guest = new PersistentCommandSession(false,gamePort,baselineId:next.Hash,sessionKey:next.Ticket);
    guest.Start();
    Require(Wait(()=>host.IsConnected && guest.IsConnected), "New snapshot could not rejoin");
    host.MarkGameplayReady();guest.MarkGameplayReady();
    Require(Wait(()=>host.PeerGameplayReady), "New baseline not accepted");
    Require(host.AdvanceHostAuthorityFrame()==0 && guest.WaitForAuthorityFrame(0,3000), "Rejoin inherited old frame counters");
}
Console.WriteLine("PASS: rejoin gets current world, new ticket and fresh authority counters; stale ticket rejected");
Console.WriteLine("LIVE WORLD SMOKE PASS (transport lifecycle; game capture tested separately)");
