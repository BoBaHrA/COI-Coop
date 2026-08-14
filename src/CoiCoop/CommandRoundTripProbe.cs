using System;
using System.IO;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Game;
using Mafi.Core.Input;
using Mafi.Core.Prototypes;
using Mafi.Serialization;

namespace CoiCoop;

/// <summary>
/// Development-only probe that verifies whether a processed IInputCommand can be
/// serialized to bytes and reconstructed with the game's own serialization stack.
/// No reconstructed command is executed.
/// </summary>
internal sealed class CommandRoundTripProbe {
    private readonly DependencyResolver m_resolver;
    private ImmutableArray<ISpecialSerializerFactory> m_serializers;
    private bool m_serializersReady;

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

        try {
            var payload = Serialize(command);
            payloadLength = payload.Length;

            var clone = Deserialize(payload);
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
