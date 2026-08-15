using System;
using System.Text;
using CoiCoop.Networking;
using Mafi;
using Mafi.Core.Simulation;

namespace CoiCoop;

/// <summary>
/// Development bootstrap for real-time remote placement previews plus targeted
/// discovery of sandbox source state that bypasses InputScheduler.
/// </summary>
internal sealed class PlacementPreviewBootstrap : IDisposable {
    private const int SampleIntervalMs = 100;
    private const int SandboxSampleIntervalMs = 250;
    private const int MaxLoggedObservationChars = 900;
    private static readonly object s_lock = new object();
    private static PlacementPreviewBootstrap s_current;

    private readonly DependencyResolver m_resolver;
    private readonly int m_mode;
    private readonly int m_previewPort;
    private PlacementPreviewDiscovery m_discovery;
    private SandboxSourceDiscovery m_sandboxDiscovery;
    private PlacementPreviewSession m_session;
    private ISimLoopEvents m_simLoop;
    private int m_lastSampleMs;
    private int m_lastSandboxSampleMs;
    private int m_lastLoggedCandidateCount = -1;
    private int m_lastLoggedSandboxCandidateCount = -1;
    private bool m_hooked;
    private bool m_resolverObserved;

    private PlacementPreviewBootstrap(DependencyResolver resolver, int mode, int previewPort) {
        m_resolver = resolver;
        m_mode = mode;
        m_previewPort = previewPort;
    }

    public static void EnsureStarted(DependencyResolver resolver) {
        if (resolver == null) return;

        var mode = ReadEnvironmentInt("COI_COOP_MODE", 0);
        var mainPort = ReadEnvironmentInt("COI_COOP_PORT", 27015);
        if ((mode != 1 && mode != 2) || mainPort < 1024 || mainPort > 65534) return;

        lock (s_lock) {
            if (s_current != null && ReferenceEquals(s_current.m_resolver, resolver)) return;

            s_current?.Dispose();
            var next = new PlacementPreviewBootstrap(resolver, mode, mainPort + 1);
            s_current = next;
            next.Start();
        }
    }

    private void Start() {
        m_discovery = new PlacementPreviewDiscovery(m_resolver);
        m_sandboxDiscovery = new SandboxSourceDiscovery(m_resolver);
        m_session = new PlacementPreviewSession(
            m_mode == 1,
            m_previewPort,
            message => Log.Info("COI-Coop: " + message));
        m_session.Start();

        // Keep observing resolver objects for the whole session. Placement/input UI
        // controllers can be instantiated well after mod initialization.
        m_resolver.ObjectInstantiated += OnObjectInstantiated;
        m_resolverObserved = true;

        ISimLoopEvents simLoop;
        if (m_resolver.TryGetResolvedDependency<ISimLoopEvents>(out simLoop) && simLoop != null) {
            Attach(simLoop);
        }

        Log.Info(
            "COI-Coop: PREVIEW sidecar starting on 127.0.0.1:" + m_previewPort
            + " mode=" + (m_mode == 1 ? "HOST" : "CLIENT"));
    }

    private void OnObjectInstantiated(object instance) {
        if (instance == null) return;

        var simLoop = instance as ISimLoopEvents;
        if (simLoop != null) {
            Attach(simLoop);
        }

        if (m_discovery != null && m_discovery.ObserveInstance(instance)) {
            Log.Info(
                "COI-Coop: PREVIEW discovered runtime candidate "
                + instance.GetType().FullName);
        }
    }

    private void Attach(ISimLoopEvents simLoop) {
        if (m_hooked || simLoop == null) return;
        m_simLoop = simLoop;
        m_simLoop.UpdateEndForUi.AddNonSaveable(this, OnUpdateEndForUi);
        m_hooked = true;
        Log.Info("COI-Coop: PREVIEW UpdateEndForUi sampler attached");
    }

    private void OnUpdateEndForUi() {
        if (!ReferenceEquals(s_current, this) || m_session == null) return;

        PumpIncoming();
        if (!m_session.IsConnected) return;

        SamplePlacementPreview();
        SampleSandboxSources();
    }

    private void SamplePlacementPreview() {
        if (m_discovery == null) return;

        var changed = m_discovery.RefreshResolvedCandidates();
        if (changed || m_lastLoggedCandidateCount != m_discovery.CandidateCount) {
            m_lastLoggedCandidateCount = m_discovery.CandidateCount;
            Log.Info(
                "COI-Coop: PREVIEW discovery candidates=" + m_discovery.CandidateCount
                + " [" + m_discovery.CandidateSummary + "]");
        }

        var now = Environment.TickCount;
        if (unchecked(now - m_lastSampleMs) < SampleIntervalMs) return;
        m_lastSampleMs = now;

        string observation;
        if (!m_discovery.TryCaptureChanged(out observation)) return;

        var payload = Encoding.UTF8.GetBytes(observation);
        var revision = m_session.Publish("PLACEMENT_DISCOVERY", payload);
        Log.Info(
            "COI-Coop: PREVIEW TX rev=" + revision
            + " bytes=" + payload.Length
            + " " + Truncate(observation));
    }

    private void SampleSandboxSources() {
        if (m_sandboxDiscovery == null) return;

        var now = Environment.TickCount;
        if (unchecked(now - m_lastSandboxSampleMs) < SandboxSampleIntervalMs) return;
        m_lastSandboxSampleMs = now;

        var changedCandidates = m_sandboxDiscovery.Refresh();
        if (changedCandidates || m_lastLoggedSandboxCandidateCount != m_sandboxDiscovery.CandidateCount) {
            m_lastLoggedSandboxCandidateCount = m_sandboxDiscovery.CandidateCount;
            Log.Info(
                "COI-Coop: SANDBOX discovery candidates=" + m_sandboxDiscovery.CandidateCount
                + " [" + Truncate(m_sandboxDiscovery.CandidateSummary) + "]");
        }

        string observation;
        if (!m_sandboxDiscovery.TryCaptureChanged(out observation)) return;
        Log.Info("COI-Coop: SANDBOX STATE " + Truncate(observation));
    }

    private void PumpIncoming() {
        PlacementPreviewState peer;
        while (m_session.TryTakeLatestPeerState(out peer)) {
            if (peer == null) continue;

            if (string.Equals(peer.Kind, "NONE", StringComparison.Ordinal)) {
                Log.Info("COI-Coop: PREVIEW RX CLEAR rev=" + peer.Revision);
                continue;
            }

            string observation;
            try {
                observation = Encoding.UTF8.GetString(peer.Payload ?? Array.Empty<byte>());
            }
            catch {
                observation = "<invalid utf8 payload>";
            }

            Log.Info(
                "COI-Coop: PREVIEW RX rev=" + peer.Revision
                + " kind=" + peer.Kind
                + " bytes=" + (peer.Payload?.Length ?? 0)
                + " " + Truncate(observation));
        }
    }

    private static string Truncate(string value) {
        value = value ?? string.Empty;
        return value.Length <= MaxLoggedObservationChars
            ? value
            : value.Substring(0, MaxLoggedObservationChars) + "...";
    }

    private static int ReadEnvironmentInt(string name, int fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public void Dispose() {
        if (m_resolver != null && m_resolverObserved) {
            try { m_resolver.ObjectInstantiated -= OnObjectInstantiated; } catch { }
        }
        m_resolverObserved = false;
        m_session?.Dispose();
        m_session = null;
        m_discovery = null;
        m_sandboxDiscovery = null;
        m_simLoop = null;
        m_hooked = false;
    }
}
