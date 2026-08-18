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

static bool WaitUntil(Func<bool> predicate, int timeoutMs = 5000) {
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs) {
        if (predicate()) return true;
        Thread.Sleep(10);
    }
    return false;
}

static int TestMatchingBaseline() {
    var original = Environment.GetEnvironmentVariable("COI_COOP_BASELINE_SHA256");
    var baseline = new string('A', 64);
    var port = GetFreePort();
    var hostLog = new List<string>();
    var clientLog = new List<string>();

    try {
        Environment.SetEnvironmentVariable("COI_COOP_BASELINE_SHA256", baseline);
        using var host = new PersistentCommandSession(true, port, hostLog.Add);
        using var client = new PersistentCommandSession(false, port, clientLog.Add);
        host.Start();
        client.Start();

        if (!WaitUntil(() => host.IsConnected && client.IsConnected)) {
            Console.Error.WriteLine("FAIL: matching-baseline peers did not connect.");
            return 10;
        }

        host.MarkGameplayReady();
        client.MarkGameplayReady();
        if (!WaitUntil(() => host.PeerGameplayReady)) {
            Console.Error.WriteLine("FAIL: host rejected a client with the same verified baseline.");
            Console.Error.WriteLine(string.Join(Environment.NewLine, hostLog));
            return 11;
        }

        Console.WriteLine("PASS: matching snapshot baseline allows gameplay READY.");
        return 0;
    }
    finally {
        Environment.SetEnvironmentVariable("COI_COOP_BASELINE_SHA256", original);
    }
}

static int TestMismatchingBaseline() {
    var original = Environment.GetEnvironmentVariable("COI_COOP_BASELINE_SHA256");
    var hostBaseline = new string('B', 64);
    var clientBaseline = new string('C', 64);
    var port = GetFreePort();
    var hostLog = new List<string>();
    var clientLog = new List<string>();

    try {
        Environment.SetEnvironmentVariable("COI_COOP_BASELINE_SHA256", hostBaseline);
        using var host = new PersistentCommandSession(true, port, hostLog.Add);

        Environment.SetEnvironmentVariable("COI_COOP_BASELINE_SHA256", clientBaseline);
        using var client = new PersistentCommandSession(false, port, clientLog.Add);

        host.Start();
        client.Start();
        if (!WaitUntil(() => host.IsConnected && client.IsConnected)) {
            Console.Error.WriteLine("FAIL: mismatching-baseline peers did not reach protocol connection.");
            return 20;
        }

        host.MarkGameplayReady();
        client.MarkGameplayReady();

        if (!WaitUntil(
                () => hostLog.Any(line => line.Contains("BASELINE MISMATCH", StringComparison.Ordinal)),
                3000)) {
            Console.Error.WriteLine("FAIL: host did not report the mismatching snapshot baseline.");
            Console.Error.WriteLine(string.Join(Environment.NewLine, hostLog));
            return 21;
        }

        if (host.PeerGameplayReady) {
            Console.Error.WriteLine("FAIL: host accepted gameplay READY from a different snapshot baseline.");
            return 22;
        }

        if (host.AdvanceHostAuthorityFrame() != -1) {
            Console.Error.WriteLine("FAIL: host advanced authority despite baseline mismatch.");
            return 23;
        }

        Console.WriteLine("PASS: stale/different snapshot baseline is fail-closed before gameplay frame 0.");
        return 0;
    }
    finally {
        Environment.SetEnvironmentVariable("COI_COOP_BASELINE_SHA256", original);
    }
}

var match = TestMatchingBaseline();
if (match != 0) return match;

var mismatch = TestMismatchingBaseline();
if (mismatch != 0) return mismatch;

Console.WriteLine("BASELINE READY SMOKE PASS");
return 0;
