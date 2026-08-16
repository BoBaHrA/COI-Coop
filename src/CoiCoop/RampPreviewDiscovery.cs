using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Core.Prototypes;

namespace CoiCoop;

/// <summary>
/// Targeted reader for COI 0.8.7 ModularVehicleRampBuildController.
/// The vanilla controller already computes the complete future ramp as m_pieces,
/// a list of (ILayoutEntityProto, TileTransform) pairs. We mirror that list instead
/// of reimplementing ramp geometry/path selection.
/// </summary>
internal sealed class RampPreviewDiscovery {
    private const string ControllerTypeName = "Mafi.Unity.Ui.Controllers.Roads.ModularVehicleRampBuildController";
    private const int MaxPieces = 128;

    private readonly DependencyResolver m_resolver;
    private object m_controller;
    private bool m_initialResolveAttempted;

    internal sealed class Piece {
        public Proto Prototype { get; }
        public TileTransform Transform { get; }

        public Piece(Proto prototype, TileTransform transform) {
            Prototype = prototype;
            Transform = transform;
        }
    }

    internal sealed class CapturedState {
        public IReadOnlyList<Piece> Pieces { get; }
        public string ControllerState { get; }

        public CapturedState(IReadOnlyList<Piece> pieces, string controllerState) {
            Pieces = pieces;
            ControllerState = controllerState ?? string.Empty;
        }
    }

    public RampPreviewDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshResolvedCandidate(force: true);
    }

    public bool HasCandidate => m_controller != null;

    public bool ObserveInstance(object instance) {
        if (instance == null || !IsControllerType(instance.GetType())) return false;
        if (ReferenceEquals(m_controller, instance)) return false;
        m_controller = instance;
        return true;
    }

    public bool RefreshResolvedCandidate(bool force = false) {
        if (m_initialResolveAttempted && !force) return false;
        m_initialResolveAttempted = true;

        var before = m_controller;
        var type = FindLoadedType(ControllerTypeName);
        if (type == null) return false;

        try {
            var resolved = m_resolver.GetResolvedInstance(type);
            if (resolved.HasValue && resolved.Value != null) {
                m_controller = resolved.Value;
            }
        }
        catch { }

        return !ReferenceEquals(before, m_controller);
    }

    public bool TryCapture(out CapturedState state, out string error) {
        state = null;
        error = "no active modular vehicle ramp controller";

        if (m_controller == null) {
            RefreshResolvedCandidate(force: true);
            if (m_controller == null) return false;
        }

        object activeValue;
        if (!TryReadMember(m_controller, "IsActive", out activeValue)) {
            TryReadMember(m_controller, "m_isActive", out activeValue);
        }
        if (!(activeValue is bool isActive) || !isActive) return false;

        object piecesValue;
        if (!TryReadMember(m_controller, "m_pieces", out piecesValue) || piecesValue == null) {
            error = "active ramp controller has no m_pieces";
            return false;
        }

        var enumerable = piecesValue as IEnumerable;
        if (enumerable == null) {
            error = "ramp m_pieces is not enumerable";
            return false;
        }

        var pieces = new List<Piece>();
        foreach (var pair in enumerable) {
            if (pair == null) continue;
            if (pieces.Count >= MaxPieces) {
                error = "ramp preview has more than " + MaxPieces + " pieces";
                return false;
            }

            Proto prototype;
            TileTransform transform;
            if (!TryExtractPiece(pair, out prototype, out transform)) {
                error = "could not read proto/transform from ramp m_pieces element "
                    + pair.GetType().FullName;
                return false;
            }
            pieces.Add(new Piece(prototype, transform));
        }

        if (pieces.Count == 0) {
            error = "active ramp controller has no preview pieces yet";
            return false;
        }

        object controllerStateValue;
        var controllerState = TryReadMember(m_controller, "m_state", out controllerStateValue)
            && controllerStateValue != null
                ? controllerStateValue.ToString()
                : string.Empty;

        state = new CapturedState(pieces, controllerState);
        error = null;
        return true;
    }

    private static bool TryExtractPiece(
        object pair,
        out Proto prototype,
        out TileTransform transform) {

        prototype = null;
        transform = default(TileTransform);
        var foundTransform = false;

        for (var type = pair.GetType(); type != null && type != typeof(object); type = type.BaseType) {
            FieldInfo[] fields;
            try {
                fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch {
                fields = Array.Empty<FieldInfo>();
            }

            for (var i = 0; i < fields.Length; i++) {
                if (fields[i].IsStatic) continue;
                object value;
                try { value = fields[i].GetValue(pair); }
                catch { continue; }

                if (prototype == null && value is Proto proto) prototype = proto;
                if (!foundTransform && value is TileTransform tileTransform) {
                    transform = tileTransform;
                    foundTransform = true;
                }
            }

            PropertyInfo[] properties;
            try {
                properties = type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch {
                properties = Array.Empty<PropertyInfo>();
            }

            for (var i = 0; i < properties.Length; i++) {
                if (properties[i].GetIndexParameters().Length != 0
                    || properties[i].GetGetMethod(true) == null) continue;
                object value;
                try { value = properties[i].GetValue(pair, null); }
                catch { continue; }

                if (prototype == null && value is Proto proto) prototype = proto;
                if (!foundTransform && value is TileTransform tileTransform) {
                    transform = tileTransform;
                    foundTransform = true;
                }
            }
        }

        return prototype != null && foundTransform;
    }

    private static bool TryReadMember(object instance, string name, out object value) {
        value = null;
        if (instance == null) return false;

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
            catch {
                return false;
            }
        }
        return false;
    }

    private static bool IsControllerType(Type type) {
        return string.Equals(type?.FullName, ControllerTypeName, StringComparison.Ordinal);
    }

    private static Type FindLoadedType(string fullName) {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }
}
