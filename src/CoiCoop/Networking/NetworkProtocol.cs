using System;

namespace CoiCoop.Networking;

internal static class NetworkProtocol {
    public const int ProtocolVersion = 3;
    public const string ModVersion = "0.0.1";

    public static string Hello() => $"HELLO|{ProtocolVersion}|{ModVersion}";
    public static string Welcome() => $"WELCOME|{ProtocolVersion}|{ModVersion}";
    public static string Ping(long nonce) => $"PING|{nonce}";
    public static string Pong(long nonce) => $"PONG|{nonce}";
    public static string Frame(long authorityFrame) => $"FRAME|{authorityFrame}";
    public static string Progress(long authorityFrame, long appliedThroughSequence)
        => $"PROGRESS|{authorityFrame}|{appliedThroughSequence}";

    public static string Submit(long clientCommandId, byte[] payload) {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        return "SUBMIT|" + clientCommandId + "|" + Convert.ToBase64String(payload);
    }

    // Compatibility overload used by the standalone transport smoke tests.
    public static string Commit(
        long authoritySequence,
        string originClientId,
        long clientCommandId,
        byte[] payload) {

        return Commit(authoritySequence, -1, originClientId, clientCommandId, payload);
    }

    public static string Commit(
        long authoritySequence,
        long authorityFrame,
        string originClientId,
        long clientCommandId,
        byte[] payload) {

        if (string.IsNullOrEmpty(originClientId)) throw new ArgumentException("Origin is required.", nameof(originClientId));
        if (originClientId.IndexOf('|') >= 0) throw new ArgumentException("Origin may not contain '|'.", nameof(originClientId));
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        return "COMMIT|"
            + authoritySequence + "|"
            + authorityFrame + "|"
            + originClientId + "|"
            + clientCommandId + "|"
            + Convert.ToBase64String(payload);
    }

    public static bool IsCompatibleHello(string line) {
        if (!TrySplit(line, "HELLO", out var parts) || parts.Length != 3) {
            return false;
        }

        return int.TryParse(parts[1], out var protocol)
            && protocol == ProtocolVersion
            && string.Equals(parts[2], ModVersion, StringComparison.Ordinal);
    }

    public static bool IsCompatibleWelcome(string line) {
        if (!TrySplit(line, "WELCOME", out var parts) || parts.Length != 3) {
            return false;
        }

        return int.TryParse(parts[1], out var protocol)
            && protocol == ProtocolVersion
            && string.Equals(parts[2], ModVersion, StringComparison.Ordinal);
    }

    public static bool TryReadPing(string line, out long nonce) => TryReadNonce(line, "PING", out nonce);
    public static bool TryReadPong(string line, out long nonce) => TryReadNonce(line, "PONG", out nonce);
    public static bool TryReadFrame(string line, out long authorityFrame) => TryReadNonce(line, "FRAME", out authorityFrame);

    public static bool TryReadProgress(
        string line,
        out long authorityFrame,
        out long appliedThroughSequence) {

        authorityFrame = -1;
        appliedThroughSequence = -1;

        return TrySplit(line, "PROGRESS", out var parts)
            && parts.Length == 3
            && long.TryParse(parts[1], out authorityFrame)
            && long.TryParse(parts[2], out appliedThroughSequence);
    }

    public static bool TryReadSubmit(string line, out long clientCommandId, out byte[] payload) {
        clientCommandId = 0;
        payload = null;

        if (!TrySplit(line, "SUBMIT", out var parts)
            || parts.Length != 3
            || !long.TryParse(parts[1], out clientCommandId)) {
            return false;
        }

        return TryDecodePayload(parts[2], out payload);
    }

    // Compatibility overload used by existing callers that do not inspect frames.
    public static bool TryReadCommit(
        string line,
        out long authoritySequence,
        out string originClientId,
        out long clientCommandId,
        out byte[] payload) {

        long ignoredAuthorityFrame;
        return TryReadCommit(
            line,
            out authoritySequence,
            out ignoredAuthorityFrame,
            out originClientId,
            out clientCommandId,
            out payload);
    }

    public static bool TryReadCommit(
        string line,
        out long authoritySequence,
        out long authorityFrame,
        out string originClientId,
        out long clientCommandId,
        out byte[] payload) {

        authoritySequence = 0;
        authorityFrame = -1;
        originClientId = null;
        clientCommandId = 0;
        payload = null;

        if (!TrySplit(line, "COMMIT", out var parts)
            || parts.Length != 6
            || !long.TryParse(parts[1], out authoritySequence)
            || !long.TryParse(parts[2], out authorityFrame)
            || string.IsNullOrEmpty(parts[3])
            || !long.TryParse(parts[4], out clientCommandId)) {
            return false;
        }

        originClientId = parts[3];
        return TryDecodePayload(parts[5], out payload);
    }

    private static bool TryReadNonce(string line, string expectedType, out long nonce) {
        nonce = 0;
        return TrySplit(line, expectedType, out var parts)
            && parts.Length == 2
            && long.TryParse(parts[1], out nonce);
    }

    private static bool TryDecodePayload(string encoded, out byte[] payload) {
        payload = null;
        try {
            payload = Convert.FromBase64String(encoded ?? string.Empty);
            return true;
        }
        catch (FormatException) {
            return false;
        }
    }

    private static bool TrySplit(string line, string expectedType, out string[] parts) {
        parts = (line ?? string.Empty).Split('|');
        return parts.Length > 0 && string.Equals(parts[0], expectedType, StringComparison.Ordinal);
    }
}
