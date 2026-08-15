using System;
using System.Collections.Generic;
using System.Text;
using CoiCoop.Networking;
using Mafi;
using Mafi.Core.Simulation;

namespace CoiCoop;

/// <summary>
/// Auxiliary co-op sidecar for presentation-only placement telemetry and the
/// targeted sandbox ProductsSource state adapter.
///
/// The sidecar is deliberately independent from authoritative replay: failures
/// here are logged but never halt simulation.
/// </summary>
internal sealed class PlacementPreviewBootstrap : IDisposable {
    private const int PlacementSampleIntervalMs = 100;
    private const int SandboxSampleIntervalMs = 100;
    private const int MaxLoggedObservationChars = 700;
    private const string PlacementKind = "PLACEMENT_DELTA";
    private const string SandboxKind = "SANDBOX_SOURCE";

    private static readonly object s_lock = new object();
    private static PlacementPreviewBootstrap s_current;

    private readonly DependencyResolver m_resolver;
    private readonly int m_mode;
    private readonly int m_previewPort;
    private PlacementPreviewDiscovery m_discovery;
    private SandboxSourceDiscovery m_sandboxDiscovery;
    private CommandRoundTripProbe m_stateCodec;
    private PlacementPreviewSession m_session;
    private ISimLoopEvents m_simLoop;
    private int m_lastPlacementSampleMs;
    private int m_lastSandboxSampleMs;
    private int m_lastLoggedCandidateCount = -1;
    private int m_lastLoggedSandboxCandidateCount = -1;
    private bool m_hooked;
    private bool m_resolverObserved;
    private bool m_sandboxFailureLogged;

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
        m_stateCodec = new CommandRoundTripProbe(m_resolver);
        m_session = new PlacementPreviewSession(
            m_mode == 1,
            m_previewPort,
            message => Log.Info("COI-Coop: " + message));
        m_session.Start();

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
        if (simLoop != null) Attach(simLoop);

        if (m_discovery != null && m_discovery.ObserveInstance(instance)) {
            Log.Info(
                "COI-Coop: PREVIEW targeted candidate "
                + instance.GetType().FullName);
        }
    }

    private void Attach(ISimLoopEvents simLoop) {
        if (m_hooked || simLoop == null) return;
        m_simLoop = simLoop;
        m_simLoop.UpdateEndForUi.AddNonSaveable(this, OnUpdateEndForUi);
        m_hooked = true;
        Log.Info("COI-Coop: PREVIEW lightweight UpdateEndForUi sampler attached");
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

        var changedCandidates = m_discovery.RefreshResolvedCandidates();
        if (changedCandidates || m_lastLoggedCandidateCount != m_discovery.CandidateCount) {
            m_lastLoggedCandidateCount = m_discovery.CandidateCount;
            Log.Info(
                "COI-Coop: PREVIEW targeted candidates=" + m_discovery.CandidateCount
                + " [" + Truncate(m_discovery.CandidateSummary) + "]");
        }

        var now = Environment.TickCount;
        if (unchecked(now - m_lastPlacementSampleMs) < PlacementSampleIntervalMs) return;
        m_lastPlacementSampleMs = now;

        string observation;
        if (!m_discovery.TryCaptureChanged(out observation)) return;

        var payload = Encoding.UTF8.GetBytes(observation);
        var revision = m_session.Publish(PlacementKind, payload);
        Log.Info(
            "COI-Coop: PREVIEW DELTA TX rev=" + revision
            + " bytes=" + payload.Length
            + " " + Truncate(observation));
    }

    private void SampleSandboxSources() {
        if (m_sandboxDiscovery == null || m_stateCodec == null) return;

        var now = Environment.TickCount;
        if (unchecked(now - m_lastSandboxSampleMs) < SandboxSampleIntervalMs) return;
        m_lastSandboxSampleMs = now;

        var changedCandidates = m_sandboxDiscovery.RefreshIfNeeded();
        if (changedCandidates || m_lastLoggedSandboxCandidateCount != m_sandboxDiscovery.CandidateCount) {
            m_lastLoggedSandboxCandidateCount = m_sandboxDiscovery.CandidateCount;
            Log.Info(
                "COI-Coop: SANDBOX targeted ProductsSource candidates="
                + m_sandboxDiscovery.CandidateCount);
        }

        List<SandboxSourceDiscovery.LocalUpdate> updates;
        string error;
        if (!m_sandboxDiscovery.TryCaptureChanges(m_stateCodec, out updates, out error)) {
            if (!m_sandboxFailureLogged) {
                m_sandboxFailureLogged = true;
                Log.Info("COI-Coop: SANDBOX SYNC CAPTURE FAIL - " + error);
            }
            return;
        }

        m_sandboxFailureLogged = false;
        if (updates == null) return;

        foreach (var update in updates) {
            var wirePayload = BuildSandboxWirePayload(update.EntityId, update.ValuePayload);
            var revision = m_session.PublishReliable(SandboxKind, wirePayload);
            Log.Info(
                "COI-Coop: SANDBOX SYNC TX rev=" + revision
                + " entity=" + update.EntityId
                + " bytes=" + update.ValuePayload.Length);
        }
    }

    private void PumpIncoming() {
        PlacementPreviewState reliable;
        while (m_session.TryDequeueReliablePeerState(out reliable)) {
            if (reliable == null || !string.Equals(reliable.Kind, SandboxKind, StringComparison.Ordinal)) {
                continue;
            }

            int entityId;
            byte[] valuePayload;
            if (!TryParseSandboxWirePayload(reliable.Payload, out entityId, out valuePayload)) {
                Log.Info("COI-Coop: SANDBOX SYNC RX malformed rev=" + reliable.Revision);
                continue;
            }

            string error;
            if (m_sandboxDiscovery == null
                || m_stateCodec == null
                || !m_sandboxDiscovery.TryApplyRemote(entityId, valuePayload, m_stateCodec, out error)) {

                Log.Info(
                    "COI-Coop: SANDBOX SYNC RX FAIL rev=" + reliable.Revision
                    + " entity=" + entityId
                    + " error=" + error);
                continue;
            }

            Log.Info(
                "COI-Coop: SANDBOX SYNC RX/APPLIED rev=" + reliable.Revision
                + " entity=" + entityId
                + " bytes=" + valuePayload.Length);
        }

        PlacementPreviewState peer;
        while (m_session.TryTakeLatestPeerState(out peer)) {
            if (peer == null) continue;

            if (string.Equals(peer.Kind, "NONE", StringComparison.Ordinal)) {
                Log.Info("COI-Coop: PREVIEW RX CLEAR rev=" + peer.Revision);
                continue;
            }

            if (!string.Equals(peer.Kind, PlacementKind, StringComparison.Ordinal)) {
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
                "COI-Coop: PREVIEW DELTA RX rev=" + peer.Revision
                + " bytes=" + (peer.Payload?.Length ?? 0)
                + " " + Truncate(observation));
        }
    }

    private static byte[] BuildSandboxWirePayload(int entityId, byte[] valuePayload) {
        valuePayload = valuePayload ?? Array.Empty<byte>();
        var payload = new byte[4 + valuePayload.Length];
        var idBytes = BitConverter.GetBytes(entityId);
        Buffer.BlockCopy(idBytes, 0, payload, 0, 4);
        if (valuePayload.Length > 0) {
            Buffer.BlockCopy(valuePayload, 0, payload, 4, valuePayload.Length);
        }
        return payload;
    }

    private static bool TryParseSandboxWirePayload(
        byte[] payload,
        out int entityId,
        out byte[] valuePayload) {

        entityId = 0;
        valuePayload = null;
        if (payload == null || payload.Length < 4) return false;

        entityId = BitConverter.ToInt32(payload, 0);
        valuePayload = new byte[payload.Length - 4];
        if (valuePayload.Length > 0) {
            Buffer.BlockCopy(payload, 4, valuePayload, 0, valuePayload.Length);
        }
        return true;
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
        m_stateCodec = null;
        m_simLoop = null;
        m_hooked = false;
    }
}
