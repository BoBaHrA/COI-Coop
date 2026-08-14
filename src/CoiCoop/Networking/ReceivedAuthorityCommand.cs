namespace CoiCoop.Networking;

internal sealed class ReceivedAuthorityCommand {
    public long AuthoritySequence { get; }
    public long AuthorityFrame { get; }
    public string OriginClientId { get; }
    public long ClientCommandId { get; }
    public byte[] Payload { get; }

    public ReceivedAuthorityCommand(
        long authoritySequence,
        string originClientId,
        long clientCommandId,
        byte[] payload)
        : this(authoritySequence, -1, originClientId, clientCommandId, payload) {
    }

    public ReceivedAuthorityCommand(
        long authoritySequence,
        long authorityFrame,
        string originClientId,
        long clientCommandId,
        byte[] payload) {

        AuthoritySequence = authoritySequence;
        AuthorityFrame = authorityFrame;
        OriginClientId = originClientId;
        ClientCommandId = clientCommandId;
        Payload = payload;
    }
}
