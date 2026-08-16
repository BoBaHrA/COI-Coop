using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mafi;
using Mafi.Core;
using Mafi.Core.Prototypes;

namespace CoiCoop;

/// <summary>
/// Targeted runtime bridge for COI 0.8.7 building placement.
///
/// Unlike the old diagnostic scanner, this class never walks all Unity objects and
/// never serializes reflection dumps. It keeps only the handful of known placement
/// controller instances and, while one StaticEntityMassPlacer is active, reads the
/// exact m_helper.Transform plus the first active preview's EntityProto.
/// </summary>
internal sealed class PlacementPreviewDiscovery {
    private const string MassPlacerTypeName
        = "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.StaticEntityMassPlacer";

    private static readonly string[] TargetTypeNames = {
        MassPlacerTypeName,
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntitySlotPlacerHelper",
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntityToolbox",
        "Mafi.Unity.InputControl.Factory.LayoutEntityPreviewManager",
        "Mafi.Unity.UiStatic.Controllers.LayoutEntityPlacing.LastUsedStaticEntityTransform"
    };

    private sealed class Candidate {
        public int Id { get; }
        public Type Type { get; }
        public object Instance { get; }

        public Candidate(int id, Type type, object instance) {
            Id = id;
            Type = type;
            Instance = instance;
        }
    }

    private readonly DependencyResolver m_resolver;
    private readonly List<Candidate> m_candidates = new List<Candidate>();
    private readonly HashSet<object> m_candidateInstances
        = new HashSet<object>(ReferenceEqualityComparer.Instance);
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
            var builder = new StringBuilder(256);
            var shown = 0;
            foreach (var candidate in m_candidates) {
                if (shown++ >= 16) {
                    builder.Append(" ...");
                    break;
                }
                if (builder.Length > 0) builder.Append(", ");
                builder.Append('#').Append(candidate.Id)
                    .Append(':').Append(candidate.Type.Name);
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
    /// Captures one ordinary building ghost from the currently active
    /// StaticEntityMassPlacer. Blueprint/multi-entity and transport drag previews are
    /// deliberately left for later protocol variants.
    /// </summary>
    public bool TryCaptureBuildingGhost(
        out Proto prototype,
        out TileTransform transform,
        out string error) {

        prototype = null;
        transform = default(TileTransform);
        error = null;

        Candidate active = null;
        foreach (var candidate in m_candidates) {
            if (!string.Equals(candidate.Type.FullName, MassPlacerTypeName, StringComparison.Ordinal)) {
                continue;
            }

            object activeValue;
            if (!TryReadMember(candidate.Instance, "IsActive", out activeValue)
                || !(activeValue is bool isActive)
                || !isActive) {
                continue;
            }

            active = candidate;
            break;
        }

        if (active == null) {
            error = "no active StaticEntityMassPlacer";
            return false;
        }

        object helper;
        if (!TryReadMember(active.Instance, "m_helper", out helper) || helper == null) {
            error = "active StaticEntityMassPlacer.m_helper was not found";
            return false;
        }

        object transformValue;
        if (!TryReadMember(helper, "Transform", out transformValue)
            || !(transformValue is TileTransform capturedTransform)) {
            error = "active placement helper Transform was not available";
            return false;
        }

        object previews;
        if (TryReadMember(active.Instance, "m_entityPreviews", out previews)
            && TryExtractPrototypeFromPreviews(previews, out prototype)) {

            transform = capturedTransform;
            return true;
        }

        // Version-tolerant fallback: inspect only the single active placer and at
        // most two shallow levels looking for a Proto reference. This is tiny
        // compared with the removed all-world reflection scanner.
        if (TryExtractPrototype(active.Instance, 2, new HashSet<object>(ReferenceEqualityComparer.Instance), out prototype)) {
            transform = capturedTransform;
            return true;
        }

        error = "active StaticEntityMassPlacer prototype was not found";
        return false;
    }

    private static bool TryExtractPrototypeFromPreviews(object previews, out Proto prototype) {
        prototype = null;
        var enumerable = previews as IEnumerable;
        if (enumerable == null) return false;

        var inspected = 0;
        foreach (var item in enumerable) {
            if (item == null) continue;
            if (++inspected > 16) break;

            object key;
            if (TryReadMember(item, "Key", out key) && key != null) {
                object entityProto;
                if (TryReadMember(key, "EntityProto", out entityProto) && entityProto is Proto direct) {
                    prototype = direct;
                    return true;
                }
            }

            object value;
            if (TryReadMember(item, "Value", out value) && value != null) {
                if (TryExtractPrototype(
                    value,
                    2,
                    new HashSet<object>(ReferenceEqualityComparer.Instance),
                    out prototype)) {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryExtractPrototype(
        object value,
        int remainingDepth,
        HashSet<object> visited,
        out Proto prototype) {

        prototype = value as Proto;
        if (prototype != null) return true;
        if (value == null || remainingDepth < 0) return false;

        var type = value.GetType();
        if (!type.IsValueType) {
            if (visited.Contains(value)) return false;
            visited.Add(value);
        }

        // Handle Mafi.Option<T> and similar wrappers without knowing T.
        object hasValue;
        if (TryReadMember(value, "HasValue", out hasValue) && hasValue is bool has && has) {
            object optionValue;
            if ((TryReadMember(value, "ValueOrNull", out optionValue)
                    || TryReadMember(value, "Value", out optionValue))
                && optionValue != null
                && TryExtractPrototype(optionValue, remainingDepth - 1, visited, out prototype)) {
                return true;
            }
        }

        if (remainingDepth == 0) return false;

        if (!(value is string) && value is IEnumerable enumerable) {
            var count = 0;
            foreach (var item in enumerable) {
                if (++count > 16) break;
                if (item != null
                    && TryExtractPrototype(item, remainingDepth - 1, visited, out prototype)) {
                    return true;
                }
            }
        }

        for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {

                if (field.IsStatic || typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                if (!LooksPrototypeRelated(field.Name, field.FieldType)) continue;

                object nested;
                try { nested = field.GetValue(value); }
                catch { continue; }
                if (nested != null
                    && TryExtractPrototype(nested, remainingDepth - 1, visited, out prototype)) {
                    return true;
                }
            }
        }

        foreach (var property in type.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {

            if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
            if (!LooksPrototypeRelated(property.Name, property.PropertyType)) continue;

            object nested;
            try { nested = property.GetValue(value, null); }
            catch { continue; }
            if (nested != null
                && TryExtractPrototype(nested, remainingDepth - 1, visited, out prototype)) {
                return true;
            }
        }
        return false;
    }

    private static bool LooksPrototypeRelated(string memberName, Type memberType) {
        if (typeof(Proto).IsAssignableFrom(memberType)) return true;
        var combined = (memberName ?? string.Empty) + " " + (memberType?.FullName ?? string.Empty);
        return combined.IndexOf("proto", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("config", StringComparison.OrdinalIgnoreCase) >= 0
            || combined.IndexOf("entity", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool TryAddCandidate(Type type, object instance) {
        if (type == null || instance == null || m_candidateInstances.Contains(instance)) return false;
        m_candidateInstances.Add(instance);
        m_candidates.Add(new Candidate(m_nextCandidateId++, type, instance));
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

    private static bool TryReadMember(object instance, string name, out object value) {
        value = null;
        if (instance == null || string.IsNullOrEmpty(name)) return false;

        for (var current = instance.GetType(); current != null && current != typeof(object); current = current.BaseType) {
            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) {
                try { value = field.GetValue(instance); return true; }
                catch { return false; }
            }

            var property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null) {
                try { value = property.GetValue(instance, null); return true; }
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
