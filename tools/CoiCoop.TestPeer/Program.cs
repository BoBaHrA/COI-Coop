using System.Net.WebSockets;
using System.Text;

const int ProtocolVersion = 4;
const string ModVersion = "0.0.1";

static string RequireEnv(string name) {
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) {
        throw new InvalidOperationException($"Missing required environment variable: {name}");
    }
    return value.Trim();
}

static string NormalizeCode(string value) {
    var raw = new string((value ?? string.Empty)
        .ToUpperInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());
    if (raw.Length != 8) throw new InvalidOperationException("Session code must contain 8 letters/digits.");
    return raw[..4] + "-" + raw[4..];
}

static Uri BuildRelayUri(string relayWsBase, string code, string role, int lane) {
    var builder = new UriBuilder(relayWsBase);
    var prefix = string.IsNullOrEmpty(builder.Query)
        ? string.Empty
        : builder.Query.TrimStart('?') + "&";
    builder.Query = prefix
        + "code=" + Uri.EscapeDataString(code)
        + "&role=" + Uri.EscapeDataString(role)
        + "&lane=" + lane;
    return builder.Uri;
}

static async Task SendLineAsync(ClientWebSocket ws, string line, CancellationToken cancellationToken) {
    var payload = Encoding.UTF8.GetBytes(line + "\n");
    await ws.SendAsync(
        new ArraySegment<byte>(payload),
        WebSocketMessageType.Binary,
        true,
        cancellationToken);
}

var relayWs = RequireEnv("COI_COOP_RELAY_WS");
var code = NormalizeCode(RequireEnv("COI_COOP_SESSION_CODE"));
var token = RequireEnv("COI_COOP_SESSION_TOKEN");
var role = (Environment.GetEnvironmentVariable("COI_COOP_TEST_PEER_ROLE") ?? "client").Trim().ToLowerInvariant();
if (role != "client") {
    throw new InvalidOperationException("This first test-peer implementation supports client role only.");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => {
    eventArgs.Cancel = true;
    cts.Cancel();
};

using var ws = new ClientWebSocket();
ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
var uri = BuildRelayUri(relayWs, code, role, 0);

Console.WriteLine($"TEST-PEER connecting session={code} role={role} lane=0 endpoint={uri.Scheme}://{uri.Authority}{uri.AbsolutePath}");
await ws.ConnectAsync(uri, cts.Token);
Console.WriteLine("TEST-PEER WSS connected");

await SendLineAsync(ws, $"HELLO|{ProtocolVersion}|{ModVersion}", cts.Token);
Console.WriteLine("TEST-PEER HELLO queued");

var receiveBuffer = new byte[64 * 1024];
var pending = new StringBuilder();
var welcomed = false;
var readySent = false;
long latestCommitSequence = -1;
long latestFrame = -1;
long frameCount = 0;
long commitCount = 0;

while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open) {
    WebSocketReceiveResult result;
    try {
        result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
    }
    catch (OperationCanceledException) {
        break;
    }

    if (result.MessageType == WebSocketMessageType.Close) {
        Console.WriteLine($"TEST-PEER relay closed status={result.CloseStatus} reason={result.CloseStatusDescription}");
        break;
    }

    if (result.MessageType == WebSocketMessageType.Text) {
        var text = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
        Console.WriteLine("TEST-PEER control " + text.Trim());
        continue;
    }

    if (result.MessageType != WebSocketMessageType.Binary || result.Count <= 0) {
        continue;
    }

    pending.Append(Encoding.UTF8.GetString(receiveBuffer, 0, result.Count));

    while (true) {
        var all = pending.ToString();
        var newline = all.IndexOf('\n');
        if (newline < 0) break;

        var line = all[..newline].TrimEnd('\r');
        pending.Clear();
        pending.Append(all[(newline + 1)..]);
        if (line.Length == 0) continue;

        var parts = line.Split('|');
        var type = parts[0];

        if (type == "WELCOME") {
            if (parts.Length != 3
                || parts[1] != ProtocolVersion.ToString()
                || parts[2] != ModVersion) {
                throw new InvalidOperationException("Host returned incompatible WELCOME: " + line);
            }
            welcomed = true;
            Console.WriteLine("TEST-PEER WELCOME accepted");
            if (!readySent) {
                await SendLineAsync(ws, "READY", cts.Token);
                readySent = true;
                Console.WriteLine("TEST-PEER READY sent");
            }
            continue;
        }

        if (type == "PING" && parts.Length == 2) {
            await SendLineAsync(ws, "PONG|" + parts[1], cts.Token);
            continue;
        }

        if (type == "COMMIT" && parts.Length == 6 && long.TryParse(parts[1], out var seq)) {
            latestCommitSequence = Math.Max(latestCommitSequence, seq);
            commitCount++;
            Console.WriteLine($"TEST-PEER COMMIT seq={seq} frame={parts[2]} origin={parts[3]} id={parts[4]} total={commitCount}");
            continue;
        }

        if (type == "FRAME" && parts.Length == 2 && long.TryParse(parts[1], out var frame)) {
            latestFrame = frame;
            frameCount++;
            await SendLineAsync(ws, $"PROGRESS|{frame}|{latestCommitSequence}", cts.Token);
            if (frame == 0 || frame % 120 == 0) {
                Console.WriteLine($"TEST-PEER FRAME frame={frame} seqThrough={latestCommitSequence} total={frameCount}");
            }
            continue;
        }

        if (type == "STATE") {
            // Intentionally do not fabricate a world fingerprint. The real game
            // treats absence of a peer state probe as unknown, not as mismatch.
            continue;
        }

        if (type == "PROGRESS") {
            // The real host reports its own progress to the peer as well. This is
            // expected protocol traffic; the headless client has no simulation
            // state to compare it with, so silently ignore it instead of flooding
            // diagnostics with one line per authority frame.
            continue;
        }

        Console.WriteLine("TEST-PEER ignored line: " + line);
    }
}

Console.WriteLine($"TEST-PEER stopped welcomed={welcomed} ready={readySent} latestFrame={latestFrame} seqThrough={latestCommitSequence} commits={commitCount}");
