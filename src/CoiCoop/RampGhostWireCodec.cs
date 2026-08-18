using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CoiCoop;

/// <summary>
/// Latest-wins payload for modular vehicle ramp previews. Each ramp piece reuses
/// the already-proven building ghost codec (Proto + TileTransform).
/// </summary>
internal static class RampGhostWireCodec {
    private const byte Version = 1;
    private const int MaxPieces = 128;
    private const int MaxStringBytes = 256;
    private const int MaxPiecePayloadBytes = 1024 * 1024;
    private const int MaxTotalPayloadBytes = 8 * 1024 * 1024;

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
        public string ControllerState { get; }

        public DecodedState(IReadOnlyList<Piece> pieces, string controllerState) {
            Pieces = pieces;
            ControllerState = controllerState ?? string.Empty;
        }
    }

    public static bool TryEncode(
        RampPreviewDiscovery.CapturedState state,
        CommandRoundTripProbe codec,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (state == null || state.Pieces == null || state.Pieces.Count == 0) {
            error = "ramp preview state has no pieces";
            return false;
        }
        if (state.Pieces.Count > MaxPieces) {
            error = "ramp preview has too many pieces: " + state.Pieces.Count;
            return false;
        }
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        try {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
                writer.Write(Version);
                WriteString(writer, state.ControllerState ?? string.Empty);
                writer.Write(state.Pieces.Count);

                for (var i = 0; i < state.Pieces.Count; i++) {
                    var piece = state.Pieces[i];
                    if (piece == null || piece.Prototype == null) {
                        error = "ramp piece " + i + " is null";
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

                        error = "ramp piece " + i + " encode failed: " + pieceError;
                        return false;
                    }
                    if (piecePayload == null
                        || piecePayload.Length == 0
                        || piecePayload.Length > MaxPiecePayloadBytes) {
                        error = "ramp piece " + i + " payload size is invalid";
                        return false;
                    }

                    writer.Write(piecePayload.Length);
                    writer.Write(piecePayload);
                }

                writer.Flush();
                if (stream.Length > MaxTotalPayloadBytes) {
                    error = "ramp payload is unexpectedly large: " + stream.Length;
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
            error = "ramp payload is empty";
            return false;
        }
        if (payload.Length > MaxTotalPayloadBytes) {
            error = "ramp payload is too large: " + payload.Length;
            return false;
        }
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        try {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8)) {
                var version = reader.ReadByte();
                if (version != Version) {
                    error = "unsupported ramp payload version " + version;
                    return false;
                }

                var controllerState = ReadString(reader, stream);
                var pieceCount = reader.ReadInt32();
                if (pieceCount <= 0 || pieceCount > MaxPieces) {
                    error = "invalid ramp piece count " + pieceCount;
                    return false;
                }

                var pieces = new List<Piece>(pieceCount);
                for (var i = 0; i < pieceCount; i++) {
                    var length = reader.ReadInt32();
                    if (length <= 0
                        || length > MaxPiecePayloadBytes
                        || length > stream.Length - stream.Position) {
                        error = "invalid ramp piece payload length " + length + " at index " + i;
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

                        error = "ramp piece " + i + " decode failed: " + pieceError;
                        return false;
                    }
                    pieces.Add(new Piece(decoded.Prototype, decoded.Transform));
                }

                if (stream.Position != stream.Length) {
                    error = "ramp payload has trailing bytes";
                    return false;
                }

                state = new DecodedState(pieces, controllerState);
                return true;
            }
        }
        catch (EndOfStreamException) {
            error = "ramp payload ended unexpectedly";
            return false;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static void WriteString(BinaryWriter writer, string value) {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > MaxStringBytes) {
            throw new InvalidDataException("ramp controller state is too long");
        }
        writer.Write(bytes.Length);
        if (bytes.Length > 0) writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, Stream stream) {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaxStringBytes || length > stream.Length - stream.Position) {
            throw new InvalidDataException("invalid ramp controller state length " + length);
        }
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}
