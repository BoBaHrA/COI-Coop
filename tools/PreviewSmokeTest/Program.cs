using System.Net;
using System.Net.Sockets;
using System.Text;
using CoiCoop.Networking;

static int GetFreePort() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally {
        listener.Stop();
    }
}

static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000) {
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs) {
        if (condition()) return true;
        Thread.Sleep(10);
    }
    return false;
}

var port = GetFreePort();
var hostLog = new List<string>();
var clientLog = new List<string>();

using var host = new PlacementPreviewSession(true, port, hostLog.Add);
using var client = new PlacementPreviewSession(false, port, clientLog.Add);
host.Start();
client.Start();

if (!WaitUntil(() => host.IsConnected && client.IsConnected)) {
    Console.Error.WriteLine("FAIL: preview sidecar did not connect.");
    Console.Error.WriteLine(string.Join(Environment.NewLine, hostLog));
    Console.Error.WriteLine(string.Join(Environment.NewLine, clientLog));
    return 1;
}

// Publish several intermediate positions without waiting. Only the newest state
// is important for a cursor/blueprint preview.
host.Publish("PLACEMENT", Encoding.UTF8.GetBytes("x=10;y=10;rot=0"));
host.Publish("PLACEMENT", Encoding.UTF8.GetBytes("x=11;y=10;rot=0"));
var finalHostRevision = host.Publish("PLACEMENT", Encoding.UTF8.GetBytes("x=12;y=10;rot=1"));

PlacementPreviewState? clientState = null;
if (!WaitUntil(() => {
        if (!client.TryTakeLatestPeerState(out var state)) return false;
        clientState = state;
        return state.Revision >= finalHostRevision;
    })) {
    Console.Error.WriteLine("FAIL: client did not receive latest host preview state.");
    return 2;
}

if (clientState == null
    || clientState.Kind != "PLACEMENT"
    || Encoding.UTF8.GetString(clientState.Payload) != "x=12;y=10;rot=1") {
    Console.Error.WriteLine("FAIL: latest host preview payload was not preserved.");
    return 3;
}

var clientRevision = client.Publish("PLACEMENT", Encoding.UTF8.GetBytes("x=2;y=3;rot=2"));
PlacementPreviewState? hostState = null;
if (!WaitUntil(() => {
        if (!host.TryTakeLatestPeerState(out var state)) return false;
        hostState = state;
        return state.Revision >= clientRevision;
    })) {
    Console.Error.WriteLine("FAIL: host did not receive client preview state.");
    return 4;
}

if (hostState == null || Encoding.UTF8.GetString(hostState.Payload) != "x=2;y=3;rot=2") {
    Console.Error.WriteLine("FAIL: client-to-host preview payload mismatch.");
    return 5;
}

// Reliable adapter messages must survive even when a newer preview is published
// immediately afterwards.
var reliablePayload = Encoding.UTF8.GetBytes("entity=1457;product=sand");
host.PublishReliable("SANDBOX_SOURCE", reliablePayload);
host.Publish("PLACEMENT", Encoding.UTF8.GetBytes("x=99;y=99;rot=3"));

PlacementPreviewState? reliableState = null;
if (!WaitUntil(() => client.TryDequeueReliablePeerState(out reliableState))) {
    Console.Error.WriteLine("FAIL: reliable sidecar state did not arrive.");
    return 6;
}

if (reliableState == null
    || reliableState.Kind != "SANDBOX_SOURCE"
    || Encoding.UTF8.GetString(reliableState.Payload) != "entity=1457;product=sand") {
    Console.Error.WriteLine("FAIL: reliable sidecar payload mismatch.");
    return 7;
}

var clearRevision = client.Clear();
PlacementPreviewState? clearState = null;
if (!WaitUntil(() => {
        if (!host.TryTakeLatestPeerState(out var state)) return false;
        clearState = state;
        return state.Revision >= clearRevision;
    })) {
    Console.Error.WriteLine("FAIL: preview clear did not arrive.");
    return 8;
}

if (clearState == null || clearState.Kind != "NONE" || clearState.Payload.Length != 0) {
    Console.Error.WriteLine("FAIL: preview clear payload was invalid.");
    return 9;
}

Console.WriteLine($"PASS: latest-wins preview + reliable sidecar lane worked bidirectionally on 127.0.0.1:{port}");
return 0;
