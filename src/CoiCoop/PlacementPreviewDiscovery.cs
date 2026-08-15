using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mafi;

namespace CoiCoop;

/// <summary>
/// Lightweight runtime bridge for the handful of COI 0.8.7 placement objects
/// observed during live testing. The previous broad scanner sampled dozens of
/// unrelated controllers and built ~18 KB reflection snapshots on the game
/// thread; this version keeps only targeted layout-placement types and emits
/// field deltas rather than whole-object dumps.
/// </summary>
internal sealed class PlacementPreviewDiscovery {
    private sealed class Candidate {
        public int Id { get; }
        public Type Type { get; }
        public object Instance { get; }
        public FieldInfo[] Fields { get; }
        public Dictionary<FieldInfo, string> LastValues { get; }
            = new Dictionary<FieldInfo, string>();
        public bool BaselineReady { get; set; }

        public Candidate(int id, Type type, object instance, FieldInfo[] fields) {
            Id = id;
            Type = type;
            Instance = instance;
            Fields = fields;
        }
    }

    private static readonly string[] TargetTypeNames = {
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.StaticEntityMassPlacer",
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntitySlotPlacerHelper",
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntityToolbox",
        "Mafi.Unity.InputControl.Factory.LayoutEntityPreviewManager",
        "Mafi.Unity.UiStatic.Controllers.LayoutEntityPlacing.LastUsedStaticEntityTransform"
    };

    private readonly DependencyResolver m_resolver;
    private readonly List<Candidate> m_candidates = new List<Candidate>();
    private readonly HashSet<object> m_candidateInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
    private int m_nextCandidateId;
    private bool m_initialResolveAttempted;

    public PlacementPreviewDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshResolvedCandidates(force: true);
    }

    public int CandidateCount => m_candidates.Count;

    public string CandidateSummary {
        get {
            if (m_candidates.Count == 0) return "none";
            var builder = new StringBuilder(512);
            var shown = 0;
            foreach (var candidate in m_candidates) {
                if (shown++ >= 12) {
                    builder.Append(" ...");
                    break;
                }
                if (builder.Length > 0) builder.Append(", ");
                builder.Append('#').Append(candidate.Id)
                    .Append(':').Append(candidate.Type.Name)
                    .Append('[');
                for (var i = 0; i < candidate.Fields.Length; i++) {
                    if (i > 0) builder.Append(',');
                    builder.Append(candidate.Fields[i].Name);
                }
                builder.Append(']');
            }
            return builder.ToString();
        }
    }

    public bool ObserveInstance(object instance) {
        if (instance == null) return false;
        var type = instance.GetType();
        if (!IsTargetType(type)) return false;
        return TryAddCandidate(type, instance);
    }

    /// <summary>
    /// Resolve the known target types directly by full name. No assembly GetTypes
    /// walk and no resolver-container traversal are performed.
    /// </summary>
    public bool RefreshResolvedCandidates(bool force = false) {
        if (m_initialResolveAttempted && !force) return false;
        m_initialResolveAttempted = true;

        var before = m_candidates.Count;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            for (var i = 0; i < TargetTypeNames.Length; i++) {
                Type type;
                try { type = assembly.GetType(TargetTypeNames[i], false); }
                catch { continue; }
                if (type == null) continue;

                object instance;
                try {
                    var resolved = m_resolver.GetResolvedInstance(type);
                    if (!resolved.HasValue) continue;
                    instance = resolved.Value;
                }
                catch {
                    continue;
                }
                TryAddCandidate(type, instance);
            }
        }
        return before != m_candidates.Count;
    }

    /// <summary>
    /// Returns only fields whose formatted value changed after the baseline.
    /// Typical payload is tens/hundreds of bytes instead of the previous 18 KB.
    /// </summary>
    public bool TryCaptureChanged(out string observation) {
        observation = null;
        if (m_candidates.Count == 0) return false;

        var builder = new StringBuilder(256);
        foreach (var candidate in m_candidates) {
            var candidateBuilder = new StringBuilder(128);
            var hadBaseline = candidate.BaselineReady;

            foreach (var field in candidate.Fields) {
                object value;
                try { value = field.GetValue(candidate.Instance); }
                catch { continue; }

                string formatted;
                if (!TryFormatValue(value, out formatted)) continue;

                string previous;
                var hasPrevious = candidate.LastValues.TryGetValue(field, out previous);
                candidate.LastValues[field] = formatted;

                if (!hadBaseline || !hasPrevious || string.Equals(previous, formatted, StringComparison.Ordinal)) {
                    continue;
                }

                candidateBuilder.Append('|').Append(field.Name).Append('=').Append(formatted);
            }

            candidate.BaselineReady = true;
            if (candidateBuilder.Length == 0) continue;

            if (builder.Length > 0) builder.Append(" || ");
            builder.Append('#').Append(candidate.Id)
                .Append(':').Append(candidate.Type.FullName)
                .Append(candidateBuilder);
        }

        if (builder.Length == 0) return false;
        observation = builder.ToString();
        return true;
    }

    private bool TryAddCandidate(Type type, object instance) {
        if (type == null || instance == null || m_candidateInstances.Contains(instance)) return false;

        var fields = GetInterestingFields(type);
        if (fields.Length == 0) return false;

        m_candidateInstances.Add(instance);
        m_candidates.Add(new Candidate(m_nextCandidateId++, type, instance, fields));
        return true;
    }

    private static bool IsTargetType(Type type) {
        if (type == null) return false;
        var name = type.FullName ?? string.Empty;
        for (var i = 0; i < TargetTypeNames.Length; i++) {
            if (string.Equals(name, TargetTypeNames[i], StringComparison.Ordinal)) return true;
        }
        return false;
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

        result.Sort((a, b) => {
            var declaring = string.CompareOrdinal(
                a.DeclaringType?.FullName ?? string.Empty,
                b.DeclaringType?.FullName ?? string.Empty);
            return declaring != 0 ? declaring : string.CompareOrdinal(a.Name, b.Name);
        });
        return result.ToArray();
    }

    private static bool LooksLikePlacementField(string fieldName, Type fieldType) {
        var combined = (fieldName ?? string.Empty) + " " + (fieldType?.FullName ?? string.Empty);
        return combined.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("position", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("rotation", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("orientation", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("direction", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("transform", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("prototype", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("proto", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("layout", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0;
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
                return formatted != null && formatted.Length <= 220;
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
                    try { id = field.GetValue(value); return true; }
                    catch { return false; }
                }
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
            for (var i = 0; i < names.Length; i++) {
                if (!string.Equals(property.Name, names[i], StringComparison.Ordinal)) continue;
                try { id = property.GetValue(value, null); return true; }
                catch { return false; }
            }
        }
        return false;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object> {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
