using System.Net;
using System.Net.Sockets;
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

var port = GetFreePort();
Exception? hostError = null;
bool hostSucceeded = false;

var hostThread = new Thread(() => {
    try {
        hostSucceeded = LocalTransportProbe.HostOnce(port, acceptTimeoutMs: 5000);
    }
    catch (Exception ex) {
        hostError = ex;
    }
}) {
    IsBackground = true,
    Name = "COI-Coop smoke-test host"
};

hostThread.Start();
Thread.Sleep(100);

var clientSucceeded = LocalTransportProbe.ClientOnce(port);
var hostStopped = hostThread.Join(TimeSpan.FromSeconds(5));

if (!hostStopped) {
    Console.Error.WriteLine("FAIL: host did not stop after the probe.");
    return 1;
}

if (hostError is not null) {
    Console.Error.WriteLine("FAIL: host threw an exception:");
    Console.Error.WriteLine(hostError);
    return 2;
}

if (!hostSucceeded) {
    Console.Error.WriteLine("FAIL: host did not complete the handshake/PING exchange.");
    return 3;
}

if (!clientSucceeded) {
    Console.Error.WriteLine("FAIL: client rejected handshake or PONG did not match.");
    return 4;
}

Console.WriteLine($"PASS: COI-Coop transport handshake + bidirectional ping succeeded on 127.0.0.1:{port}");
return 0;
