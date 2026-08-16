using System;
using System.IO;
using System.Text;
using Mafi.Core;
using Mafi.Core.Prototypes;

namespace CoiCoop;

/// <summary>
/// Compact presentation-only payload for a remote placement ghost.
/// Prototype references and TileTransform are serialized through COI's own
/// serializer so prototype IDs remain stable across the two processes.
/// </summary>
internal static class PlacementGhostWireCodec {
    private const byte Version = 1;
    private const int MaxTypeNameBytes = 512;
    private const int MaxValuePayloadBytes = 64 * 1024;

    internal sealed class DecodedState {
        public Proto Prototype { get; }
        public TileTransform Transform { get; }

        public DecodedState(Proto prototype, TileTransform transform) {
            Prototype = prototype;
            Transform = transform;
        }
    }

    public static bool TryEncode(
        Proto prototype,
        TileTransform transform,
        CommandRoundTripProbe codec,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (prototype == null) {
            error = "prototype is null";
            return false;
        }
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        var prototypeType = prototype.GetType();
        var typeName = prototypeType.AssemblyQualifiedName ?? prototypeType.FullName;
        if (string.IsNullOrWhiteSpace(typeName)) {
            error = "prototype runtime type name is unavailable";
            return false;
        }

        var typeNameBytes = Encoding.UTF8.GetBytes(typeName);
        if (typeNameBytes.Length > MaxTypeNameBytes) {
            error = "prototype runtime type name is too long";
            return false;
        }

        byte[] protoPayload;
        if (!codec.TrySerializeValue(prototype, prototypeType, out protoPayload, out error)) {
            error = "prototype serialization failed: " + error;
            return false;
        }

        byte[] transformPayload;
        if (!codec.TrySerializeValue(transform, typeof(TileTransform), out transformPayload, out error)) {
            error = "transform serialization failed: " + error;
            return false;
        }

        if (protoPayload.Length > MaxValuePayloadBytes || transformPayload.Length > MaxValuePayloadBytes) {
            error = "placement ghost value payload is unexpectedly large";
            return false;
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
            writer.Write(Version);
            writer.Write(typeNameBytes.Length);
            writer.Write(typeNameBytes);
            writer.Write(protoPayload.Length);
            writer.Write(protoPayload);
            writer.Write(transformPayload.Length);
            writer.Write(transformPayload);
            writer.Flush();
            payload = stream.ToArray();
            return true;
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
            error = "placement ghost payload is empty";
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
                    error = "unsupported placement ghost payload version " + version;
                    return false;
                }

                var typeNameLength = reader.ReadInt32();
                if (typeNameLength <= 0 || typeNameLength > MaxTypeNameBytes || typeNameLength > stream.Length - stream.Position) {
                    error = "invalid prototype type-name length " + typeNameLength;
                    return false;
                }
                var typeName = Encoding.UTF8.GetString(reader.ReadBytes(typeNameLength));

                var protoLength = reader.ReadInt32();
                if (protoLength <= 0 || protoLength > MaxValuePayloadBytes || protoLength > stream.Length - stream.Position) {
                    error = "invalid prototype payload length " + protoLength;
                    return false;
                }
                var protoPayload = reader.ReadBytes(protoLength);

                var transformLength = reader.ReadInt32();
                if (transformLength <= 0 || transformLength > MaxValuePayloadBytes || transformLength > stream.Length - stream.Position) {
                    error = "invalid transform payload length " + transformLength;
                    return false;
                }
                var transformPayload = reader.ReadBytes(transformLength);
                if (stream.Position != stream.Length) {
                    error = "placement ghost payload has trailing bytes";
                    return false;
                }

                var prototypeType = ResolveType(typeName);
                if (prototypeType == null || !typeof(Proto).IsAssignableFrom(prototypeType)) {
                    error = "prototype type could not be resolved: " + typeName;
                    return false;
                }

                object decodedPrototype;
                if (!codec.TryDeserializeValue(protoPayload, prototypeType, out decodedPrototype, out error)) {
                    error = "prototype deserialization failed: " + error;
                    return false;
                }
                var prototype = decodedPrototype as Proto;
                if (prototype == null) {
                    error = "decoded prototype is not a Proto";
                    return false;
                }

                object decodedTransform;
                if (!codec.TryDeserializeValue(transformPayload, typeof(TileTransform), out decodedTransform, out error)) {
                    error = "transform deserialization failed: " + error;
                    return false;
                }
                if (!(decodedTransform is TileTransform transform)) {
                    error = "decoded placement transform has unexpected type";
                    return false;
                }

                state = new DecodedState(prototype, transform);
                return true;
            }
        }
        catch (EndOfStreamException) {
            error = "placement ghost payload ended unexpectedly";
            return false;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static Type ResolveType(string assemblyQualifiedOrFullName) {
        if (string.IsNullOrWhiteSpace(assemblyQualifiedOrFullName)) return null;

        try {
            var direct = Type.GetType(assemblyQualifiedOrFullName, false);
            if (direct != null) return direct;
        }
        catch { }

        var comma = assemblyQualifiedOrFullName.IndexOf(',');
        var fullName = comma > 0
            ? assemblyQualifiedOrFullName.Substring(0, comma).Trim()
            : assemblyQualifiedOrFullName;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                var found = assembly.GetType(fullName, false);
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }
}
