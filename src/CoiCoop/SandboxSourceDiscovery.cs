using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mafi;
using Mafi.Core.Entities;

namespace CoiCoop;

/// <summary>
/// Development-only targeted audit for sandbox/infinite material sources that
/// appear to mutate entity state outside InputScheduler. It does not modify the
/// simulation; it only reports candidate entity fields when their values change.
/// </summary>
internal sealed class SandboxSourceDiscovery {
    private sealed class Candidate {
        public IEntity Entity { get; }
        public FieldInfo[] Fields { get; }

        public Candidate(IEntity entity, FieldInfo[] fields) {
            Entity = entity;
            Fields = fields;
        }
    }

    private readonly DependencyResolver m_resolver;
    private readonly List<Candidate> m_candidates = new List<Candidate>();
    private readonly HashSet<int> m_candidateIds = new HashSet<int>();
    private EntitiesManager m_entities;
    private string m_lastObservation;
    private int m_lastRefreshMs;

    public SandboxSourceDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public int CandidateCount => m_candidates.Count;

    public string CandidateSummary {
        get {
            if (m_candidates.Count == 0) return "none";
            var items = new List<string>();
            foreach (var candidate in m_candidates) {
                items.Add(candidate.Entity.Id.Value + ":" + candidate.Entity.GetType().FullName);
                if (items.Count >= 20) break;
            }
            return string.Join(", ", items);
        }
    }

    public bool Refresh(bool force = false) {
        var now = Environment.TickCount;
        if (!force && unchecked(now - m_lastRefreshMs) < 3000) return false;
        m_lastRefreshMs = now;

        if (m_entities == null) {
            EntitiesManager entities;
            if (!m_resolver.TryGetResolvedDependency<EntitiesManager>(out entities) || entities == null) {
                return false;
            }
            m_entities = entities;
        }

        var before = m_candidates.Count;
        foreach (IEntity entity in m_entities.Entities) {
            if (entity == null || m_candidateIds.Contains(entity.Id.Value)) continue;

            var type = entity.GetType();
            var fields = GetInterestingFields(type);
            if (fields.Length == 0) continue;
            if (!LooksLikeSandboxSource(type) && !HasStrongSourceField(fields)) continue;

            m_candidateIds.Add(entity.Id.Value);
            m_candidates.Add(new Candidate(entity, fields));
            if (m_candidates.Count >= 64) break;
        }

        m_candidates.Sort((a, b) => a.Entity.Id.Value.CompareTo(b.Entity.Id.Value));
        return before != m_candidates.Count;
    }

    public bool TryCaptureChanged(out string observation) {
        observation = null;
        if (m_candidates.Count == 0) return false;

        var builder = new StringBuilder(1024);
        foreach (var candidate in m_candidates) {
            var entity = candidate.Entity;
            if (entity == null) continue;

            var wrote = false;
            foreach (var field in candidate.Fields) {
                object value;
                try {
                    value = field.GetValue(entity);
                }
                catch {
                    continue;
                }

                string formatted;
                if (!TryFormatValue(value, out formatted)) continue;

                if (!wrote) {
                    if (builder.Length > 0) builder.Append(" || ");
                    builder.Append(entity.Id.Value)
                        .Append(':')
                        .Append(entity.GetType().FullName);
                    wrote = true;
                }
                builder.Append('|').Append(field.Name).Append('=').Append(formatted);
            }
        }

        if (builder.Length == 0) return false;
        var current = builder.ToString();
        if (string.Equals(current, m_lastObservation, StringComparison.Ordinal)) return false;

        m_lastObservation = current;
        observation = current;
        return true;
    }

    private static bool LooksLikeSandboxSource(Type type) {
        var name = type?.FullName ?? string.Empty;
        return name.IndexOf("sandbox", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("generator", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("infinite", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("infinity", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("cheat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasStrongSourceField(FieldInfo[] fields) {
        foreach (var field in fields) {
            var name = field.Name ?? string.Empty;
            if (name.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("outputProduct", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("sourceProduct", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("generatedProduct", StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }
        }
        return false;
    }

    private static FieldInfo[] GetInterestingFields(Type type) {
        var result = new List<FieldInfo>();
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {

                if (field.IsStatic || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                var combined = (field.Name ?? string.Empty) + " " + (field.FieldType?.FullName ?? string.Empty);
                if (combined.IndexOf("product", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("material", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("resource", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("output", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("proto", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("selected", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) < 0
                    && combined.IndexOf("active", StringComparison.OrdinalIgnoreCase) < 0) {
                    continue;
                }
                result.Add(field);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result.ToArray();
    }

    private static bool TryFormatValue(object value, out string formatted) {
        formatted = null;
        if (value == null) {
            formatted = "null";
            return true;
        }

        var type = value.GetType();
        if (value is string || value is bool || value is char || type.IsPrimitive || type.IsEnum || type.IsValueType) {
            try {
                formatted = value.ToString();
                return formatted != null && formatted.Length <= 200;
            }
            catch {
                return false;
            }
        }

        object id;
        if (TryReadId(value, out id)) {
            formatted = type.Name + "#" + (id ?? "null");
            return true;
        }
        return false;
    }

    private static bool TryReadId(object value, out object id) {
        id = null;
        if (value == null) return false;

        var names = new[] { "Id", "ID", "ProtoId", "PrototypeId", "m_id" };
        var type = value.GetType();
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                for (var i = 0; i < names.Length; i++) {
                    if (!string.Equals(field.Name, names[i], StringComparison.Ordinal)) continue;
                    try {
                        id = field.GetValue(value);
                        return true;
                    }
                    catch {
                        return false;
                    }
                }
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
            for (var i = 0; i < names.Length; i++) {
                if (!string.Equals(property.Name, names[i], StringComparison.Ordinal)) continue;
                try {
                    id = property.GetValue(value, null);
                    return true;
                }
                catch {
                    return false;
                }
            }
        }
        return false;
    }
}
