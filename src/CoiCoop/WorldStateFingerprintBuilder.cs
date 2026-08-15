using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CoiCoop.Networking;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;

namespace CoiCoop;

/// <summary>
/// Diagnostic-only shallow world fingerprint.
///
/// The goal is not to serialize the whole simulation. We hash stable entity
/// identity plus simple shallow state (primitive/enum/value types and reference
/// objects that expose a stable Id-like member). Unknown reference graphs are
/// deliberately ignored so UI caches/delegates do not poison determinism.
/// </summary>
internal sealed class WorldStateFingerprintBuilder {
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const int MaxValueDepth = 3;

    private readonly DependencyResolver m_resolver;
    private readonly Dictionary<Type, FieldInfo[]> m_entityFieldCache = new Dictionary<Type, FieldInfo[]>();
    private readonly Dictionary<Type, MemberInfo> m_stableIdMemberCache = new Dictionary<Type, MemberInfo>();
    private readonly HashSet<Type> m_noStableIdMember = new HashSet<Type>();
    private EntitiesManager m_entities;

    public WorldStateFingerprintBuilder(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public bool TryCapture(
        long authorityFrame,
        long authoritySequence,
        long simulationStep,
        out StateProbeSnapshot snapshot,
        out string error) {

        snapshot = null;
        error = null;

        try {
            if (m_entities == null) {
                EntitiesManager entities;
                if (!m_resolver.TryGetResolvedDependency<EntitiesManager>(out entities) || entities == null) {
                    error = "EntitiesManager is unavailable";
                    return false;
                }
                m_entities = entities;
            }

            var entitiesList = new List<IEntity>(m_entities.EntitiesCount);
            foreach (IEntity entity in m_entities.Entities) {
                if (entity != null) {
                    entitiesList.Add(entity);
                }
            }
            entitiesList.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));

            var idHash = FnvOffset;
            var stateHash = FnvOffset;
            var hashedMembers = 0;

            foreach (var entity in entitiesList) {
                AppendInt64(ref idHash, entity.Id.Value);

                AppendInt64(ref stateHash, entity.Id.Value);
                AppendString(ref stateHash, entity.GetType().FullName ?? entity.GetType().Name);

                var fields = GetStableCandidateFields(entity.GetType());
                foreach (var field in fields) {
                    object value;
                    try {
                        value = field.GetValue(entity);
                    }
                    catch {
                        continue;
                    }

                    var valueHash = FnvOffset;
                    if (!TryAppendStableValue(ref valueHash, value, 0)) {
                        continue;
                    }

                    AppendString(ref stateHash, field.DeclaringType?.FullName ?? string.Empty);
                    AppendString(ref stateHash, field.Name);
                    AppendUInt64(ref stateHash, valueHash);
                    hashedMembers++;
                }
            }

            snapshot = new StateProbeSnapshot(
                authorityFrame,
                authoritySequence,
                simulationStep,
                entitiesList.Count,
                idHash,
                stateHash,
                hashedMembers);
            return true;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private FieldInfo[] GetStableCandidateFields(Type type) {
        FieldInfo[] cached;
        if (m_entityFieldCache.TryGetValue(type, out cached)) {
            return cached;
        }

        var fields = new List<FieldInfo>();
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {

                if (field.IsStatic
                    || field.IsNotSerialized
                    || typeof(Delegate).IsAssignableFrom(field.FieldType)
                    || !CanHashDeclaredType(field.FieldType, 0)) {
                    continue;
                }
                fields.Add(field);
            }
        }

        fields.Sort((a, b) => {
            var declaring = string.CompareOrdinal(
                a.DeclaringType?.FullName ?? string.Empty,
                b.DeclaringType?.FullName ?? string.Empty);
            return declaring != 0 ? declaring : string.CompareOrdinal(a.Name, b.Name);
        });

        cached = fields.ToArray();
        m_entityFieldCache[type] = cached;
        return cached;
    }

    private bool CanHashDeclaredType(Type type, int depth) {
        if (type == null || depth > MaxValueDepth) {
            return false;
        }

        if (type == typeof(string)
            || type == typeof(bool)
            || type == typeof(char)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type.IsEnum
            || IsIntegral(type)) {
            return true;
        }

        if (type.IsValueType) {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (field.IsStatic || field.IsNotSerialized) {
                    continue;
                }
                if (CanHashDeclaredType(field.FieldType, depth + 1)) {
                    return true;
                }
            }
            return false;
        }

        MemberInfo ignored;
        return TryGetStableIdMember(type, out ignored);
    }

    private bool TryAppendStableValue(ref ulong hash, object value, int depth) {
        if (depth > MaxValueDepth) {
            return false;
        }

        if (value == null) {
            AppendByte(ref hash, 0);
            return true;
        }

        var type = value.GetType();
        AppendString(ref hash, type.FullName ?? type.Name);

        if (value is string text) {
            AppendString(ref hash, text);
            return true;
        }
        if (value is bool boolean) {
            AppendByte(ref hash, boolean ? (byte)1 : (byte)0);
            return true;
        }
        if (value is char character) {
            AppendUInt64(ref hash, character);
            return true;
        }
        if (type.IsEnum) {
            AppendString(ref hash, Convert.ToString(value, CultureInfo.InvariantCulture));
            return true;
        }
        if (value is float singleValue) {
            AppendString(ref hash, singleValue.ToString("R", CultureInfo.InvariantCulture));
            return true;
        }
        if (value is double doubleValue) {
            AppendString(ref hash, doubleValue.ToString("R", CultureInfo.InvariantCulture));
            return true;
        }
        if (IsIntegral(type)) {
            AppendString(ref hash, Convert.ToString(value, CultureInfo.InvariantCulture));
            return true;
        }
        if (value is decimal decimalValue) {
            AppendString(ref hash, decimalValue.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        if (value is DateTime dateTime) {
            AppendInt64(ref hash, dateTime.Ticks);
            AppendInt64(ref hash, (int)dateTime.Kind);
            return true;
        }
        if (value is TimeSpan timeSpan) {
            AppendInt64(ref hash, timeSpan.Ticks);
            return true;
        }
        if (value is Guid guid) {
            AppendString(ref hash, guid.ToString("D"));
            return true;
        }

        if (type.IsValueType) {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var any = false;
            foreach (var field in fields) {
                if (field.IsStatic
                    || field.IsNotSerialized
                    || !CanHashDeclaredType(field.FieldType, depth + 1)) {
                    continue;
                }

                object nested;
                try {
                    nested = field.GetValue(value);
                }
                catch {
                    continue;
                }

                var nestedHash = FnvOffset;
                if (!TryAppendStableValue(ref nestedHash, nested, depth + 1)) {
                    continue;
                }

                AppendString(ref hash, field.Name);
                AppendUInt64(ref hash, nestedHash);
                any = true;
            }
            return any;
        }

        // Reference objects are only admitted when they expose an Id-like value.
        // This catches prototype selections (for example a selected product)
        // without traversing mutable object graphs or hashing object identity.
        MemberInfo idMember;
        if (!TryGetStableIdMember(type, out idMember)) {
            return false;
        }

        object idValue;
        try {
            var field = idMember as FieldInfo;
            if (field != null) {
                idValue = field.GetValue(value);
            }
            else {
                var property = (PropertyInfo)idMember;
                idValue = property.GetValue(value, null);
            }
        }
        catch {
            return false;
        }

        AppendString(ref hash, idMember.Name);
        return TryAppendStableValue(ref hash, idValue, depth + 1);
    }

    private bool TryGetStableIdMember(Type type, out MemberInfo member) {
        if (m_stableIdMemberCache.TryGetValue(type, out member)) {
            return true;
        }
        if (m_noStableIdMember.Contains(type)) {
            member = null;
            return false;
        }

        var names = new[] { "Id", "ID", "ProtoId", "PrototypeId", "m_id" };
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var name in names) {
                var field = current.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null && !field.IsStatic) {
                    m_stableIdMemberCache[type] = field;
                    member = field;
                    return true;
                }
            }
        }

        // GetProperty(name) can throw AmbiguousMatchException when COI types hide an
        // Id property in a derived class. Enumerate properties and pick a concrete
        // getter instead so the diagnostic probe cannot fail on that shape.
        foreach (var property in type.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {

            if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) {
                continue;
            }

            var nameMatches = false;
            for (var i = 0; i < names.Length; i++) {
                if (string.Equals(property.Name, names[i], StringComparison.Ordinal)) {
                    nameMatches = true;
                    break;
                }
            }
            if (!nameMatches) {
                continue;
            }

            m_stableIdMemberCache[type] = property;
            member = property;
            return true;
        }

        m_noStableIdMember.Add(type);
        member = null;
        return false;
    }

    private static bool IsIntegral(Type type) {
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong);
    }

    private static void AppendString(ref ulong hash, string value) {
        value = value ?? string.Empty;
        for (var i = 0; i < value.Length; i++) {
            var ch = value[i];
            AppendByte(ref hash, (byte)(ch & 0xff));
            AppendByte(ref hash, (byte)(ch >> 8));
        }
        AppendByte(ref hash, 0xff);
    }

    private static void AppendInt64(ref ulong hash, long value) {
        AppendUInt64(ref hash, unchecked((ulong)value));
    }

    private static void AppendUInt64(ref ulong hash, ulong value) {
        for (var i = 0; i < 8; i++) {
            AppendByte(ref hash, (byte)(value & 0xff));
            value >>= 8;
        }
    }

    private static void AppendByte(ref ulong hash, byte value) {
        hash ^= value;
        hash *= FnvPrime;
    }
}
