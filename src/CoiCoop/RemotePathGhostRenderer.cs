using System;
using System.Reflection;
using Mafi;

namespace CoiCoop;

/// <summary>
/// Presentation-only renderer for peer transport/bridge/train path planning.
/// It reuses COI's native PathFinding*Preview objects and never submits commands.
///
/// The vanilla preview object is shared with the local build controller, therefore
/// remote rendering is deliberately suppressed while the local player is actively
/// using the same path family. This avoids corrupting local placement state; a
/// future dedicated visualizer instance can remove that limitation.
/// </summary>
internal sealed class RemotePathGhostRenderer : IDisposable {
    private readonly DependencyResolver m_resolver;
    private readonly Action<string> m_log;
    private readonly object m_stateLock = new object();

    private PathPreviewWireCodec.DecodedState m_pending;
    private bool m_pendingVisible;
    private long m_pendingRevision;

    private string m_currentFamily;
    private object m_preview;
    private MethodInfo m_activate;
    private MethodInfo m_clear;
    private MethodInfo m_deactivate;
    private bool m_remoteActivated;
    private bool m_waitLogged;
    private bool m_failureLogged;
    private bool m_suppressedLogged;

    public RemotePathGhostRenderer(DependencyResolver resolver, Action<string> log) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        m_log = log;
    }

    public void Publish(PathPreviewWireCodec.DecodedState state) {
        if (state == null || state.Request == null) return;
        lock (m_stateLock) {
            m_pending = state;
            m_pendingVisible = true;
            m_pendingRevision++;
        }
    }

    public void Clear() {
        lock (m_stateLock) {
            m_pending = null;
            m_pendingVisible = false;
            m_pendingRevision++;
        }
    }

    /// <summary>Must be called from COI's render/main thread.</summary>
    public void RenderUpdate() {
        PathPreviewWireCodec.DecodedState state;
        bool visible;
        lock (m_stateLock) {
            state = m_pending;
            visible = m_pendingVisible;
        }

        try {
            if (!visible || state == null) {
                if (!IsCurrentLocalControllerActive()) {
                    ClearCurrentPreview();
                }
                m_failureLogged = false;
                m_suppressedLogged = false;
                return;
            }

            if (IsLocalControllerActive(state.Family)) {
                if (!m_suppressedLogged) {
                    m_suppressedLogged = true;
                    m_log?.Invoke(
                        "REMOTE PATH GHOST SUPPRESSED family=" + state.Family
                        + " because local player is using the same path tool");
                }
                return;
            }
            m_suppressedLogged = false;

            if (!EnsurePreview(state.Family)) return;
            if (!m_remoteActivated) {
                m_activate?.Invoke(m_preview, null);
                m_remoteActivated = true;
                m_log?.Invoke("REMOTE PATH GHOST renderer active family=" + state.Family);
            }

            var methodName = state.IsContinuation ? "ShowContinuationPreview" : "ShowStartPreview";
            var method = FindShowMethod(m_preview.GetType(), methodName, state.RequestType);
            if (method == null) {
                throw new MissingMethodException(
                    m_preview.GetType().FullName,
                    methodName + "(" + state.RequestType.FullName + ", ThicknessTilesI, ...)");
            }

            var args = BuildArguments(method, state.Request, state.RelativeHeight);
            method.Invoke(m_preview, args);
            m_failureLogged = false;
        }
        catch (TargetInvocationException ex) {
            var inner = ex.InnerException ?? ex;
            LogFailure(inner.GetType().Name + ": " + inner.Message);
        }
        catch (Exception ex) {
            LogFailure(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool EnsurePreview(string family) {
        if (m_preview != null && string.Equals(m_currentFamily, family, StringComparison.Ordinal)) {
            return true;
        }

        ClearCurrentPreview();

        var previewTypeName = PreviewTypeName(family);
        var previewType = FindLoadedType(previewTypeName);
        if (previewType == null) {
            LogWait("preview type not loaded for family=" + family);
            return false;
        }

        object preview = null;
        try {
            var resolved = m_resolver.GetResolvedInstance(previewType);
            if (resolved.HasValue) preview = resolved.Value;
        }
        catch { }

        if (preview == null) {
            LogWait("preview instance not resolved for family=" + family);
            return false;
        }

        m_preview = preview;
        m_currentFamily = family;
        m_activate = FindZeroArgMethod(previewType, "Activate");
        m_clear = FindZeroArgMethod(previewType, "Clear");
        m_deactivate = FindZeroArgMethod(previewType, "Deactivate");
        m_remoteActivated = false;
        m_waitLogged = false;
        m_log?.Invoke("REMOTE PATH GHOST ready family=" + family + " via " + previewType.FullName);
        return true;
    }

    private bool IsCurrentLocalControllerActive() {
        return !string.IsNullOrEmpty(m_currentFamily) && IsLocalControllerActive(m_currentFamily);
    }

    private bool IsLocalControllerActive(string family) {
        var controllerType = FindLoadedType(ControllerTypeName(family));
        if (controllerType == null) return false;

        object controller = null;
        try {
            var resolved = m_resolver.GetResolvedInstance(controllerType);
            if (resolved.HasValue) controller = resolved.Value;
        }
        catch { }
        if (controller == null) return false;

        object active;
        return TryReadMember(controller, "IsActive", out active)
            && active is bool isActive
            && isActive;
    }

    private void ClearCurrentPreview() {
        if (m_preview == null) {
            ResetCurrent();
            return;
        }

        try {
            m_clear?.Invoke(m_preview, null);
            if (m_remoteActivated) m_deactivate?.Invoke(m_preview, null);
        }
        catch (Exception ex) {
            m_log?.Invoke("REMOTE PATH GHOST clear warning: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally {
            ResetCurrent();
        }
    }

    private void ResetCurrent() {
        m_preview = null;
        m_activate = null;
        m_clear = null;
        m_deactivate = null;
        m_currentFamily = null;
        m_remoteActivated = false;
    }

    private static MethodInfo FindShowMethod(Type type, string methodName, Type requestType) {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length < 2) continue;
            if (parameters[0].ParameterType != requestType) continue;
            if (parameters[1].ParameterType != typeof(ThicknessTilesI)) continue;
            return method;
        }
        return null;
    }

    private static object[] BuildArguments(MethodInfo method, object request, ThicknessTilesI relativeHeight) {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];
        args[0] = request;
        args[1] = relativeHeight;

        for (var i = 2; i < parameters.Length; i++) {
            var type = parameters[i].ParameterType;
            if (type.IsByRef) type = type.GetElementType();

            if (type == null) {
                args[i] = null;
            }
            else if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null) {
                args[i] = null;
            }
            else {
                args[i] = Activator.CreateInstance(type);
            }
        }
        return args;
    }

    private static MethodInfo FindZeroArgMethod(Type type, string name) {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (string.Equals(method.Name, name, StringComparison.Ordinal)
                && method.GetParameters().Length == 0) {
                return method;
            }
        }
        return null;
    }

    private void LogWait(string message) {
        if (m_waitLogged) return;
        m_waitLogged = true;
        m_log?.Invoke("REMOTE PATH GHOST WAITING - " + message);
    }

    private void LogFailure(string message) {
        if (m_failureLogged) return;
        m_failureLogged = true;
        m_log?.Invoke("REMOTE PATH GHOST RENDER FAIL - " + message);
    }

    private static string PreviewTypeName(string family) {
        if (string.Equals(family, PathPreviewDiscovery.TransportFamily, StringComparison.Ordinal)) {
            return "Mafi.Unity.Ui.Controllers.PathFindingTransportPreview";
        }
        if (string.Equals(family, PathPreviewDiscovery.BridgeFamily, StringComparison.Ordinal)) {
            return "Mafi.Unity.Ui.Controllers.Bridges.PathFindingBridgePreview";
        }
        return "Mafi.Unity.Ui.Controllers.Trains.PathFindingTrainTrackPreview";
    }

    private static string ControllerTypeName(string family) {
        if (string.Equals(family, PathPreviewDiscovery.TransportFamily, StringComparison.Ordinal)) {
            return "Mafi.Unity.Ui.Controllers.TransportBuildController";
        }
        if (string.Equals(family, PathPreviewDiscovery.BridgeFamily, StringComparison.Ordinal)) {
            return "Mafi.Unity.Ui.Controllers.Bridges.BridgeBuildController";
        }
        return "Mafi.Unity.Ui.Controllers.Trains.TrainTrackBuildController";
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
            catch { return false; }
        }
        return false;
    }

    private static Type FindLoadedType(string fullName) {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }

    public void Dispose() {
        if (!IsCurrentLocalControllerActive()) ClearCurrentPreview();
        lock (m_stateLock) {
            m_pending = null;
            m_pendingVisible = false;
            m_pendingRevision++;
        }
    }
}
