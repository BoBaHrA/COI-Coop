using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;

namespace CoiCoop;

/// <summary>
/// Targeted reader for COI 0.8.7 multi-stage placement controllers.
/// It never walks the world or arbitrary Unity objects. It only keeps the three
/// known build-controller instances and reads the PreviewRequest already produced
/// by the game's own path preview.
/// </summary>
internal sealed class PathPreviewDiscovery {
    internal const string TransportFamily = "TRANSPORT";
    internal const string BridgeFamily = "BRIDGE";
    internal const string TrainFamily = "TRAIN";

    private const string TransportControllerTypeName = "Mafi.Unity.Ui.Controllers.TransportBuildController";
    private const string BridgeControllerTypeName = "Mafi.Unity.Ui.Controllers.Bridges.BridgeBuildController";
    private const string TrainControllerTypeName = "Mafi.Unity.Ui.Controllers.Trains.TrainTrackBuildController";

    private readonly DependencyResolver m_resolver;
    private readonly Dictionary<string, object> m_controllers = new Dictionary<string, object>(StringComparer.Ordinal);
    private bool m_initialResolveAttempted;

    internal sealed class CapturedState {
        public string Family { get; }
        public bool IsContinuation { get; }
        public object Request { get; }
        public Type RequestType { get; }
        public ThicknessTilesI RelativeHeight { get; }
        public string ControllerState { get; }

        public CapturedState(
            string family,
            bool isContinuation,
            object request,
            Type requestType,
            ThicknessTilesI relativeHeight,
            string controllerState) {

            Family = family;
            IsContinuation = isContinuation;
            Request = request;
            RequestType = requestType;
            RelativeHeight = relativeHeight;
            ControllerState = controllerState;
        }
    }

    public PathPreviewDiscovery(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshResolvedCandidates(force: true);
    }

    public int CandidateCount => m_controllers.Count;

    public bool ObserveInstance(object instance) {
        if (instance == null) return false;
        var family = FamilyForControllerType(instance.GetType());
        if (family == null) return false;

        object existing;
        if (m_controllers.TryGetValue(family, out existing) && ReferenceEquals(existing, instance)) {
            return false;
        }
        m_controllers[family] = instance;
        return true;
    }

    public bool RefreshResolvedCandidates(bool force = false) {
        if (m_initialResolveAttempted && !force) return false;
        m_initialResolveAttempted = true;

        var before = m_controllers.Count;
        TryResolveController(TransportControllerTypeName, TransportFamily);
        TryResolveController(BridgeControllerTypeName, BridgeFamily);
        TryResolveController(TrainControllerTypeName, TrainFamily);
        return before != m_controllers.Count;
    }

    public bool TryCapture(out CapturedState state, out string error) {
        state = null;
        error = "no active multi-stage path controller";

        string[] order = { TransportFamily, BridgeFamily, TrainFamily };
        for (var i = 0; i < order.Length; i++) {
            object controller;
            if (!m_controllers.TryGetValue(order[i], out controller) || controller == null) continue;

            object activeValue;
            if (!TryReadMember(controller, "IsActive", out activeValue)
                || !(activeValue is bool isActive)
                || !isActive) {
                continue;
            }

            var family = order[i];
            var previewFieldName = family == TransportFamily
                ? "m_transportPreview"
                : family == BridgeFamily
                    ? "m_bridgePreview"
                    : "m_trainTrackPreview";

            object preview;
            if (!TryReadMember(controller, previewFieldName, out preview) || preview == null) {
                error = family + " controller has no " + previewFieldName;
                return false;
            }

            object request;
            if (!TryReadFirstNonNullMember(
                    preview,
                    new[] { "m_requestedPreview", "m_processedRequest", "m_requestedPreviewOnSimThread" },
                    out request)
                || request == null) {

                error = family + " path preview has no current PreviewRequest";
                return false;
            }

            object relativeHeightValue;
            if (!TryReadMember(preview, "m_relativeHeight", out relativeHeightValue)
                || !(relativeHeightValue is ThicknessTilesI relativeHeight)) {

                error = family + " path preview has no relative height";
                return false;
            }

            object controllerStateValue;
            var controllerState = TryReadMember(controller, "m_state", out controllerStateValue)
                && controllerStateValue != null
                    ? controllerStateValue.ToString()
                    : string.Empty;

            var continuation = controllerState.IndexOf("Continuation", StringComparison.OrdinalIgnoreCase) >= 0;
            state = new CapturedState(
                family,
                continuation,
                request,
                request.GetType(),
                relativeHeight,
                controllerState);
            error = null;
            return true;
        }

        return false;
    }

    private void TryResolveController(string typeName, string family) {
        var type = FindLoadedType(typeName);
        if (type == null) return;

        try {
            var resolved = m_resolver.GetResolvedInstance(type);
            if (!resolved.HasValue || resolved.Value == null) return;
            m_controllers[family] = resolved.Value;
        }
        catch { }
    }

    private static string FamilyForControllerType(Type type) {
        var fullName = type?.FullName;
        if (string.Equals(fullName, TransportControllerTypeName, StringComparison.Ordinal)) return TransportFamily;
        if (string.Equals(fullName, BridgeControllerTypeName, StringComparison.Ordinal)) return BridgeFamily;
        if (string.Equals(fullName, TrainControllerTypeName, StringComparison.Ordinal)) return TrainFamily;
        return null;
    }

    private static bool TryReadFirstNonNullMember(object instance, string[] names, out object value) {
        value = null;
        for (var i = 0; i < names.Length; i++) {
            object candidate;
            if (TryReadMember(instance, names[i], out candidate) && candidate != null) {
                value = candidate;
                return true;
            }
        }
        return false;
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
                if (property != null && property.GetIndexParameters().Length == 0) {
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
