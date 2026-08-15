using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mafi;

namespace CoiCoop;

/// <summary>
/// Development bridge for locating the current COI placement/build controller
/// without hard-coding private 0.8.7 members. Placement UI does not necessarily
/// live in Mafi.Core, so discovery scans all currently loaded game assemblies,
/// inspects resolver storage, and accepts ObjectInstantiated callbacks.
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
    private readonly HashSet<object> m_candidateInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
    private string m_lastObservation;
    private int m_lastRefreshMs;

    public PlacementPreviewDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshResolvedCandidates(force: true);
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

    public bool ObserveInstance(object instance) {
        if (instance == null) return false;
        var type = instance.GetType();
        if (!LooksLikePlacementController(type)) return false;
        return TryAddCandidate(type, instance);
    }

    public bool RefreshResolvedCandidates(bool force = false) {
        if (!force && m_candidates.Count > 0) return false;

        var now = Environment.TickCount;
        if (!force && unchecked(now - m_lastRefreshMs) < 2000) return false;
        m_lastRefreshMs = now;

        var before = m_candidates.Count;
        ScanResolverStorage();
        if (m_candidates.Count == 0) {
            ScanLoadedGameAssemblies();
        }
        return m_candidates.Count != before;
    }

    public bool TryCaptureChanged(out string observation) {
        observation = null;
        if (m_candidates.Count == 0) return false;

        var builder = new StringBuilder(768);
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

    private void ScanResolverStorage() {
        try {
            var resolverType = m_resolver.GetType();
            foreach (var field in resolverType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                object value;
                try { value = field.GetValue(m_resolver); }
                catch { continue; }
                ScanContainerValue(value, 0);
                if (m_candidates.Count >= 32) return;
            }
        }
        catch {
            // Discovery is best-effort and must never affect simulation.
        }
    }

    private void ScanContainerValue(object value, int depth) {
        if (value == null || depth > 2 || m_candidates.Count >= 32) return;

        if (ObserveInstance(value)) return;
        if (value is string) return;

        var dictionary = value as IDictionary;
        if (dictionary != null) {
            var seen = 0;
            foreach (DictionaryEntry entry in dictionary) {
                ScanContainerValue(entry.Value, depth + 1);
                if (++seen >= 4096 || m_candidates.Count >= 32) break;
            }
            return;
        }

        var enumerable = value as IEnumerable;
        if (enumerable != null) {
            var seen = 0;
            try {
                foreach (var item in enumerable) {
                    if (item == null) continue;

                    var itemType = item.GetType();
                    var valueProperty = itemType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    if (valueProperty != null && valueProperty.GetIndexParameters().Length == 0) {
                        try {
                            ScanContainerValue(valueProperty.GetValue(item, null), depth + 1);
                        }
                        catch {
                            ScanContainerValue(item, depth + 1);
                        }
                    }
                    else {
                        ScanContainerValue(item, depth + 1);
                    }

                    if (++seen >= 4096 || m_candidates.Count >= 32) break;
                }
            }
            catch {
                // Ignore enumerators that are not safe outside their owner.
            }
        }
    }

    private void ScanLoadedGameAssemblies() {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!LooksLikeGameAssembly(assembly)) continue;

            Type[] types;
            try {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex) {
                types = ex.Types ?? Array.Empty<Type>();
            }
            catch {
                continue;
            }

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

                TryAddCandidate(type, instance);
                if (m_candidates.Count >= 32) break;
            }
            if (m_candidates.Count >= 32) break;
        }
    }

    private bool TryAddCandidate(Type type, object instance) {
        if (type == null || instance == null || m_candidateInstances.Contains(instance)) return false;

        var fields = GetInterestingFields(type);
        if (fields.Length == 0) return false;

        m_candidateInstances.Add(instance);
        m_candidates.Add(new Candidate(type, instance, fields));
        m_candidates.Sort((a, b) => string.CompareOrdinal(a.Type.FullName, b.Type.FullName));
        return true;
    }

    private static bool LooksLikeGameAssembly(Assembly assembly) {
        if (assembly == null) return false;
        var name = assembly.GetName().Name ?? string.Empty;
        return name.StartsWith("Mafi", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("Captain", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Industry", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikePlacementController(Type type) {
        if (type == null || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) return false;

        var name = type.FullName ?? type.Name;
        if (name.IndexOf("Cmd", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        var actionWord = name.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Place", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Placement", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Construction", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Entity", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Transport", StringComparison.OrdinalIgnoreCase) >= 0;
        var controllerWord = name.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Tool", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Planner", StringComparison.OrdinalIgnoreCase) >= 0;
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

        result.Sort((a, b) => {
            var declaring = string.CompareOrdinal(a.DeclaringType?.FullName ?? string.Empty, b.DeclaringType?.FullName ?? string.Empty);
            return declaring != 0 ? declaring : string.CompareOrdinal(a.Name, b.Name);
        });
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
            || combined.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("building", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("transport", StringComparison.OrdinalIgnoreCase) >= 0;
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
                return formatted != null && formatted.Length <= 240;
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
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
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
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
