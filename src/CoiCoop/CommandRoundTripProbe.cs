using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
/// </summary>
internal sealed class CommandRoundTripProbe {
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
            error = ex.GetType().Name + ": " + ex.Message;
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
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Serializes one exact declared type through COI's BlobWriter. Reflection is
    /// used only to close WriteGeneric&lt;T&gt; with the runtime field type; this keeps
    /// prototype references encoded through ProtosSerializerFactory instead of
    /// relying on ToString()/object identity.
    /// </summary>
    public bool TrySerializeValue(
        object value,
        Type declaredType,
        out byte[] payload,
        out string error) {

        payload = null;
        error = null;
        if (declaredType == null) {
            error = "declared type is null";
            return false;
        }

        try {
            using (var stream = new MemoryStream()) {
                var writer = new BlobWriter(stream, GetSerializers());
                var method = GetWriteGenericMethod().MakeGenericMethod(declaredType);
                method.Invoke(writer, new[] { value });
                writer.FinalizeSerialization();
                writer.Dispose();
                payload = stream.ToArray();
                return true;
            }
        }
        catch (TargetInvocationException ex) {
            var inner = ex.InnerException ?? ex;
            error = inner.GetType().Name + ": " + inner.Message;
            return false;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public bool TryDeserializeValue(
        byte[] payload,
        Type declaredType,
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

        try {
            using (var stream = new MemoryStream(payload, writable: false)) {
                var reader = new BlobReader(
                    stream,
                    SaveVersion.CURRENT_SAVE_VERSION,
                    GetSerializers());

                var method = GetReadGenericAsMethod().MakeGenericMethod(declaredType);
                value = method.Invoke(reader, null);
                reader.FinalizeLoading(m_resolver);

                if (stream.Position != stream.Length) {
                    throw new InvalidDataException(
                        "state payload has trailing bytes: " + (stream.Length - stream.Position));
                }
                return true;
            }
        }
        catch (TargetInvocationException ex) {
            var inner = ex.InnerException ?? ex;
            error = inner.GetType().Name + ": " + inner.Message;
            return false;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
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
