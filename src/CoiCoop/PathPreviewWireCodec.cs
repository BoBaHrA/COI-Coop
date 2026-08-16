using System;
using System.IO;
using System.Text;
using Mafi;

namespace CoiCoop;

/// <summary>
/// Compact latest-wins payload for the game's native multi-stage PreviewRequest.
/// The request itself and relative height are serialized with COI's own serializer
/// so prototype/plan references remain stable between peers.
/// </summary>
internal static class PathPreviewWireCodec {
    private const byte Version = 1;
    private const int MaxStringBytes = 512;
    private const int MaxValuePayloadBytes = 1024 * 1024;

    internal sealed class DecodedState {
        public string Family { get; }
        public bool IsContinuation { get; }
        public object Request { get; }
        public Type RequestType { get; }
        public ThicknessTilesI RelativeHeight { get; }
        public string ControllerState { get; }

        public DecodedState(
            string family,
            bool isContinuation,
            object request,
            Type requestType,
            ThicknessTilesI relativeHeight,
            string controllerState) {

            Family = family;
            IsContinuation = isContinuation;
            Request = request;
            RequestType = requestType;
            RelativeHeight = relativeHeight;
            ControllerState = controllerState;
        }
    }

    public static bool TryEncode(
        PathPreviewDiscovery.CapturedState state,
        CommandRoundTripProbe codec,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (state == null || state.Request == null || state.RequestType == null) {
            error = "path preview state/request is null";
            return false;
        }
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        var familyBytes = Encoding.UTF8.GetBytes(state.Family ?? string.Empty);
        var typeName = state.RequestType.AssemblyQualifiedName ?? state.RequestType.FullName ?? string.Empty;
        var typeNameBytes = Encoding.UTF8.GetBytes(typeName);
        var controllerStateBytes = Encoding.UTF8.GetBytes(state.ControllerState ?? string.Empty);
        if (familyBytes.Length == 0 || familyBytes.Length > MaxStringBytes
            || typeNameBytes.Length == 0 || typeNameBytes.Length > MaxStringBytes
            || controllerStateBytes.Length > MaxStringBytes) {
            error = "path preview metadata string is invalid or too long";
            return false;
        }

        byte[] requestPayload;
        if (!codec.TrySerializeValue(state.Request, state.RequestType, out requestPayload, out error)) {
            error = "PreviewRequest serialization failed: " + error;
            return false;
        }

        byte[] heightPayload;
        if (!codec.TrySerializeValue(state.RelativeHeight, typeof(ThicknessTilesI), out heightPayload, out error)) {
            error = "relative-height serialization failed: " + error;
            return false;
        }

        if (requestPayload.Length <= 0 || requestPayload.Length > MaxValuePayloadBytes
            || heightPayload.Length <= 0 || heightPayload.Length > MaxValuePayloadBytes) {
            error = "path preview value payload is unexpectedly large";
            return false;
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
            writer.Write(Version);
            WriteBytes(writer, familyBytes);
            writer.Write(state.IsContinuation);
            WriteBytes(writer, typeNameBytes);
            WriteBytes(writer, controllerStateBytes);
            WriteBytes(writer, requestPayload);
            WriteBytes(writer, heightPayload);
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
            error = "path preview payload is empty";
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
                    error = "unsupported path preview payload version " + version;
                    return false;
                }

                var family = Encoding.UTF8.GetString(ReadBytes(reader, stream, MaxStringBytes, "family"));
                var continuation = reader.ReadBoolean();
                var typeName = Encoding.UTF8.GetString(ReadBytes(reader, stream, MaxStringBytes, "type name"));
                var controllerState = Encoding.UTF8.GetString(ReadBytes(reader, stream, MaxStringBytes, "controller state", allowEmpty: true));
                var requestPayload = ReadBytes(reader, stream, MaxValuePayloadBytes, "request payload");
                var heightPayload = ReadBytes(reader, stream, MaxValuePayloadBytes, "height payload");

                if (stream.Position != stream.Length) {
                    error = "path preview payload has trailing bytes";
                    return false;
                }

                var requestType = ResolveType(typeName);
                if (requestType == null) {
                    error = "PreviewRequest type could not be resolved: " + typeName;
                    return false;
                }

                object request;
                if (!codec.TryDeserializeValue(requestPayload, requestType, out request, out error)) {
                    error = "PreviewRequest deserialization failed: " + error;
                    return false;
                }
                if (request == null) {
                    error = "decoded PreviewRequest is null";
                    return false;
                }

                object height;
                if (!codec.TryDeserializeValue(heightPayload, typeof(ThicknessTilesI), out height, out error)) {
                    error = "relative-height deserialization failed: " + error;
                    return false;
                }
                if (!(height is ThicknessTilesI relativeHeight)) {
                    error = "decoded relative height has unexpected type";
                    return false;
                }

                state = new DecodedState(
                    family,
                    continuation,
                    request,
                    requestType,
                    relativeHeight,
                    controllerState);
                return true;
            }
        }
        catch (EndOfStreamException) {
            error = "path preview payload ended unexpectedly";
            return false;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static void WriteBytes(BinaryWriter writer, byte[] value) {
        value = value ?? Array.Empty<byte>();
        writer.Write(value.Length);
        if (value.Length > 0) writer.Write(value);
    }

    private static byte[] ReadBytes(
        BinaryReader reader,
        Stream stream,
        int maxLength,
        string label,
        bool allowEmpty = false) {

        var length = reader.ReadInt32();
        if (length < 0
            || (!allowEmpty && length == 0)
            || length > maxLength
            || length > stream.Length - stream.Position) {
            throw new InvalidDataException("invalid " + label + " length " + length);
        }
        return reader.ReadBytes(length);
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
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }
}
