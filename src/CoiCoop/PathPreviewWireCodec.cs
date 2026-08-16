using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mafi;

namespace CoiCoop;

/// <summary>
/// Compact latest-wins payload for the game's native multi-stage PreviewRequest.
///
/// COI 0.8.7 does not expose a generic serializer for the nested PreviewRequest
/// structs themselves (BlobWriter fails with "Failed to create generic serializer
/// for 'PreviewRequest'"). Their component fields are normal COI/core values that
/// already participate in command/save serialization, so we serialize those fields
/// individually and reconstruct the value type through its real constructor on the
/// receiving peer.
/// </summary>
internal static class PathPreviewWireCodec {
    private const byte Version = 2;
    private const int MaxStringBytes = 1024;
    private const int MaxFieldCount = 64;
    private const int MaxValuePayloadBytes = 1024 * 1024;
    private const int MaxTotalPayloadBytes = 4 * 1024 * 1024;

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

    private sealed class DecodedField {
        public string Name { get; }
        public Type DeclaredType { get; }
        public object Value { get; }

        public DecodedField(string name, Type declaredType, object value) {
            Name = name;
            DeclaredType = declaredType;
            Value = value;
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

        try {
            var familyBytes = EncodeString(state.Family, "family", allowEmpty: false);
            var typeName = state.RequestType.AssemblyQualifiedName ?? state.RequestType.FullName ?? string.Empty;
            var typeNameBytes = EncodeString(typeName, "type name", allowEmpty: false);
            var controllerStateBytes = EncodeString(state.ControllerState, "controller state", allowEmpty: true);

            byte[] heightPayload;
            if (!codec.TrySerializeValue(
                    state.RelativeHeight,
                    typeof(ThicknessTilesI),
                    out heightPayload,
                    out error)) {

                error = "relative-height serialization failed: " + error;
                return false;
            }
            if (!ValidateValuePayload(heightPayload)) {
                error = "relative-height payload is unexpectedly large";
                return false;
            }

            var fields = state.RequestType
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsStatic)
                .OrderBy(field => field.MetadataToken)
                .ToArray();

            if (fields.Length == 0 || fields.Length > MaxFieldCount) {
                error = "PreviewRequest field count is invalid: " + fields.Length;
                return false;
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
                writer.Write(Version);
                WriteBytes(writer, familyBytes);
                writer.Write(state.IsContinuation);
                WriteBytes(writer, typeNameBytes);
                WriteBytes(writer, controllerStateBytes);
                WriteBytes(writer, heightPayload);
                writer.Write(fields.Length);

                for (var i = 0; i < fields.Length; i++) {
                    var field = fields[i];
                    var fieldNameBytes = EncodeString(field.Name, "field name", allowEmpty: false);
                    var fieldTypeName = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? string.Empty;
                    var fieldTypeBytes = EncodeString(fieldTypeName, "field type", allowEmpty: false);
                    var fieldValue = field.GetValue(state.Request);

                    byte[] fieldPayload;
                    string fieldError;
                    if (!codec.TrySerializeValue(
                            fieldValue,
                            field.FieldType,
                            out fieldPayload,
                            out fieldError)) {

                        error = "PreviewRequest field '" + field.Name
                            + "' (" + field.FieldType.FullName + ") serialization failed: "
                            + fieldError;
                        return false;
                    }
                    if (!ValidateValuePayload(fieldPayload)) {
                        error = "PreviewRequest field '" + field.Name + "' payload is unexpectedly large";
                        return false;
                    }

                    WriteBytes(writer, fieldNameBytes);
                    WriteBytes(writer, fieldTypeBytes);
                    WriteBytes(writer, fieldPayload);
                }

                writer.Flush();
                if (stream.Length > MaxTotalPayloadBytes) {
                    error = "path preview payload is unexpectedly large: " + stream.Length;
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
            error = "path preview payload is empty";
            return false;
        }
        if (payload.Length > MaxTotalPayloadBytes) {
            error = "path preview payload is too large: " + payload.Length;
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
                var controllerState = Encoding.UTF8.GetString(
                    ReadBytes(reader, stream, MaxStringBytes, "controller state", allowEmpty: true));
                var heightPayload = ReadBytes(reader, stream, MaxValuePayloadBytes, "height payload");

                var requestType = ResolveType(typeName);
                if (requestType == null) {
                    error = "PreviewRequest type could not be resolved: " + typeName;
                    return false;
                }

                object height;
                if (!codec.TryDeserializeValue(
                        heightPayload,
                        typeof(ThicknessTilesI),
                        out height,
                        out error)) {

                    error = "relative-height deserialization failed: " + error;
                    return false;
                }
                if (!(height is ThicknessTilesI relativeHeight)) {
                    error = "decoded relative height has unexpected type";
                    return false;
                }

                var fieldCount = reader.ReadInt32();
                if (fieldCount <= 0 || fieldCount > MaxFieldCount) {
                    error = "invalid PreviewRequest field count " + fieldCount;
                    return false;
                }

                var decodedFields = new List<DecodedField>(fieldCount);
                for (var i = 0; i < fieldCount; i++) {
                    var fieldName = Encoding.UTF8.GetString(
                        ReadBytes(reader, stream, MaxStringBytes, "field name"));
                    var fieldTypeName = Encoding.UTF8.GetString(
                        ReadBytes(reader, stream, MaxStringBytes, "field type"));
                    var fieldPayload = ReadBytes(reader, stream, MaxValuePayloadBytes, "field payload");

                    var declaredType = ResolveType(fieldTypeName);
                    if (declaredType == null) {
                        error = "PreviewRequest field type could not be resolved: " + fieldTypeName;
                        return false;
                    }

                    var runtimeField = requestType.GetField(
                        fieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (runtimeField == null) {
                        error = "PreviewRequest field was not found on peer: " + fieldName;
                        return false;
                    }
                    if (runtimeField.FieldType != declaredType) {
                        error = "PreviewRequest field type mismatch for '" + fieldName
                            + "': wire=" + declaredType.FullName
                            + " local=" + runtimeField.FieldType.FullName;
                        return false;
                    }

                    object fieldValue;
                    string fieldError;
                    if (!codec.TryDeserializeValue(
                            fieldPayload,
                            declaredType,
                            out fieldValue,
                            out fieldError)) {

                        error = "PreviewRequest field '" + fieldName
                            + "' deserialization failed: " + fieldError;
                        return false;
                    }

                    decodedFields.Add(new DecodedField(fieldName, declaredType, fieldValue));
                }

                if (stream.Position != stream.Length) {
                    error = "path preview payload has trailing bytes";
                    return false;
                }

                object request;
                if (!TryConstructRequest(requestType, decodedFields, out request, out error)) {
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

    private static bool TryConstructRequest(
        Type requestType,
        List<DecodedField> fields,
        out object request,
        out string error) {

        request = null;
        error = null;

        var byName = new Dictionary<string, DecodedField>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++) {
            byName[fields[i].Name] = fields[i];
        }

        var constructors = requestType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var c = 0; c < constructors.Length; c++) {
            var constructor = constructors[c];
            var parameters = constructor.GetParameters();
            if (parameters.Length != fields.Count) continue;

            var args = new object[parameters.Length];
            var matches = true;
            for (var i = 0; i < parameters.Length; i++) {
                DecodedField field;
                if (!byName.TryGetValue(parameters[i].Name ?? string.Empty, out field)
                    || field.DeclaredType != parameters[i].ParameterType) {

                    matches = false;
                    break;
                }
                args[i] = field.Value;
            }
            if (!matches) continue;

            try {
                request = constructor.Invoke(args);
                if (request != null) return true;
            }
            catch (TargetInvocationException ex) {
                var inner = ex.InnerException ?? ex;
                error = "PreviewRequest constructor failed: "
                    + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            catch (Exception ex) {
                error = "PreviewRequest constructor failed: "
                    + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        error = "no PreviewRequest constructor matched " + fields.Count
            + " decoded fields on " + requestType.FullName;
        return false;
    }

    private static byte[] EncodeString(string value, string label, bool allowEmpty) {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if ((!allowEmpty && bytes.Length == 0) || bytes.Length > MaxStringBytes) {
            throw new InvalidDataException(label + " is invalid or too long");
        }
        return bytes;
    }

    private static bool ValidateValuePayload(byte[] value) {
        return value != null && value.Length > 0 && value.Length <= MaxValuePayloadBytes;
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
