using System;
using System.Collections.Generic;
using System.IO;

namespace CoiCoop;

/// <summary>
/// Latest-wins payload for composite StaticEntityMassPlacer previews (drag rows,
/// duplicated buildings, blueprints, barriers). A composite placer may temporarily
/// expose only one live preview, so one piece is valid even though ordinary single
/// placement continues to use PlacementGhostWireCodec directly.
/// </summary>
internal static class MultiPlacementGhostWireCodec {
    private const byte Version = 1;
    private const int MaxPieces = 256;
    private const int MaxPiecePayloadBytes = 1024 * 1024;
    private const int MaxTotalPayloadBytes = 16 * 1024 * 1024;

    internal sealed class Piece {
        public Mafi.Core.Prototypes.Proto Prototype { get; }
        public Mafi.Core.TileTransform Transform { get; }

        public Piece(Mafi.Core.Prototypes.Proto prototype, Mafi.Core.TileTransform transform) {
            Prototype = prototype;
            Transform = transform;
        }
    }

    internal sealed class DecodedState {
        public IReadOnlyList<Piece> Pieces { get; }

        public DecodedState(IReadOnlyList<Piece> pieces) {
            Pieces = pieces;
        }
    }

    public static bool TryEncode(
        PlacementPreviewDiscovery.CapturedSet state,
        CommandRoundTripProbe codec,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (state == null || state.Pieces == null || state.Pieces.Count < 1) {
            error = "composite placement preview needs at least one piece";
            return false;
        }
        if (state.Pieces.Count > MaxPieces) {
            error = "composite placement preview has too many pieces: " + state.Pieces.Count;
            return false;
        }
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        try {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) {
                writer.Write(Version);
                writer.Write(state.Pieces.Count);

                for (var i = 0; i < state.Pieces.Count; i++) {
                    var piece = state.Pieces[i];
                    if (piece == null || piece.Prototype == null) {
                        error = "composite placement piece " + i + " is null";
                        return false;
                    }

                    byte[] piecePayload;
                    string pieceError;
                    if (!PlacementGhostWireCodec.TryEncode(
                            piece.Prototype,
                            piece.Transform,
                            codec,
                            out piecePayload,
                            out pieceError)) {

                        error = "composite placement piece " + i + " encode failed: " + pieceError;
                        return false;
                    }
                    if (piecePayload == null
                        || piecePayload.Length == 0
                        || piecePayload.Length > MaxPiecePayloadBytes) {
                        error = "composite placement piece " + i + " payload size is invalid";
                        return false;
                    }

                    writer.Write(piecePayload.Length);
                    writer.Write(piecePayload);
                }

                writer.Flush();
                if (stream.Length > MaxTotalPayloadBytes) {
                    error = "composite placement payload is unexpectedly large: " + stream.Length;
                    return false;
                }
                payload = stream.ToArray();
                return true;
            }
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TryDecode(
        byte[] payload,
        CommandRoundTripProbe codec,
        out DecodedState state,
        out string error) {

        state = null;
        error = null;
        if (payload == null || payload.Length == 0) {
            error = "composite placement payload is empty";
            return false;
        }
        if (payload.Length > MaxTotalPayloadBytes) {
            error = "composite placement payload is too large: " + payload.Length;
            return false;
        }
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        try {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream)) {
                var version = reader.ReadByte();
                if (version != Version) {
                    error = "unsupported composite placement payload version " + version;
                    return false;
                }

                var pieceCount = reader.ReadInt32();
                if (pieceCount < 1 || pieceCount > MaxPieces) {
                    error = "invalid composite placement piece count " + pieceCount;
                    return false;
                }

                var pieces = new List<Piece>(pieceCount);
                for (var i = 0; i < pieceCount; i++) {
                    var length = reader.ReadInt32();
                    if (length <= 0
                        || length > MaxPiecePayloadBytes
                        || length > stream.Length - stream.Position) {
                        error = "invalid composite placement piece payload length " + length + " at index " + i;
                        return false;
                    }

                    var piecePayload = reader.ReadBytes(length);
                    PlacementGhostWireCodec.DecodedState decoded;
                    string pieceError;
                    if (!PlacementGhostWireCodec.TryDecode(
                            piecePayload,
                            codec,
                            out decoded,
                            out pieceError)) {

                        error = "composite placement piece " + i + " decode failed: " + pieceError;
                        return false;
                    }
                    pieces.Add(new Piece(decoded.Prototype, decoded.Transform));
                }

                if (stream.Position != stream.Length) {
                    error = "composite placement payload has trailing bytes";
                    return false;
                }

                state = new DecodedState(pieces);
                return true;
            }
        }
        catch (EndOfStreamException) {
            error = "composite placement payload ended unexpectedly";
            return false;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
