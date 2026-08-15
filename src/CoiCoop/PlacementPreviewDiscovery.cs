using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mafi;
using Mafi.Core.Input;

namespace CoiCoop;

/// <summary>
/// Development bridge for locating the current COI placement/build controller
/// without hard-coding private 0.8.7 members. It samples only likely placement
/// fields and emits a compact text observation when those values change.
/// </summary>
internal sealed class PlacementPreviewDiscovery {
    private sealed class Candidate {
        public Type Type { get; }
        public object Instance { get; }
        public FieldInfo[] Fields { get; }

        public Candidate(Type type, object instance, FieldInfo[] fields) {
            Type = type;
            Instance = instance;
            Fields = fields;
        }
    }

    private readonly DependencyResolver m_resolver;
    private readonly List<Candidate> m_candidates = new List<Candidate>();
    private string m_lastObservation;

    public PlacementPreviewDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        DiscoverCandidates();
    }

    public int CandidateCount => m_candidates.Count;

    public string CandidateSummary {
        get {
            if (m_candidates.Count == 0) return "none";
            var names = new string[m_candidates.Count];
            for (var i = 0; i < m_candidates.Count; i++) names[i] = m_candidates[i].Type.FullName;
            return string.Join(", ", names);
        }
    }

    public bool TryCaptureChanged(out string observation) {
        observation = null;
        if (m_candidates.Count == 0) return false;

        var builder = new StringBuilder(512);
        foreach (var candidate in m_candidates) {
            var wroteCandidate = false;
            foreach (var field in candidate.Fields) {
                object value;
                try {
                    value = field.GetValue(candidate.Instance);
                }
                catch {
                    continue;
                }

                string formatted;
                if (!TryFormatValue(value, out formatted)) continue;

                if (!wroteCandidate) {
                    if (builder.Length > 0) builder.Append(" || ");
                    builder.Append(candidate.Type.FullName);
                    wroteCandidate = true;
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

    private void DiscoverCandidates() {
        Type[] types;
        try {
            types = typeof(InputScheduler).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex) {
            types = ex.Types ?? Array.Empty<Type>();
        }

        Array.Sort(types, (a, b) => string.CompareOrdinal(a?.FullName, b?.FullName));
        foreach (var type in types) {
            if (type == null || !LooksLikePlacementController(type)) continue;

            object instance;
            try {
                var resolved = m_resolver.GetResolvedInstance(type);
                if (!resolved.HasValue) continue;
                instance = resolved.Value;
            }
            catch {
                continue;
            }

            var fields = GetInterestingFields(type);
            if (fields.Length == 0) continue;
            m_candidates.Add(new Candidate(type, instance, fields));
            if (m_candidates.Count >= 16) break;
        }
    }

    private static bool LooksLikePlacementController(Type type) {
        var name = type.FullName ?? type.Name;
        if (name.IndexOf("Cmd", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        var actionWord = name.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Place", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Placement", StringComparison.OrdinalIgnoreCase) >= 0;
        var controllerWord = name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0;
        return actionWord && controllerWord;
    }

    private static FieldInfo[] GetInterestingFields(Type type) {
        var result = new List<FieldInfo>();
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {

                if (field.IsStatic || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                if (!LooksLikePlacementField(field.Name, field.FieldType)) continue;
                result.Add(field);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result.ToArray();
    }

    private static bool LooksLikePlacementField(string fieldName, Type fieldType) {
        var name = fieldName ?? string.Empty;
        var typeName = fieldType?.FullName ?? string.Empty;
        var combined = name + " " + typeName;

        return combined.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("position", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("rotation", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("orientation", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("direction", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("prototype", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("proto", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("layout", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("placement", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryFormatValue(object value, out string formatted) {
        formatted = null;
        if (value == null) {
            formatted = "null";
            return true;
        }

        var type = value.GetType();
        if (value is string || value is bool || value is char || type.IsEnum || type.IsPrimitive || type.IsValueType) {
            try {
                formatted = value.ToString();
                return formatted != null && formatted.Length <= 180;
            }
            catch {
                return false;
            }
        }

        object stableId;
        if (TryReadStableId(value, out stableId)) {
            formatted = type.Name + "#" + (stableId ?? "null");
            return true;
        }

        return false;
    }

    private static bool TryReadStableId(object value, out object id) {
        id = null;
        var type = value.GetType();
        var names = new[] { "Id", "ID", "ProtoId", "PrototypeId", "m_id" };

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
            var matched = false;
            for (var i = 0; i < names.Length; i++) {
                if (string.Equals(property.Name, names[i], StringComparison.Ordinal)) {
                    matched = true;
                    break;
                }
            }
            if (!matched) continue;
            try {
                id = property.GetValue(value, null);
                return true;
            }
            catch {
                return false;
            }
        }

        return false;
    }
}
