using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mafi;
using Mafi.Core.Entities;

namespace CoiCoop;

/// <summary>
/// Targeted sidecar synchronization for sandbox product endpoints that mutate
/// configuration outside InputScheduler.
///
/// Runtime testing identified ProductsSourceEntity.ProvidedProduct as one such
/// value. ProductsSinkEntity is handled by the same lightweight path: only
/// configuration-like product selectors and IsEnabled are considered. Runtime
/// counters such as ConsumedLastTick / ProvidedLastTick are deliberately ignored.
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

    private sealed class SyncMember {
        public string Key { get; }
        public Type ValueType { get; }
        public FieldInfo Field { get; }
        public PropertyInfo Property { get; }
        public string LastComparable { get; set; }
        public bool HasBaseline { get; set; }

        public SyncMember(string key, Type valueType, FieldInfo field, PropertyInfo property) {
            Key = key;
            ValueType = valueType;
            Field = field;
            Property = property;
        }

        public bool TryRead(object target, out object value) {
            value = null;
            try {
                var getter = Property?.GetGetMethod(true);
                if (getter != null) {
                    value = getter.Invoke(target, null);
                    return true;
                }
                if (Field != null) {
                    value = Field.GetValue(target);
                    return true;
                }
            }
            catch {
            }
            return false;
        }

        public bool TryWrite(object target, object value, out string error) {
            error = null;
            try {
                var setter = Property?.GetSetMethod(true);
                if (setter != null) {
                    setter.Invoke(target, new[] { value });
                    return true;
                }
                if (Field != null) {
                    Field.SetValue(target, value);
                    return true;
                }
                error = "member has no writable property or backing field";
                return false;
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
    }

    private sealed class Candidate {
        public IEntity Entity { get; }
        public Dictionary<string, SyncMember> Members { get; }

        public Candidate(IEntity entity, Dictionary<string, SyncMember> members) {
            Entity = entity;
            Members = members;
        }
    }

    private const string ProductsSourceTypeName = "Mafi.Base.Prototypes.Sandbox.ProductsSourceEntity";
    private const string ProductsSinkTypeName = "Mafi.Base.Prototypes.Sandbox.ProductsSinkEntity";
    private const byte WireVersion = 1;

    private readonly DependencyResolver m_resolver;
    private readonly Dictionary<int, Candidate> m_candidates = new Dictionary<int, Candidate>();
    private readonly Dictionary<Type, Dictionary<string, SyncMember>> m_memberTemplates
        = new Dictionary<Type, Dictionary<string, SyncMember>>();
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
            if (!IsSandboxEndpointType(type)) continue;

            var template = GetMemberTemplate(type);
            if (template.Count == 0) continue;

            // Per-candidate state must not share LastComparable/HasBaseline with
            // another entity, so clone the tiny member template.
            var members = new Dictionary<string, SyncMember>(StringComparer.Ordinal);
            foreach (var pair in template) {
                var member = pair.Value;
                members.Add(pair.Key, new SyncMember(
                    member.Key,
                    member.ValueType,
                    member.Field,
                    member.Property));
            }

            m_candidates.Add(entity.Id.Value, new Candidate(entity, members));
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

        foreach (var candidatePair in m_candidates) {
            var candidate = candidatePair.Value;
            foreach (var memberPair in candidate.Members) {
                var member = memberPair.Value;
                object value;
                if (!member.TryRead(candidate.Entity, out value)) continue;

                var comparable = SafeComparable(value);
                if (!member.HasBaseline) {
                    member.LastComparable = comparable;
                    member.HasBaseline = true;
                    continue;
                }

                if (string.Equals(member.LastComparable, comparable, StringComparison.Ordinal)) continue;

                byte[] serializedValue;
                string serializeError;
                if (!codec.TrySerializeValue(
                        value,
                        member.ValueType,
                        out serializedValue,
                        out serializeError)) {

                    error = "serialize sandbox endpoint " + candidatePair.Key
                        + " member " + member.Key + " failed: " + serializeError;
                    return false;
                }

                member.LastComparable = comparable;
                if (updates == null) updates = new List<LocalUpdate>();
                updates.Add(new LocalUpdate(
                    candidatePair.Key,
                    BuildMemberPayload(member.Key, serializedValue)));
            }
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
            error = "remote sandbox payload is null";
            return false;
        }

        RefreshIfNeeded();

        Candidate candidate;
        if (!m_candidates.TryGetValue(entityId, out candidate)) {
            m_lastKnownEntityCount = -1;
            RefreshIfNeeded();
            if (!m_candidates.TryGetValue(entityId, out candidate)) {
                error = "sandbox endpoint entity " + entityId + " was not found";
                return false;
            }
        }

        string memberKey;
        byte[] serializedValue;
        if (!TryParseMemberPayload(valuePayload, out memberKey, out serializedValue)) {
            error = "sandbox endpoint payload is malformed";
            return false;
        }

        SyncMember member;
        if (!candidate.Members.TryGetValue(memberKey, out member)) {
            error = "sandbox endpoint " + entityId + " has no sync member " + memberKey;
            return false;
        }

        object decoded;
        string deserializeError;
        if (!codec.TryDeserializeValue(
                serializedValue,
                member.ValueType,
                out decoded,
                out deserializeError)) {

            error = "deserialize sandbox endpoint " + entityId
                + " member " + memberKey + " failed: " + deserializeError;
            return false;
        }

        string writeError;
        if (!member.TryWrite(candidate.Entity, decoded, out writeError)) {
            error = "apply sandbox endpoint " + entityId
                + " member " + memberKey + " failed: " + writeError;
            return false;
        }

        member.LastComparable = SafeComparable(decoded);
        member.HasBaseline = true;
        return true;
    }

    private Dictionary<string, SyncMember> GetMemberTemplate(Type type) {
        Dictionary<string, SyncMember> cached;
        if (m_memberTemplates.TryGetValue(type, out cached)) return cached;

        var result = new Dictionary<string, SyncMember>(StringComparer.Ordinal);
        if (IsTypeOrBase(type, ProductsSourceTypeName)) {
            AddExactMember(result, type, "ProvidedProduct");
            AddExactMember(result, type, "IsEnabled");
        }
        else if (IsTypeOrBase(type, ProductsSinkTypeName)) {
            // Different COI revisions have used slightly different names for
            // product selectors. Register only configuration-looking members;
            // never counters such as ConsumedLastTick.
            var preferred = new[] {
                "AcceptedProduct",
                "ConsumedProduct",
                "SelectedProduct",
                "InputProduct",
                "RequiredProduct",
                "Product",
                "IsEnabled"
            };
            for (var i = 0; i < preferred.Length; i++) {
                AddExactMember(result, type, preferred[i]);
            }
            AddProductLikeSinkMembers(result, type);
        }

        m_memberTemplates[type] = result;
        return result;
    }

    private static void AddExactMember(
        Dictionary<string, SyncMember> result,
        Type type,
        string propertyName) {

        if (result.ContainsKey(propertyName)) return;

        PropertyInfo property = null;
        FieldInfo field = null;
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            if (property == null) {
                property = current.GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            if (field == null) {
                field = current.GetField(
                    "<" + propertyName + ">k__BackingField",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? current.GetField(
                        "m_" + char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
        }

        var valueType = property?.PropertyType ?? field?.FieldType;
        if (valueType == null) return;
        if (property?.GetGetMethod(true) == null && field == null) return;
        if (property?.GetSetMethod(true) == null && field == null) return;

        result.Add(propertyName, new SyncMember(propertyName, valueType, field, property));
    }

    private static void AddProductLikeSinkMembers(
        Dictionary<string, SyncMember> result,
        Type type) {

        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {

                if (field.IsStatic) continue;
                var name = field.Name ?? string.Empty;
                var combined = name + " " + (field.FieldType?.FullName ?? string.Empty);
                if (combined.IndexOf("product", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf("LastTick", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("counter", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("proto", StringComparison.OrdinalIgnoreCase) >= 0) {
                    continue;
                }

                var configLike = name.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("accepted", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("consumed", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("required", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(name, "m_product", StringComparison.OrdinalIgnoreCase);
                if (!configLike) continue;

                var key = "field:" + name;
                if (!result.ContainsKey(key)) {
                    result.Add(key, new SyncMember(key, field.FieldType, field, null));
                }
            }
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

    private static bool IsSandboxEndpointType(Type type) {
        return IsTypeOrBase(type, ProductsSourceTypeName)
            || IsTypeOrBase(type, ProductsSinkTypeName);
    }

    private static bool IsTypeOrBase(Type type, string fullName) {
        if (type == null) return false;
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static byte[] BuildMemberPayload(string memberKey, byte[] serializedValue) {
        memberKey = memberKey ?? string.Empty;
        serializedValue = serializedValue ?? Array.Empty<byte>();
        var keyBytes = Encoding.UTF8.GetBytes(memberKey);
        if (keyBytes.Length > ushort.MaxValue) throw new InvalidOperationException("sandbox member key is too long");

        var payload = new byte[1 + 2 + keyBytes.Length + serializedValue.Length];
        payload[0] = WireVersion;
        payload[1] = (byte)(keyBytes.Length & 0xFF);
        payload[2] = (byte)((keyBytes.Length >> 8) & 0xFF);
        Buffer.BlockCopy(keyBytes, 0, payload, 3, keyBytes.Length);
        if (serializedValue.Length > 0) {
            Buffer.BlockCopy(serializedValue, 0, payload, 3 + keyBytes.Length, serializedValue.Length);
        }
        return payload;
    }

    private static bool TryParseMemberPayload(
        byte[] payload,
        out string memberKey,
        out byte[] serializedValue) {

        memberKey = null;
        serializedValue = null;
        if (payload == null || payload.Length < 3 || payload[0] != WireVersion) return false;

        var keyLength = payload[1] | (payload[2] << 8);
        if (keyLength <= 0 || payload.Length < 3 + keyLength) return false;

        memberKey = Encoding.UTF8.GetString(payload, 3, keyLength);
        serializedValue = new byte[payload.Length - 3 - keyLength];
        if (serializedValue.Length > 0) {
            Buffer.BlockCopy(payload, 3 + keyLength, serializedValue, 0, serializedValue.Length);
        }
        return true;
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
