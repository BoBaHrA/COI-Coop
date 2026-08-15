using System;

namespace CoiCoop.Networking;

/// <summary>
/// Ephemeral presentation-only state. Preview traffic is deliberately separate
/// from authoritative simulation commands: only the newest revision matters.
/// </summary>
internal sealed class PlacementPreviewState {
    public long Revision { get; }
    public string Kind { get; }
    public byte[] Payload { get; }

    public PlacementPreviewState(long revision, string kind, byte[] payload) {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Preview kind is required.", nameof(kind));
        if (kind.IndexOf('|') >= 0) throw new ArgumentException("Preview kind may not contain '|'.", nameof(kind));

        Revision = revision;
        Kind = kind;
        Payload = payload ?? Array.Empty<byte>();
    }
}
