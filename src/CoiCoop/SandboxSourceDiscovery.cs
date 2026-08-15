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
        public PropertyInfo ProvidedProductProperty { get; }
        public string LastComparable { get; set; }
        public bool HasBaseline { get; set; }

        public Candidate(
            IEntity entity,
            FieldInfo providedProductField,
            PropertyInfo providedProductProperty) {

            Entity = entity;
            ProvidedProductField = providedProductField;
            ProvidedProductProperty = providedProductProperty;
        }
    }

    private const string ProductsSourceTypeName = "Mafi.Base.Prototypes.Sandbox.ProductsSourceEntity";
    private const string ProvidedProductBackingField = "<ProvidedProduct>k__BackingField";
    private const string ProvidedProductPropertyName = "ProvidedProduct";

    private readonly DependencyResolver m_resolver;
    private readonly Dictionary<int, Candidate> m_candidates = new Dictionary<int, Candidate>();
    private readonly Dictionary<Type, FieldInfo> m_fieldByType = new Dictionary<Type, FieldInfo>();
    private readonly Dictionary<Type, PropertyInfo> m_propertyByType = new Dictionary<Type, PropertyInfo>();
    private readonly HashSet<Type> m_typesWithoutField = new HashSet<Type>();
    private readonly HashSet<Type> m_typesWithoutProperty = new HashSet<Type>();
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

            var property = GetProvidedProductProperty(type);
            m_candidates.Add(entity.Id.Value, new Candidate(entity, field, property));
        }

        return before != m_candidates.Count;
    }

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

            var comparable = SafeComparable(value);
            if (!candidate.HasBaseline) {
                candidate.LastComparable = comparable;
                candidate.HasBaseline = true;
                continue;
            }

            if (string.Equals(candidate.LastComparable, comparable, StringComparison.Ordinal)) continue;

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

            candidate.LastComparable = comparable;
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
            var setter = candidate.ProvidedProductProperty?.GetSetMethod(true);
            if (setter != null) {
                setter.Invoke(candidate.Entity, new[] { decoded });
            }
            else {
                candidate.ProvidedProductField.SetValue(candidate.Entity, decoded);
            }

            candidate.LastComparable = SafeComparable(decoded);
            candidate.HasBaseline = true;
            return true;
        }
        catch (TargetInvocationException ex) {
            var inner = ex.InnerException ?? ex;
            error = "apply source " + entityId + " failed: " + inner.GetType().Name + ": " + inner.Message;
            return false;
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

    private PropertyInfo GetProvidedProductProperty(Type type) {
        PropertyInfo cached;
        if (m_propertyByType.TryGetValue(type, out cached)) return cached;
        if (m_typesWithoutProperty.Contains(type)) return null;

        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var property in current.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {

                if (!string.Equals(property.Name, ProvidedProductPropertyName, StringComparison.Ordinal)
                    || property.GetIndexParameters().Length != 0) {
                    continue;
                }

                m_propertyByType[type] = property;
                return property;
            }
        }

        m_typesWithoutProperty.Add(type);
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

    private static string SafeComparable(object value) {
        if (value == null) return "<null>";
        try {
            return value.GetType().FullName + "|" + value;
        }
        catch {
            return value.GetType().FullName ?? "<unknown>";
        }
    }
}
