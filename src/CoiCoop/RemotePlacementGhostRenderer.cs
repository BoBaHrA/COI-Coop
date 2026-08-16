using System;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Core.Prototypes;

namespace CoiCoop;

/// <summary>
/// Presentation-only renderer for the peer's current building placement.
/// All Unity/preview-manager access happens from the render thread. The sim/network
/// side only publishes immutable Proto + TileTransform snapshots into this bridge.
///
/// Mafi.Unity is intentionally accessed through reflection so the core mod does not
/// need a hard compile-time dependency on the experimental Unity modding API.
/// </summary>
internal sealed class RemotePlacementGhostRenderer : IDisposable {
    private const string PreviewManagerTypeName = "Mafi.Unity.InputControl.Factory.LayoutEntityPreviewManager";

    private readonly DependencyResolver m_resolver;
    private readonly Action<string> m_log;
    private readonly object m_stateLock = new object();

    private Proto m_pendingPrototype;
    private TileTransform m_pendingTransform;
    private bool m_pendingVisible;
    private long m_pendingRevision;
    private long m_appliedRevision = -1;

    private object m_previewManager;
    private MethodInfo m_createPreview;
    private object m_preview;
    private MethodInfo m_setTransform;
    private MethodInfo m_destroyPreview;
    private string m_currentProtoKey;
    private bool m_managerWaitLogged;
    private bool m_renderFailureLogged;

    public RemotePlacementGhostRenderer(DependencyResolver resolver, Action<string> log) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        m_log = log;
    }

    public void Publish(Proto prototype, TileTransform transform) {
        if (prototype == null) return;
        lock (m_stateLock) {
            m_pendingPrototype = prototype;
            m_pendingTransform = transform;
            m_pendingVisible = true;
            m_pendingRevision++;
        }
    }

    public void Clear() {
        lock (m_stateLock) {
            m_pendingPrototype = null;
            m_pendingVisible = false;
            m_pendingRevision++;
        }
    }

    /// <summary>
    /// Must be called from COI's render/main thread.
    /// </summary>
    public void RenderUpdate() {
        Proto prototype;
        TileTransform transform;
        bool visible;
        long revision;
        lock (m_stateLock) {
            revision = m_pendingRevision;
            if (revision == m_appliedRevision) return;
            prototype = m_pendingPrototype;
            transform = m_pendingTransform;
            visible = m_pendingVisible;
        }

        try {
            if (!visible || prototype == null) {
                DestroyPreview();
                m_appliedRevision = revision;
                m_renderFailureLogged = false;
                return;
            }

            if (!EnsurePreviewManager()) {
                // Do not mark the revision applied. The manager can be instantiated
                // after the first render frame while a save is still loading.
                return;
            }

            var protoKey = GetProtoKey(prototype);
            if (m_preview == null || !string.Equals(protoKey, m_currentProtoKey, StringComparison.Ordinal)) {
                DestroyPreview();
                m_preview = CreatePreview(prototype, transform);
                if (m_preview == null) {
                    throw new InvalidOperationException("LayoutEntityPreviewManager.CreatePreview returned null");
                }
                m_currentProtoKey = protoKey;
                CachePreviewMethods(m_preview.GetType());
                m_log?.Invoke("REMOTE GHOST CREATED proto=" + protoKey);
            }
            else {
                if (m_setTransform == null) CachePreviewMethods(m_preview.GetType());
                if (m_setTransform == null) {
                    throw new MissingMethodException(m_preview.GetType().FullName, "SetTransform(TileTransform)");
                }
                m_setTransform.Invoke(m_preview, new object[] { transform });
            }

            m_appliedRevision = revision;
            m_renderFailureLogged = false;
        }
        catch (TargetInvocationException ex) {
            var inner = ex.InnerException ?? ex;
            LogRenderFailure(inner.GetType().Name + ": " + inner.Message);
        }
        catch (Exception ex) {
            LogRenderFailure(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool EnsurePreviewManager() {
        if (m_previewManager != null && m_createPreview != null) return true;

        var managerType = FindLoadedType(PreviewManagerTypeName);
        if (managerType == null) {
            if (!m_managerWaitLogged) {
                m_managerWaitLogged = true;
                m_log?.Invoke("REMOTE GHOST WAITING - LayoutEntityPreviewManager type is not loaded yet");
            }
            return false;
        }

        object manager = null;
        try {
            var resolved = m_resolver.GetResolvedInstance(managerType);
            if (resolved.HasValue) manager = resolved.Value;
        }
        catch { }

        if (manager == null) {
            if (!m_managerWaitLogged) {
                m_managerWaitLogged = true;
                m_log?.Invoke("REMOTE GHOST WAITING - LayoutEntityPreviewManager is not resolved yet");
            }
            return false;
        }

        MethodInfo create = null;
        foreach (var method in managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (!string.Equals(method.Name, "CreatePreview", StringComparison.Ordinal)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length < 3) continue;
            if (parameters[2].ParameterType != typeof(TileTransform)) continue;
            create = method;
            // Current COI 0.8.7 has the 9-argument overload used by the vanilla
            // placement stack. Prefer it when multiple overloads are present.
            if (parameters.Length == 9) break;
        }

        if (create == null) {
            if (!m_managerWaitLogged) {
                m_managerWaitLogged = true;
                m_log?.Invoke("REMOTE GHOST WAITING - CreatePreview overload was not found");
            }
            return false;
        }

        m_previewManager = manager;
        m_createPreview = create;
        m_managerWaitLogged = false;
        m_log?.Invoke("REMOTE GHOST renderer ready via " + managerType.FullName);
        return true;
    }

    private object CreatePreview(Proto prototype, TileTransform transform) {
        var parameters = m_createPreview.GetParameters();
        var args = new object[parameters.Length];

        for (var i = 0; i < parameters.Length; i++) {
            var parameter = parameters[i];
            var name = parameter.Name ?? string.Empty;
            var type = parameter.ParameterType;

            if (i == 0 && type.IsInstanceOfType(prototype)) {
                args[i] = prototype;
                continue;
            }
            if (i == 1 && type.IsEnum) {
                args[i] = Enum.Parse(type, "FirstAndFinal", ignoreCase: false);
                continue;
            }
            if (type == typeof(TileTransform)) {
                args[i] = transform;
                continue;
            }
            if (type == typeof(bool)) {
                if (name.IndexOf("disableValidation", StringComparison.OrdinalIgnoreCase) >= 0) {
                    args[i] = true;
                }
                else if (name.IndexOf("disablePort", StringComparison.OrdinalIgnoreCase) >= 0) {
                    args[i] = true;
                }
                else if (name.IndexOf("disableHighlight", StringComparison.OrdinalIgnoreCase) >= 0) {
                    // Keep the normal placement material/highlight visible.
                    args[i] = false;
                }
                else {
                    args[i] = false;
                }
                continue;
            }
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null) {
                args[i] = null;
                continue;
            }

            if (parameter.HasDefaultValue) {
                args[i] = parameter.DefaultValue;
                continue;
            }
            args[i] = Activator.CreateInstance(type);
        }

        if (!parameters[0].ParameterType.IsInstanceOfType(prototype)) {
            throw new InvalidOperationException(
                "CreatePreview prototype parameter " + parameters[0].ParameterType.FullName
                + " does not accept " + prototype.GetType().FullName);
        }

        return m_createPreview.Invoke(m_previewManager, args);
    }

    private void CachePreviewMethods(Type previewType) {
        m_setTransform = null;
        m_destroyPreview = null;
        foreach (var method in previewType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (string.Equals(method.Name, "SetTransform", StringComparison.Ordinal)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(TileTransform)) {
                m_setTransform = method;
            }
            else if (string.Equals(method.Name, "DestroyAndReturnToPool", StringComparison.Ordinal)
                && method.GetParameters().Length == 0) {
                m_destroyPreview = method;
            }
        }
    }

    private void DestroyPreview() {
        if (m_preview == null) {
            m_currentProtoKey = null;
            return;
        }

        try {
            if (m_destroyPreview == null) CachePreviewMethods(m_preview.GetType());
            m_destroyPreview?.Invoke(m_preview, null);
        }
        catch (Exception ex) {
            m_log?.Invoke("REMOTE GHOST destroy warning: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally {
            m_preview = null;
            m_setTransform = null;
            m_destroyPreview = null;
            m_currentProtoKey = null;
        }
    }

    private void LogRenderFailure(string message) {
        if (m_renderFailureLogged) return;
        m_renderFailureLogged = true;
        m_log?.Invoke("REMOTE GHOST RENDER FAIL - " + message);
    }

    private static string GetProtoKey(Proto prototype) {
        if (prototype == null) return "<null>";
        try {
            return prototype.GetType().FullName + "#" + prototype.Id;
        }
        catch {
            return prototype.GetType().FullName ?? prototype.GetType().Name;
        }
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

    public void Dispose() {
        // Dispose is normally called while switching/loading scenes. The actual
        // Unity preview destruction is best-effort here; normal clears happen on
        // the render thread before disposal.
        DestroyPreview();
        lock (m_stateLock) {
            m_pendingPrototype = null;
            m_pendingVisible = false;
            m_pendingRevision++;
        }
    }
}
