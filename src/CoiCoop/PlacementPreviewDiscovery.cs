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
/// Targeted runtime bridge for COI 0.8.7 ordinary building placement.
///
/// Unlike the old diagnostic scanner, this class never walks all Unity objects and
/// never serializes reflection dumps. It keeps only the handful of known placement
/// controller instances. Single placements still use the proven helper transform;
/// multi-entity mass placements read the active placer's m_entityPreviews set so
/// drag rows / duplicated buildings / blueprints can be mirrored piece-by-piece.
/// </summary>
internal sealed class PlacementPreviewDiscovery {
    private const string MassPlacerTypeName
        = "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.StaticEntityMassPlacer";
    private const int MaxMultiPreviewPieces = 256;

    private static readonly string[] TargetTypeNames = {
        MassPlacerTypeName,
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntitySlotPlacerHelper",
        "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntityToolbox",
        "Mafi.Unity.InputControl.Factory.LayoutEntityPreviewManager",
        "Mafi.Unity.UiStatic.Controllers.LayoutEntityPlacing.LastUsedStaticEntityTransform"
    };

    internal sealed class Piece {
        public Proto Prototype { get; }
        public TileTransform Transform { get; }

        public Piece(Proto prototype, TileTransform transform) {
            Prototype = prototype;
            Transform = transform;
        }
    }

    internal sealed class CapturedSet {
        public IReadOnlyList<Piece> Pieces { get; }

        public CapturedSet(IReadOnlyList<Piece> pieces) {
            Pieces = pieces;
        }
    }

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
    /// Captures a multi-entity ordinary placement from StaticEntityMassPlacer.
    /// Vanilla stores m_entityPreviews as KeyValuePair&lt;IStaticEntityPreview,
    /// EntityConfigData&gt;. EntityConfigData carries the individual prototype and
    /// transform that ultimately become BatchCreateStaticEntitiesCmd items.
    ///
    /// We only claim this path when there are at least two live preview entries;
    /// ordinary single-object placement remains on TryCaptureBuildingGhost().
    /// </summary>
    public bool TryCaptureBuildingGhostSet(out CapturedSet state, out string error) {
        state = null;
        error = "no active multi-entity StaticEntityMassPlacer";

        Candidate active;
        if (!TryGetActiveMassPlacer(out active)) return false;

        object previewsValue;
        if (!TryReadMember(active.Instance, "m_entityPreviews", out previewsValue)
            || previewsValue == null) {
            error = "active StaticEntityMassPlacer.m_entityPreviews was not found";
            return false;
        }

        var enumerable = previewsValue as IEnumerable;
        if (enumerable == null) {
            error = "active StaticEntityMassPlacer.m_entityPreviews is not enumerable";
            return false;
        }

        var pieces = new List<Piece>();
        foreach (var item in enumerable) {
            if (item == null) continue;
            if (pieces.Count >= MaxMultiPreviewPieces) {
                error = "active mass placement has more than " + MaxMultiPreviewPieces + " preview pieces";
                return false;
            }

            object key = null;
            object value = null;
            TryReadMember(item, "Key", out key);
            TryReadMember(item, "Value", out value);

            Proto prototype = null;
            object entityProto;
            if (key != null
                && TryReadMember(key, "EntityProto", out entityProto)
                && entityProto is Proto directProto) {
                prototype = directProto;
            }
            if (prototype == null && value != null) {
                TryExtractPrototype(
                    value,
                    2,
                    new HashSet<object>(ReferenceEqualityComparer.Instance),
                    out prototype);
            }

            TileTransform transform = default(TileTransform);
            var foundTransform = value != null && TryExtractTileTransform(value, out transform);
            if (!foundTransform && key != null) {
                foundTransform = TryExtractTileTransform(key, out transform);
            }

            if (prototype == null || !foundTransform) {
                error = "could not read proto/transform from mass placement preview element "
                    + item.GetType().FullName;
                return false;
            }

            pieces.Add(new Piece(prototype, transform));
        }

        if (pieces.Count < 2) {
            error = "active StaticEntityMassPlacer has fewer than two preview pieces";
            return false;
        }

        state = new CapturedSet(pieces);
        error = null;
        return true;
    }

    /// <summary>
    /// Captures one ordinary building ghost from the currently active
    /// StaticEntityMassPlacer. The helper transform is deliberately retained for
    /// the single-object path because it is the exact live cursor transform.
    /// </summary>
    public bool TryCaptureBuildingGhost(
        out Proto prototype,
        out TileTransform transform,
        out string error) {

        prototype = null;
        transform = default(TileTransform);
        error = null;

        Candidate active;
        if (!TryGetActiveMassPlacer(out active)) {
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

    private bool TryGetActiveMassPlacer(out Candidate active) {
        active = null;
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
            return true;
        }
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

    private static bool TryExtractTileTransform(object value, out TileTransform transform) {
        transform = default(TileTransform);
        if (value == null) return false;
        if (value is TileTransform direct) {
            transform = direct;
            return true;
        }

        string[] preferredNames = { "Transform", "m_transform", "EntityTransform", "TileTransform" };
        for (var i = 0; i < preferredNames.Length; i++) {
            object memberValue;
            if (TryReadMember(value, preferredNames[i], out memberValue)
                && memberValue is TileTransform preferred) {
                transform = preferred;
                return true;
            }
        }

        for (var current = value.GetType(); current != null && current != typeof(object); current = current.BaseType) {
            FieldInfo[] fields;
            try {
                fields = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch {
                fields = Array.Empty<FieldInfo>();
            }
            for (var i = 0; i < fields.Length; i++) {
                if (fields[i].IsStatic) continue;
                var fieldType = Nullable.GetUnderlyingType(fields[i].FieldType) ?? fields[i].FieldType;
                if (fieldType != typeof(TileTransform)) continue;
                object fieldValue;
                try { fieldValue = fields[i].GetValue(value); }
                catch { continue; }
                if (fieldValue is TileTransform fieldTransform) {
                    transform = fieldTransform;
                    return true;
                }
            }

            PropertyInfo[] properties;
            try {
                properties = current.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch {
                properties = Array.Empty<PropertyInfo>();
            }
            for (var i = 0; i < properties.Length; i++) {
                if (properties[i].GetIndexParameters().Length != 0 || properties[i].GetGetMethod(true) == null) continue;
                var propertyType = Nullable.GetUnderlyingType(properties[i].PropertyType) ?? properties[i].PropertyType;
                if (propertyType != typeof(TileTransform)) continue;
                object propertyValue;
                try { propertyValue = properties[i].GetValue(value, null); }
                catch { continue; }
                if (propertyValue is TileTransform propertyTransform) {
                    transform = propertyTransform;
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
