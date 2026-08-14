namespace CoiCoop.Networking;

internal sealed class ReceivedAuthorityCommand {
    public long AuthoritySequence { get; }
    public string OriginClientId { get; }
    public long ClientCommandId { get; }
    public byte[] Payload { get; }

    public ReceivedAuthorityCommand(
        long authoritySequence,
        string originClientId,
        long clientCommandId,
        byte[] payload) {

        AuthoritySequence = authoritySequence;
        OriginClientId = originClientId;
        ClientCommandId = clientCommandId;
        Payload = payload;
    }
}
