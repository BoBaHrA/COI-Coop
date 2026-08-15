using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Core.Entities;

namespace CoiCoop;

/// <summary>
/// Targeted sidecar synchronization for the sandbox ProductsSourceEntity.
///
/// Runtime testing on COI 0.8.7 identified the selected source material as the
/// compiler backing field <ProvidedProduct>k__BackingField. We intentionally do
/// not perform broad reflection scans here: candidate entities are indexed only
/// when the global entity count changes, and each candidate reads exactly one
/// cached field.
/// </summary>
internal sealed class SandboxSourceDiscovery {
    internal sealed class LocalUpdate {
        public int EntityId { get; }
        public byte[] ValuePayload { get; }

        public LocalUpdate(int entityId, byte[] valuePayload) {
            EntityId = entityId;
            ValuePayload = valuePayload ?? Array.Empty<byte>();
        }
    }

    private sealed class Candidate {
        public IEntity Entity { get; }
        public FieldInfo ProvidedProductField { get; }
        public byte[] LastPayload { get; set; }
        public bool HasBaseline { get; set; }

        public Candidate(IEntity entity, FieldInfo providedProductField) {
            Entity = entity;
            ProvidedProductField = providedProductField;
        }
    }

    private const string ProductsSourceTypeName = "Mafi.Base.Prototypes.Sandbox.ProductsSourceEntity";
    private const string ProvidedProductBackingField = "<ProvidedProduct>k__BackingField";

    private readonly DependencyResolver m_resolver;
    private readonly Dictionary<int, Candidate> m_candidates = new Dictionary<int, Candidate>();
    private readonly Dictionary<Type, FieldInfo> m_fieldByType = new Dictionary<Type, FieldInfo>();
    private readonly HashSet<Type> m_typesWithoutField = new HashSet<Type>();
    private EntitiesManager m_entities;
    private int m_lastKnownEntityCount = -1;

    public SandboxSourceDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public int CandidateCount => m_candidates.Count;

    public bool RefreshIfNeeded() {
        if (!EnsureEntitiesManager()) return false;

        var currentCount = m_entities.EntitiesCount;
        if (currentCount == m_lastKnownEntityCount) return false;
        m_lastKnownEntityCount = currentCount;

        var before = m_candidates.Count;
        foreach (IEntity entity in m_entities.Entities) {
            if (entity == null || m_candidates.ContainsKey(entity.Id.Value)) continue;

            var type = entity.GetType();
            if (!IsProductsSourceType(type)) continue;

            var field = GetProvidedProductField(type);
            if (field == null) continue;

            m_candidates.Add(entity.Id.Value, new Candidate(entity, field));
        }

        return before != m_candidates.Count;
    }

    /// <summary>
    /// Reads one field per sandbox source. The first observation establishes a
    /// baseline and is not transmitted. Later changes are returned as tiny binary
    /// updates, already serialized through COI's own prototype-aware serializer.
    /// </summary>
    public bool TryCaptureChanges(
        CommandRoundTripProbe codec,
        out List<LocalUpdate> updates,
        out string error) {

        updates = null;
        error = null;
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }

        RefreshIfNeeded();
        if (m_candidates.Count == 0) return true;

        foreach (var pair in m_candidates) {
            var candidate = pair.Value;
            object value;
            try {
                value = candidate.ProvidedProductField.GetValue(candidate.Entity);
            }
            catch (Exception ex) {
                error = "read source " + pair.Key + " failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            byte[] payload;
            string serializeError;
            if (!codec.TrySerializeValue(
                    value,
                    candidate.ProvidedProductField.FieldType,
                    out payload,
                    out serializeError)) {

                error = "serialize source " + pair.Key + " failed: " + serializeError;
                return false;
            }

            if (!candidate.HasBaseline) {
                candidate.LastPayload = payload;
                candidate.HasBaseline = true;
                continue;
            }

            if (BytesEqual(candidate.LastPayload, payload)) continue;

            candidate.LastPayload = payload;
            if (updates == null) updates = new List<LocalUpdate>();
            updates.Add(new LocalUpdate(pair.Key, payload));
        }

        return true;
    }

    public bool TryApplyRemote(
        int entityId,
        byte[] valuePayload,
        CommandRoundTripProbe codec,
        out string error) {

        error = null;
        if (codec == null) {
            error = "state codec is unavailable";
            return false;
        }
        if (valuePayload == null) {
            error = "remote source payload is null";
            return false;
        }

        RefreshIfNeeded();

        Candidate candidate;
        if (!m_candidates.TryGetValue(entityId, out candidate)) {
            // Entity count can occasionally be observed before our local cache was
            // rebuilt; force one scan by invalidating the count and retry once.
            m_lastKnownEntityCount = -1;
            RefreshIfNeeded();
            if (!m_candidates.TryGetValue(entityId, out candidate)) {
                error = "sandbox source entity " + entityId + " was not found";
                return false;
            }
        }

        object decoded;
        string deserializeError;
        if (!codec.TryDeserializeValue(
                valuePayload,
                candidate.ProvidedProductField.FieldType,
                out decoded,
                out deserializeError)) {

            error = "deserialize source " + entityId + " failed: " + deserializeError;
            return false;
        }

        try {
            candidate.ProvidedProductField.SetValue(candidate.Entity, decoded);
            // Prevent the remote application from being echoed back as a local
            // change on the next sample.
            candidate.LastPayload = (byte[])valuePayload.Clone();
            candidate.HasBaseline = true;
            return true;
        }
        catch (Exception ex) {
            error = "apply source " + entityId + " failed: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private bool EnsureEntitiesManager() {
        if (m_entities != null) return true;

        EntitiesManager entities;
        if (!m_resolver.TryGetResolvedDependency<EntitiesManager>(out entities) || entities == null) {
            return false;
        }
        m_entities = entities;
        return true;
    }

    private FieldInfo GetProvidedProductField(Type type) {
        FieldInfo cached;
        if (m_fieldByType.TryGetValue(type, out cached)) return cached;
        if (m_typesWithoutField.Contains(type)) return null;

        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            var field = current.GetField(
                ProvidedProductBackingField,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || field.IsStatic) continue;

            m_fieldByType[type] = field;
            return field;
        }

        m_typesWithoutField.Add(type);
        return null;
    }

    private static bool IsProductsSourceType(Type type) {
        if (type == null) return false;
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            if (string.Equals(current.FullName, ProductsSourceTypeName, StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }

    private static bool BytesEqual(byte[] left, byte[] right) {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++) {
            if (left[i] != right[i]) return false;
        }
        return true;
    }
}
