using System;

namespace CoiCoop.Networking;

internal static class NetworkProtocol {
    public const int ProtocolVersion = 2;
    public const string ModVersion = "0.0.1";

    public static string Hello() => $"HELLO|{ProtocolVersion}|{ModVersion}";
    public static string Welcome() => $"WELCOME|{ProtocolVersion}|{ModVersion}";
    public static string Ping(long nonce) => $"PING|{nonce}";
    public static string Pong(long nonce) => $"PONG|{nonce}";

    public static string Submit(long clientCommandId, byte[] payload) {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        return "SUBMIT|" + clientCommandId + "|" + Convert.ToBase64String(payload);
    }

    public static string Commit(
        long authoritySequence,
        string originClientId,
        long clientCommandId,
        byte[] payload) {

        if (string.IsNullOrEmpty(originClientId)) throw new ArgumentException("Origin is required.", nameof(originClientId));
        if (originClientId.IndexOf('|') >= 0) throw new ArgumentException("Origin may not contain '|'.", nameof(originClientId));
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        return "COMMIT|"
            + authoritySequence + "|"
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

    public static bool TryReadCommit(
        string line,
        out long authoritySequence,
        out string originClientId,
        out long clientCommandId,
        out byte[] payload) {

        authoritySequence = 0;
        originClientId = null;
        clientCommandId = 0;
        payload = null;

        if (!TrySplit(line, "COMMIT", out var parts)
            || parts.Length != 5
            || !long.TryParse(parts[1], out authoritySequence)
            || string.IsNullOrEmpty(parts[2])
            || !long.TryParse(parts[3], out clientCommandId)) {
            return false;
        }

        originClientId = parts[2];
        return TryDecodePayload(parts[4], out payload);
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
