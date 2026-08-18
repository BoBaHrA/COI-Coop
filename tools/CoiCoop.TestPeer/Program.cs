using System.Net.WebSockets;
using System.Text;

const int ProtocolVersion = 5;
const string ModVersion = "0.0.1";
const string PreviewHello = "COI_COOP_PREVIEW|1";
const string PreviewWelcome = "COI_COOP_PREVIEW_OK|1";

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

static ClientWebSocket CreateWebSocket(string token) {
    var ws = new ClientWebSocket();
    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
    return ws;
}

static async Task SendLineAsync(ClientWebSocket ws, string line, CancellationToken cancellationToken) {
    var payload = Encoding.UTF8.GetBytes(line + "\n");
    await ws.SendAsync(
        new ArraySegment<byte>(payload),
        WebSocketMessageType.Binary,
        true,
        cancellationToken);
}

static async Task RunPreviewLaneAsync(
    int lane,
    string laneName,
    string relayWs,
    string code,
    string token,
    string role,
    CancellationToken cancellationToken) {

    using var ws = CreateWebSocket(token);
    var uri = BuildRelayUri(relayWs, code, role, lane);
    Console.WriteLine($"TEST-PEER {laneName} connecting lane={lane} endpoint={uri.Scheme}://{uri.Authority}{uri.AbsolutePath}");
    await ws.ConnectAsync(uri, cancellationToken);
    Console.WriteLine($"TEST-PEER {laneName} WSS connected lane={lane}");

    await SendLineAsync(ws, PreviewHello, cancellationToken);
    Console.WriteLine($"TEST-PEER {laneName} preview HELLO queued");

    var receiveBuffer = new byte[64 * 1024];
    var pending = new StringBuilder();
    var welcomed = false;
    long previewCount = 0;
    string? lastKind = null;

    while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open) {
        WebSocketReceiveResult result;
        try {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);
        }
        catch (OperationCanceledException) {
            break;
        }

        if (result.MessageType == WebSocketMessageType.Close) {
            Console.WriteLine($"TEST-PEER {laneName} relay closed status={result.CloseStatus} reason={result.CloseStatusDescription}");
            break;
        }

        if (result.MessageType == WebSocketMessageType.Text) {
            var text = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count).Trim();
            if (text.Length > 0) Console.WriteLine($"TEST-PEER {laneName} control {text}");
            continue;
        }

        if (result.MessageType != WebSocketMessageType.Binary || result.Count <= 0) continue;
        pending.Append(Encoding.UTF8.GetString(receiveBuffer, 0, result.Count));

        while (true) {
            var all = pending.ToString();
            var newline = all.IndexOf('\n');
            if (newline < 0) break;

            var line = all[..newline].TrimEnd('\r');
            pending.Clear();
            pending.Append(all[(newline + 1)..]);
            if (line.Length == 0) continue;

            if (line == PreviewWelcome) {
                welcomed = true;
                Console.WriteLine($"TEST-PEER {laneName} preview WELCOME accepted lane={lane}");
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length == 4 && parts[0] == "PREVIEW") {
                previewCount++;
                var kind = parts[2];
                if (previewCount == 1 || previewCount % 120 == 0 || !string.Equals(kind, lastKind, StringComparison.Ordinal)) {
                    Console.WriteLine($"TEST-PEER {laneName} PREVIEW rev={parts[1]} kind={kind} total={previewCount}");
                }
                lastKind = kind;
                continue;
            }

            Console.WriteLine($"TEST-PEER {laneName} ignored line: {line}");
        }
    }

    Console.WriteLine($"TEST-PEER {laneName} stopped welcomed={welcomed} previews={previewCount}");
}

static async Task RunGameplayLaneAsync(
    string relayWs,
    string code,
    string token,
    string role,
    string baselineId,
    CancellationTokenSource lifetime) {

    var cancellationToken = lifetime.Token;
    using var ws = CreateWebSocket(token);
    var uri = BuildRelayUri(relayWs, code, role, 0);

    Console.WriteLine($"TEST-PEER gameplay connecting session={code} role={role} lane=0 endpoint={uri.Scheme}://{uri.Authority}{uri.AbsolutePath}");
    await ws.ConnectAsync(uri, cancellationToken);
    Console.WriteLine("TEST-PEER gameplay WSS connected");

    await SendLineAsync(ws, $"HELLO|{ProtocolVersion}|{ModVersion}", cancellationToken);
    Console.WriteLine("TEST-PEER gameplay HELLO queued");

    var receiveBuffer = new byte[64 * 1024];
    var pending = new StringBuilder();
    var welcomed = false;
    var readySent = false;
    long latestCommitSequence = -1;
    long latestFrame = -1;
    long frameCount = 0;
    long commitCount = 0;

    try {
        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open) {
            WebSocketReceiveResult result;
            try {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken);
            }
            catch (OperationCanceledException) {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close) {
                Console.WriteLine($"TEST-PEER gameplay relay closed status={result.CloseStatus} reason={result.CloseStatusDescription}");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text) {
                var text = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                Console.WriteLine("TEST-PEER gameplay control " + text.Trim());
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Binary || result.Count <= 0) continue;
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
                    Console.WriteLine("TEST-PEER gameplay WELCOME accepted");
                    if (!readySent) {
                        await SendLineAsync(ws, "READY|" + baselineId, cancellationToken);
                        readySent = true;
                        Console.WriteLine("TEST-PEER gameplay READY sent baseline=" + baselineId[..Math.Min(12, baselineId.Length)]);
                    }
                    continue;
                }

                if (type == "PING" && parts.Length == 2) {
                    await SendLineAsync(ws, "PONG|" + parts[1], cancellationToken);
                    continue;
                }

                if (type == "COMMIT" && parts.Length == 6 && long.TryParse(parts[1], out var seq)) {
                    latestCommitSequence = Math.Max(latestCommitSequence, seq);
                    commitCount++;
                    Console.WriteLine($"TEST-PEER gameplay COMMIT seq={seq} frame={parts[2]} origin={parts[3]} id={parts[4]} total={commitCount}");
                    continue;
                }

                if (type == "FRAME" && parts.Length == 2 && long.TryParse(parts[1], out var frame)) {
                    latestFrame = frame;
                    frameCount++;
                    await SendLineAsync(ws, $"PROGRESS|{frame}|{latestCommitSequence}", cancellationToken);
                    if (frame == 0 || frame % 120 == 0) {
                        Console.WriteLine($"TEST-PEER gameplay FRAME frame={frame} seqThrough={latestCommitSequence} total={frameCount}");
                    }
                    continue;
                }

                if (type == "STATE" || type == "PROGRESS") {
                    continue;
                }

                Console.WriteLine("TEST-PEER gameplay ignored line: " + line);
            }
        }
    }
    finally {
        Console.WriteLine($"TEST-PEER gameplay stopped welcomed={welcomed} ready={readySent} latestFrame={latestFrame} seqThrough={latestCommitSequence} commits={commitCount}");
        lifetime.Cancel();
    }
}

var relayWs = RequireEnv("COI_COOP_RELAY_WS");
var code = NormalizeCode(RequireEnv("COI_COOP_SESSION_CODE"));
var token = RequireEnv("COI_COOP_SESSION_TOKEN");
var baselineId = RequireEnv("COI_COOP_BASELINE_SHA256").ToUpperInvariant();
var role = (Environment.GetEnvironmentVariable("COI_COOP_TEST_PEER_ROLE") ?? "client").Trim().ToLowerInvariant();
if (role != "client") {
    throw new InvalidOperationException("This test-peer implementation currently supports client role only.");
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => {
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

var tasks = new[] {
    RunGameplayLaneAsync(relayWs, code, token, role, baselineId, lifetime),
    RunPreviewLaneAsync(1, "preview", relayWs, code, token, role, lifetime.Token),
    RunPreviewLaneAsync(2, "path", relayWs, code, token, role, lifetime.Token),
    RunPreviewLaneAsync(3, "blueprint", relayWs, code, token, role, lifetime.Token),
};

try {
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
}
finally {
    lifetime.Cancel();
}
