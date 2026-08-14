using System;

namespace CoiCoop.Networking;

internal enum CommandConflictMode : byte {
    /// <summary>
    /// Preserve every command in authoritative host order.
    /// Use for order-sensitive actions such as toggles.
    /// </summary>
    Ordered = 0,

    /// <summary>
    /// Within an uncommitted authority batch, only the newest command for the
    /// same conflict key needs to survive. Use for state-setting intents such
    /// as a ship destination.
    /// </summary>
    Supersede = 1,
}

internal sealed class AuthorityCommandEnvelope {
    public long AuthoritySequence { get; }
    public string ClientId { get; }
    public long ClientCommandId { get; }
    public string ConflictKey { get; }
    public CommandConflictMode ConflictMode { get; }
    public byte[] Payload { get; }

    public AuthorityCommandEnvelope(
        long authoritySequence,
        string clientId,
        long clientCommandId,
        string conflictKey,
        CommandConflictMode conflictMode,
        byte[] payload) {

        if (authoritySequence < 0) throw new ArgumentOutOfRangeException(nameof(authoritySequence));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client id is required.", nameof(clientId));
        if (clientCommandId < 0) throw new ArgumentOutOfRangeException(nameof(clientCommandId));
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (conflictMode == CommandConflictMode.Supersede && string.IsNullOrWhiteSpace(conflictKey)) {
            throw new ArgumentException("Superseding commands require a conflict key.", nameof(conflictKey));
        }

        AuthoritySequence = authoritySequence;
        ClientId = clientId;
        ClientCommandId = clientCommandId;
        ConflictKey = conflictKey;
        ConflictMode = conflictMode;
        Payload = payload;
    }

    public string DeduplicationKey => ClientId + ":" + ClientCommandId;
}
