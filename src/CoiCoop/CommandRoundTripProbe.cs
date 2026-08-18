using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Game;
using Mafi.Core.Input;
using Mafi.Core.Prototypes;
using Mafi.Serialization;

namespace CoiCoop;

/// <summary>
/// Small adapter around the game's own serialization stack. It is used for
/// command replay and for a few tightly-scoped sidecar state values that do not
/// travel through InputScheduler (for example sandbox ProductsSourceEntity state).
///
/// COI 0.8.7 has a few internal value structs used by path-planning previews
/// (for example BridgePathFinderOptions) for which BlobWriter cannot create a
/// generic serializer. For those values only, after the normal COI serializer
/// has actually failed, this adapter falls back to a small structural envelope
/// containing the struct's instance fields. Nested values still prefer COI's
/// serializer first. This keeps normal commands/prototypes on the game's native
/// serialization path and avoids broad reflection during ordinary gameplay.
/// </summary>
internal sealed class CommandRoundTripProbe {
    private static readonly byte[] StructuralMagic = Encoding.ASCII.GetBytes("COIPV1");
    private const int MaxStructuralDepth = 8;
    private const int MaxStructuralFields = 64;
    private const int MaxStructuralStringBytes = 4096;
    private const int MaxStructuralValueBytes = 1024 * 1024;

    private readonly DependencyResolver m_resolver;
    private ImmutableArray<ISpecialSerializerFactory> m_serializers;
    private bool m_serializersReady;
    private MethodInfo m_writeGenericMethod;
    private MethodInfo m_readGenericAsMethod;

    public CommandRoundTripProbe(DependencyResolver resolver) {
        m_resolver = resolver;
    }

    public bool TryRoundTrip(
        IInputCommand command,
        out int payloadLength,
        out string cloneType,
        out string error) {

        payloadLength = 0;
        cloneType = null;
        error = null;

        byte[] payload;
        if (!TrySerialize(command, out payload, out error)) {
            return false;
        }

        payloadLength = payload.Length;

        IInputCommand clone;
        if (!TryDeserialize(payload, out clone, out error)) {
            return false;
        }

        if (clone == null) {
            error = "deserializer returned null";
            return false;
        }

        cloneType = clone.GetType().FullName;
        if (clone.GetType() != command.GetType()) {
            error = "round-trip type mismatch: " + command.GetType().FullName + " -> " + cloneType;
            return false;
        }

        return true;
    }

    public bool TrySerialize(IInputCommand command, out byte[] payload, out string error) {
        payload = null;
        error = null;

        try {
            payload = Serialize(command);
            return true;
        }
        catch (Exception ex) {
            error = FormatException(ex);
            return false;
        }
    }

    public bool TryDeserialize(byte[] payload, out IInputCommand command, out string error) {
        command = null;
        error = null;

        try {
            command = Deserialize(payload);
            return command != null;
        }
        catch (Exception ex) {
            error = FormatException(ex);
            return false;
        }
    }

    /// <summary>
    /// Serializes one exact declared type through COI's BlobWriter. Reflection is
    /// used only to close WriteGeneric&lt;T&gt; with the runtime field type; this keeps
    /// prototype references encoded through ProtosSerializerFactory instead of
    /// relying on ToString()/object identity.
    ///
    /// If COI explicitly cannot create a serializer for a Mafi value struct, a
    /// targeted structural fallback is used for that value only.
    /// </summary>
    public bool TrySerializeValue(
        object value,
        Type declaredType,
        out byte[] payload,
        out string error) {

        return TrySerializeValueInternal(value, declaredType, 0, out payload, out error);
    }

    public bool TryDeserializeValue(
        byte[] payload,
        Type declaredType,
        out object value,
        out string error) {

        return TryDeserializeValueInternal(payload, declaredType, 0, out value, out error);
    }

    private bool TrySerializeValueInternal(
        object value,
        Type declaredType,
        int depth,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (declaredType == null) {
            error = "declared type is null";
            return false;
        }

        try {
            payload = SerializeValueGeneric(value, declaredType);
            return true;
        }
        catch (Exception ex) {
            var genericError = FormatException(ex);
            if (!CanUseStructuralFallback(declaredType)) {
                error = genericError;
                return false;
            }

            string structuralError;
            if (TrySerializeStructuralValue(
                    value,
                    declaredType,
                    depth,
                    out payload,
                    out structuralError)) {

                return true;
            }

            error = genericError + " | structural fallback failed: " + structuralError;
            return false;
        }
    }

    private bool TryDeserializeValueInternal(
        byte[] payload,
        Type declaredType,
        int depth,
        out object value,
        out string error) {

        value = null;
        error = null;
        if (payload == null) {
            error = "payload is null";
            return false;
        }
        if (declaredType == null) {
            error = "declared type is null";
            return false;
        }

        if (HasStructuralMagic(payload)) {
            return TryDeserializeStructuralValue(
                payload,
                declaredType,
                depth,
                out value,
                out error);
        }

        try {
            value = DeserializeValueGeneric(payload, declaredType);
            return true;
        }
        catch (Exception ex) {
            error = FormatException(ex);
            return false;
        }
    }

    private byte[] SerializeValueGeneric(object value, Type declaredType) {
        using (var stream = new MemoryStream()) {
            var writer = new BlobWriter(stream, GetSerializers());
            try {
                var method = GetWriteGenericMethod().MakeGenericMethod(declaredType);
                method.Invoke(writer, new[] { value });
                writer.FinalizeSerialization();
                return stream.ToArray();
            }
            finally {
                writer.Dispose();
            }
        }
    }

    private object DeserializeValueGeneric(byte[] payload, Type declaredType) {
        using (var stream = new MemoryStream(payload, writable: false)) {
            var reader = new BlobReader(
                stream,
                SaveVersion.CURRENT_SAVE_VERSION,
                GetSerializers());

            var method = GetReadGenericAsMethod().MakeGenericMethod(declaredType);
            var value = method.Invoke(reader, null);
            reader.FinalizeLoading(m_resolver);

            if (stream.Position != stream.Length) {
                throw new InvalidDataException(
                    "state payload has trailing bytes: " + (stream.Length - stream.Position));
            }
            return value;
        }
    }

    private bool TrySerializeStructuralValue(
        object value,
        Type declaredType,
        int depth,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (depth >= MaxStructuralDepth) {
            error = "maximum structural depth exceeded for " + declaredType.FullName;
            return false;
        }
        if (value == null) {
            error = "value is null for structural value type " + declaredType.FullName;
            return false;
        }

        try {
            var fields = GetSerializableInstanceFields(declaredType);
            if (fields.Length == 0 || fields.Length > MaxStructuralFields) {
                error = "invalid structural field count " + fields.Length
                    + " for " + declaredType.FullName;
                return false;
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8)) {
                writer.Write(StructuralMagic);
                WriteStructuralString(writer, declaredType.AssemblyQualifiedName ?? declaredType.FullName);
                writer.Write(fields.Length);

                for (var i = 0; i < fields.Length; i++) {
                    var field = fields[i];
                    WriteStructuralString(writer, field.Name);
                    WriteStructuralString(
                        writer,
                        field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName);

                    byte[] nestedPayload;
                    string nestedError;
                    if (!TrySerializeValueInternal(
                            field.GetValue(value),
                            field.FieldType,
                            depth + 1,
                            out nestedPayload,
                            out nestedError)) {

                        error = "field '" + field.Name + "' (" + field.FieldType.FullName
                            + ") failed: " + nestedError;
                        return false;
                    }

                    if (nestedPayload == null || nestedPayload.Length > MaxStructuralValueBytes) {
                        error = "field '" + field.Name + "' payload is invalid/too large";
                        return false;
                    }

                    writer.Write(nestedPayload.Length);
                    if (nestedPayload.Length > 0) writer.Write(nestedPayload);
                }

                writer.Flush();
                if (stream.Length > MaxStructuralValueBytes) {
                    error = "structural payload is too large: " + stream.Length;
                    return false;
                }
                payload = stream.ToArray();
                return true;
            }
        }
        catch (Exception ex) {
            error = FormatException(ex);
            return false;
        }
    }

    private bool TryDeserializeStructuralValue(
        byte[] payload,
        Type declaredType,
        int depth,
        out object value,
        out string error) {

        value = null;
        error = null;
        if (depth >= MaxStructuralDepth) {
            error = "maximum structural depth exceeded for " + declaredType.FullName;
            return false;
        }
        if (!CanUseStructuralFallback(declaredType)) {
            error = "structural payload is not permitted for " + declaredType.FullName;
            return false;
        }

        try {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8)) {
                var magic = reader.ReadBytes(StructuralMagic.Length);
                if (magic.Length != StructuralMagic.Length
                    || !magic.SequenceEqual(StructuralMagic)) {

                    error = "invalid structural payload magic";
                    return false;
                }

                var wireTypeName = ReadStructuralString(reader, stream);
                var wireType = ResolveType(wireTypeName);
                if (wireType == null || wireType != declaredType) {
                    error = "structural type mismatch: wire=" + wireTypeName
                        + " local=" + declaredType.AssemblyQualifiedName;
                    return false;
                }

                var fieldCount = reader.ReadInt32();
                if (fieldCount <= 0 || fieldCount > MaxStructuralFields) {
                    error = "invalid structural field count " + fieldCount;
                    return false;
                }

                var decoded = new List<StructuralFieldValue>(fieldCount);
                for (var i = 0; i < fieldCount; i++) {
                    var fieldName = ReadStructuralString(reader, stream);
                    var fieldTypeName = ReadStructuralString(reader, stream);
                    var fieldType = ResolveType(fieldTypeName);
                    if (fieldType == null) {
                        error = "could not resolve structural field type " + fieldTypeName;
                        return false;
                    }

                    var runtimeField = FindInstanceField(declaredType, fieldName);
                    if (runtimeField == null) {
                        error = "structural field not found on peer: " + fieldName;
                        return false;
                    }
                    if (runtimeField.FieldType != fieldType) {
                        error = "structural field type mismatch for '" + fieldName
                            + "': wire=" + fieldType.FullName
                            + " local=" + runtimeField.FieldType.FullName;
                        return false;
                    }

                    var nestedLength = reader.ReadInt32();
                    if (nestedLength < 0
                        || nestedLength > MaxStructuralValueBytes
                        || nestedLength > stream.Length - stream.Position) {

                        error = "invalid nested structural payload length " + nestedLength
                            + " for field '" + fieldName + "'";
                        return false;
                    }

                    var nestedPayload = reader.ReadBytes(nestedLength);
                    object nestedValue;
                    string nestedError;
                    if (!TryDeserializeValueInternal(
                            nestedPayload,
                            fieldType,
                            depth + 1,
                            out nestedValue,
                            out nestedError)) {

                        error = "field '" + fieldName + "' failed: " + nestedError;
                        return false;
                    }

                    decoded.Add(new StructuralFieldValue(
                        fieldName,
                        fieldType,
                        nestedValue,
                        runtimeField));
                }

                if (stream.Position != stream.Length) {
                    error = "structural payload has trailing bytes: "
                        + (stream.Length - stream.Position);
                    return false;
                }

                return TryReconstructStructuralValue(
                    declaredType,
                    decoded,
                    out value,
                    out error);
            }
        }
        catch (EndOfStreamException) {
            error = "structural payload ended unexpectedly";
            return false;
        }
        catch (Exception ex) {
            error = FormatException(ex);
            return false;
        }
    }

    private static bool TryReconstructStructuralValue(
        Type type,
        List<StructuralFieldValue> fields,
        out object value,
        out string error) {

        value = null;
        error = null;

        var byNormalizedName = new Dictionary<string, StructuralFieldValue>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++) {
            byNormalizedName[NormalizeMemberName(fields[i].Name)] = fields[i];
        }

        var constructors = type.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (var c = 0; c < constructors.Length; c++) {
            var constructor = constructors[c];
            var parameters = constructor.GetParameters();
            if (parameters.Length != fields.Count) continue;

            var args = new object[parameters.Length];
            var matches = true;
            for (var i = 0; i < parameters.Length; i++) {
                StructuralFieldValue field;
                if (!byNormalizedName.TryGetValue(
                        NormalizeMemberName(parameters[i].Name),
                        out field)
                    || field.Type != parameters[i].ParameterType) {

                    matches = false;
                    break;
                }
                args[i] = field.Value;
            }
            if (!matches) continue;

            try {
                value = constructor.Invoke(args);
                if (value != null) return true;
            }
            catch {
                // Some internal structs expose a constructor that performs runtime
                // validation unsuitable for a presentation-only replay. Fall back
                // to restoring the boxed value fields below.
            }
        }

        try {
            var boxed = Activator.CreateInstance(type);
            for (var i = 0; i < fields.Count; i++) {
                fields[i].RuntimeField.SetValue(boxed, fields[i].Value);
            }
            value = boxed;
            return value != null;
        }
        catch (Exception ex) {
            error = "could not reconstruct " + type.FullName + ": " + FormatException(ex);
            return false;
        }
    }

    private sealed class StructuralFieldValue {
        public string Name { get; }
        public Type Type { get; }
        public object Value { get; }
        public FieldInfo RuntimeField { get; }

        public StructuralFieldValue(
            string name,
            Type type,
            object value,
            FieldInfo runtimeField) {

            Name = name;
            Type = type;
            Value = value;
            RuntimeField = runtimeField;
        }
    }

    private static FieldInfo[] GetSerializableInstanceFields(Type type) {
        return type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsStatic)
            .OrderBy(field => field.MetadataToken)
            .ToArray();
    }

    private static FieldInfo FindInstanceField(Type type, string name) {
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            var field = current.GetField(
                name,
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsStatic) return field;
        }
        return null;
    }

    private static bool CanUseStructuralFallback(Type type) {
        if (type == null
            || !type.IsValueType
            || type.IsPrimitive
            || type.IsEnum
            || Nullable.GetUnderlyingType(type) != null) {
            return false;
        }

        var fullName = type.FullName ?? string.Empty;
        if (!fullName.StartsWith("Mafi.", StringComparison.Ordinal)) return false;

        // These container wrappers already have dedicated game serializers and
        // reflecting their implementation storage would make the wire format
        // brittle/large. If one of them ever fails, add a targeted adapter instead.
        if (fullName.StartsWith("Mafi.Collections.", StringComparison.Ordinal)
            || fullName.StartsWith("Mafi.Option`", StringComparison.Ordinal)) {
            return false;
        }

        return true;
    }

    private static string NormalizeMemberName(string name) {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var normalized = name;
        if (normalized.StartsWith("m_", StringComparison.Ordinal)) {
            normalized = normalized.Substring(2);
        }
        while (normalized.StartsWith("_", StringComparison.Ordinal)) {
            normalized = normalized.Substring(1);
        }

        if (normalized.Length > 3
            && normalized[0] == '<'
            && normalized.IndexOf(">k__BackingField", StringComparison.Ordinal) > 1) {

            var end = normalized.IndexOf('>');
            normalized = normalized.Substring(1, end - 1);
        }

        return normalized.Replace("_", string.Empty).ToLowerInvariant();
    }

    private static void WriteStructuralString(BinaryWriter writer, string value) {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length <= 0 || bytes.Length > MaxStructuralStringBytes) {
            throw new InvalidDataException("invalid structural string length " + bytes.Length);
        }
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadStructuralString(BinaryReader reader, Stream stream) {
        var length = reader.ReadInt32();
        if (length <= 0
            || length > MaxStructuralStringBytes
            || length > stream.Length - stream.Position) {

            throw new InvalidDataException("invalid structural string length " + length);
        }
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static bool HasStructuralMagic(byte[] payload) {
        if (payload == null || payload.Length < StructuralMagic.Length) return false;
        for (var i = 0; i < StructuralMagic.Length; i++) {
            if (payload[i] != StructuralMagic[i]) return false;
        }
        return true;
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

    private static string FormatException(Exception ex) {
        var current = ex;
        while (current is TargetInvocationException target && target.InnerException != null) {
            current = target.InnerException;
        }
        return current.GetType().Name + ": " + current.Message;
    }

    private byte[] Serialize(IInputCommand command) {
        using (var stream = new MemoryStream()) {
            var writer = new BlobWriter(stream, GetSerializers());
            writer.WriteGeneric(command);
            writer.FinalizeSerialization();
            writer.Dispose();
            return stream.ToArray();
        }
    }

    private IInputCommand Deserialize(byte[] payload) {
        using (var stream = new MemoryStream(payload, writable: false)) {
            var reader = new BlobReader(
                stream,
                SaveVersion.CURRENT_SAVE_VERSION,
                GetSerializers());

            var command = reader.ReadGenericAs<IInputCommand>();
            reader.FinalizeLoading(m_resolver);

            if (stream.Position != stream.Length) {
                throw new InvalidDataException(
                    "command payload has trailing bytes: " + (stream.Length - stream.Position));
            }

            return command;
        }
    }

    private MethodInfo GetWriteGenericMethod() {
        if (m_writeGenericMethod != null) return m_writeGenericMethod;

        m_writeGenericMethod = typeof(BlobWriter)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
                method.Name == "WriteGeneric"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 1);

        if (m_writeGenericMethod == null) {
            throw new MissingMethodException(typeof(BlobWriter).FullName, "WriteGeneric<T>(T)");
        }
        return m_writeGenericMethod;
    }

    private MethodInfo GetReadGenericAsMethod() {
        if (m_readGenericAsMethod != null) return m_readGenericAsMethod;

        m_readGenericAsMethod = typeof(BlobReader)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
                method.Name == "ReadGenericAs"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 0);

        if (m_readGenericAsMethod == null) {
            throw new MissingMethodException(typeof(BlobReader).FullName, "ReadGenericAs<T>()");
        }
        return m_readGenericAsMethod;
    }

    private ImmutableArray<ISpecialSerializerFactory> GetSerializers() {
        if (m_serializersReady) {
            return m_serializers;
        }

        ProtosDb protosDb;
        if (!m_resolver.TryGetResolvedDependency<ProtosDb>(out protosDb)) {
            throw new InvalidOperationException("ProtosDb is not available yet");
        }

        m_serializers = new ImmutableArray<ISpecialSerializerFactory>(
            new ISpecialSerializerFactory[] {
                new ProtosSerializerFactory(protosDb)
            });
        m_serializersReady = true;
        return m_serializers;
    }
}
