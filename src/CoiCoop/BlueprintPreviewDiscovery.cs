using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Core.Prototypes;

namespace CoiCoop;

/// <summary>
/// Targeted reader for the vanilla blueprint placement controller.
///
/// Blueprint libraries remain entirely local to each player. Once a blueprint is
/// selected, vanilla feeds its EntityConfigData items into a dedicated
/// StaticEntityMassPlacer. We mirror only that active placer's live preview set.
/// </summary>
internal sealed class BlueprintPreviewDiscovery {
    private const int MaxPieces = 256;

    private readonly DependencyResolver m_resolver;
    private readonly List<object> m_controllers = new List<object>();
    private readonly HashSet<object> m_seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
    private bool m_initialResolveAttempted;

    public BlueprintPreviewDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshResolvedCandidates(force: true);
    }

    public int CandidateCount => m_controllers.Count;

    public bool ObserveInstance(object instance) {
        if (instance == null || m_seen.Contains(instance)) return false;
        if (!LooksLikeBlueprintController(instance.GetType())) return false;
        if (!TryGetEntityPlacer(instance, out _)) return false;

        m_seen.Add(instance);
        m_controllers.Add(instance);
        return true;
    }

    public bool RefreshResolvedCandidates(bool force = false) {
        if (m_initialResolveAttempted && !force) return false;
        m_initialResolveAttempted = true;

        var before = m_controllers.Count;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }

            if (types == null) continue;
            for (var i = 0; i < types.Length; i++) {
                var type = types[i];
                if (type == null || !LooksLikeBlueprintController(type)) continue;

                try {
                    var resolved = m_resolver.GetResolvedInstance(type);
                    if (resolved.HasValue && resolved.Value != null) {
                        ObserveInstance(resolved.Value);
                    }
                }
                catch { }
            }
        }
        return before != m_controllers.Count;
    }

    public bool TryCapture(out PlacementPreviewDiscovery.CapturedSet state, out string error) {
        state = null;
        error = "no active blueprint StaticEntityMassPlacer";

        PlacementPreviewDiscovery.CapturedSet best = null;
        var bestCount = 0;
        string lastSpecificError = null;

        for (var i = 0; i < m_controllers.Count; i++) {
            var controller = m_controllers[i];
            object placer;
            if (!TryGetEntityPlacer(controller, out placer) || placer == null) continue;

            object activeValue;
            if (!TryReadMember(placer, "IsActive", out activeValue)
                || !(activeValue is bool isActive)
                || !isActive) {
                continue;
            }

            PlacementPreviewDiscovery.CapturedSet captured;
            string captureError;
            if (!TryCapturePlacer(placer, out captured, out captureError)) {
                if (!string.IsNullOrEmpty(captureError)) lastSpecificError = captureError;
                continue;
            }

            if (captured.Pieces.Count > bestCount) {
                best = captured;
                bestCount = captured.Pieces.Count;
            }
        }

        if (best != null) {
            state = best;
            error = null;
            return true;
        }

        if (!string.IsNullOrEmpty(lastSpecificError)) error = lastSpecificError;
        return false;
    }

    private static bool TryCapturePlacer(
        object placer,
        out PlacementPreviewDiscovery.CapturedSet state,
        out string error) {

        state = null;
        error = null;

        object previewsValue;
        if (!TryReadMember(placer, "m_entityPreviews", out previewsValue) || previewsValue == null) {
            error = "active blueprint placer has no m_entityPreviews";
            return false;
        }

        var enumerable = previewsValue as IEnumerable;
        if (enumerable == null) {
            error = "active blueprint placer m_entityPreviews is not enumerable";
            return false;
        }

        var pieces = new List<PlacementPreviewDiscovery.Piece>();
        var skipped = 0;
        foreach (var item in enumerable) {
            if (item == null) continue;
            if (pieces.Count >= MaxPieces) {
                error = "blueprint preview has more than " + MaxPieces + " supported pieces";
                return false;
            }

            object key = null;
            object value = null;
            TryReadMember(item, "Key", out key);
            TryReadMember(item, "Value", out value);

            Proto prototype;
            TileTransform transform;
            if (!TryExtractPrototype(key, value, out prototype)
                || !TryExtractTransform(key, value, out transform)) {
                skipped++;
                continue;
            }

            pieces.Add(new PlacementPreviewDiscovery.Piece(prototype, transform));
        }

        if (pieces.Count == 0) {
            error = skipped > 0
                ? "blueprint placer previews were present but none exposed a supported proto/transform"
                : "active blueprint placer has no preview pieces yet";
            return false;
        }

        state = new PlacementPreviewDiscovery.CapturedSet(pieces);
        if (skipped > 0) {
            // Partial presentation is better than suppressing the entire blueprint;
            // unsupported special visuals can be added as targeted adapters later.
            error = "captured " + pieces.Count + " blueprint pieces; skipped " + skipped;
        }
        return true;
    }

    private static bool TryExtractPrototype(object key, object value, out Proto prototype) {
        prototype = null;

        object direct;
        if (key != null
            && TryReadMember(key, "EntityProto", out direct)
            && direct is Proto keyProto) {
            prototype = keyProto;
            return true;
        }

        if (value != null) {
            object protoMember;
            if (TryReadMember(value, "Prototype", out protoMember)) {
                if (protoMember is Proto directProto) {
                    prototype = directProto;
                    return true;
                }
                if (TryUnwrapOptionProto(protoMember, out prototype)) return true;
            }

            if (TryReadMember(value, "Proto", out protoMember)) {
                if (protoMember is Proto directProto2) {
                    prototype = directProto2;
                    return true;
                }
                if (TryUnwrapOptionProto(protoMember, out prototype)) return true;
            }
        }

        return false;
    }

    private static bool TryExtractTransform(object key, object value, out TileTransform transform) {
        transform = default(TileTransform);

        if (key != null && TryFindTileTransform(key, 1, out transform)) return true;
        if (value != null && TryFindTileTransform(value, 1, out transform)) return true;
        return false;
    }

    private static bool TryFindTileTransform(object instance, int nestedDepth, out TileTransform transform) {
        transform = default(TileTransform);
        if (instance == null) return false;
        if (instance is TileTransform direct) {
            transform = direct;
            return true;
        }

        string[] preferred = {
            "Transform", "m_transform", "EntityTransform", "TileTransform",
            "m_addRequest", "AddRequest", "m_request", "Request"
        };

        for (var i = 0; i < preferred.Length; i++) {
            object member;
            if (!TryReadMember(instance, preferred[i], out member) || member == null) continue;
            if (member is TileTransform tileTransform) {
                transform = tileTransform;
                return true;
            }
            if (nestedDepth > 0 && TryFindTileTransform(member, nestedDepth - 1, out transform)) {
                return true;
            }
        }

        for (var type = instance.GetType(); type != null && type != typeof(object); type = type.BaseType) {
            FieldInfo[] fields;
            try {
                fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch { fields = Array.Empty<FieldInfo>(); }

            for (var i = 0; i < fields.Length; i++) {
                if (fields[i].IsStatic) continue;
                var effectiveType = Nullable.GetUnderlyingType(fields[i].FieldType) ?? fields[i].FieldType;
                if (effectiveType != typeof(TileTransform)) continue;
                object value;
                try { value = fields[i].GetValue(instance); } catch { continue; }
                if (value is TileTransform fieldTransform) {
                    transform = fieldTransform;
                    return true;
                }
            }

            PropertyInfo[] properties;
            try {
                properties = type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch { properties = Array.Empty<PropertyInfo>(); }

            for (var i = 0; i < properties.Length; i++) {
                if (properties[i].GetIndexParameters().Length != 0
                    || properties[i].GetGetMethod(true) == null) continue;
                var effectiveType = Nullable.GetUnderlyingType(properties[i].PropertyType)
                    ?? properties[i].PropertyType;
                if (effectiveType != typeof(TileTransform)) continue;
                object value;
                try { value = properties[i].GetValue(instance, null); } catch { continue; }
                if (value is TileTransform propertyTransform) {
                    transform = propertyTransform;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryUnwrapOptionProto(object option, out Proto prototype) {
        prototype = null;
        if (option == null) return false;

        object hasValue;
        if (!TryReadMember(option, "HasValue", out hasValue)
            || !(hasValue is bool has)
            || !has) return false;

        object value;
        if ((TryReadMember(option, "ValueOrNull", out value)
                || TryReadMember(option, "Value", out value))
            && value is Proto proto) {
            prototype = proto;
            return true;
        }
        return false;
    }

    private static bool TryGetEntityPlacer(object controller, out object placer) {
        placer = null;
        if (controller == null) return false;

        string[] names = { "m_entityPlacer", "EntityPlacer", "m_placer", "Placer" };
        for (var i = 0; i < names.Length; i++) {
            object value;
            if (TryReadMember(controller, names[i], out value)
                && value != null
                && LooksLikeStaticEntityMassPlacer(value.GetType())) {
                placer = value;
                return true;
            }
        }

        for (var type = controller.GetType(); type != null && type != typeof(object); type = type.BaseType) {
            FieldInfo[] fields;
            try {
                fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch { fields = Array.Empty<FieldInfo>(); }

            for (var i = 0; i < fields.Length; i++) {
                if (fields[i].IsStatic || !LooksLikeStaticEntityMassPlacer(fields[i].FieldType)) continue;
                try {
                    placer = fields[i].GetValue(controller);
                    if (placer != null) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static bool LooksLikeBlueprintController(Type type) {
        var name = type?.FullName ?? string.Empty;
        if (name.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) < 0) return false;
        return name.EndsWith("Controller", StringComparison.Ordinal)
            || name.IndexOf("BlueprintsController", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeStaticEntityMassPlacer(Type type) {
        var name = type?.FullName ?? string.Empty;
        return name.IndexOf("StaticEntityMassPlacer", StringComparison.Ordinal) >= 0;
    }

    private static bool TryReadMember(object instance, string name, out object value) {
        value = null;
        if (instance == null || string.IsNullOrEmpty(name)) return false;

        for (var type = instance.GetType(); type != null && type != typeof(object); type = type.BaseType) {
            try {
                var field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) {
                    value = field.GetValue(instance);
                    return true;
                }

                var property = type.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null
                    && property.GetIndexParameters().Length == 0
                    && property.GetGetMethod(true) != null) {
                    value = property.GetValue(instance, null);
                    return true;
                }
            }
            catch { return false; }
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
