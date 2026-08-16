using System;
using System.Collections.Generic;
using CoiCoop.Networking;
using Mafi;
using Mafi.Core;
using Mafi.Core.GameLoop;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;

namespace CoiCoop;

/// <summary>
/// Auxiliary co-op sidecar for presentation-only building placement ghosts and
/// targeted sandbox source/sink state adapters.
///
/// Network/state sampling happens from the sim/UI-end hook. Actual ghost creation
/// and transform updates happen only from IGameLoopEvents.RenderUpdate so Unity
/// preview objects are never touched from the simulation thread.
/// </summary>
internal sealed class PlacementPreviewBootstrap : IDisposable {
    private const int PlacementSampleIntervalMs = 100;
    private const int SandboxSampleIntervalMs = 100;
    private const string PlacementKind = "PLACEMENT_GHOST";
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
    private RemotePlacementGhostRenderer m_remoteGhostRenderer;
    private ISimLoopEvents m_simLoop;
    private IGameLoopEvents m_gameLoop;
    private int m_lastPlacementSampleMs;
    private int m_lastSandboxSampleMs;
    private int m_lastLoggedCandidateCount = -1;
    private int m_lastLoggedSandboxCandidateCount = -1;
    private bool m_simHooked;
    private bool m_renderHooked;
    private bool m_resolverObserved;
    private bool m_sandboxFailureLogged;
    private bool m_localGhostVisible;
    private bool m_localGhostFailureLogged;
    private bool m_peerGhostVisible;
    private string m_lastLocalGhostProtoKey;
    private string m_lastPeerGhostProtoKey;

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
        m_remoteGhostRenderer = new RemotePlacementGhostRenderer(
            m_resolver,
            message => Log.Info("COI-Coop: " + message));
        m_session = new PlacementPreviewSession(
            m_mode == 1,
            m_previewPort,
            message => Log.Info("COI-Coop: " + message));
        m_session.Start();

        m_resolver.ObjectInstantiated += OnObjectInstantiated;
        m_resolverObserved = true;

        ISimLoopEvents simLoop;
        if (m_resolver.TryGetResolvedDependency<ISimLoopEvents>(out simLoop) && simLoop != null) {
            AttachSimLoop(simLoop);
        }

        IGameLoopEvents gameLoop;
        if (m_resolver.TryGetResolvedDependency<IGameLoopEvents>(out gameLoop) && gameLoop != null) {
            AttachGameLoop(gameLoop);
        }

        Log.Info(
            "COI-Coop: PREVIEW sidecar starting on 127.0.0.1:" + m_previewPort
            + " mode=" + (m_mode == 1 ? "HOST" : "CLIENT"));
    }

    private void OnObjectInstantiated(object instance) {
        if (instance == null) return;

        var simLoop = instance as ISimLoopEvents;
        if (simLoop != null) AttachSimLoop(simLoop);

        var gameLoop = instance as IGameLoopEvents;
        if (gameLoop != null) AttachGameLoop(gameLoop);

        if (m_discovery != null && m_discovery.ObserveInstance(instance)) {
            Log.Info(
                "COI-Coop: PREVIEW targeted candidate "
                + instance.GetType().FullName);
        }
    }

    private void AttachSimLoop(ISimLoopEvents simLoop) {
        if (m_simHooked || simLoop == null) return;
        m_simLoop = simLoop;
        m_simLoop.UpdateEndForUi.AddNonSaveable(this, OnUpdateEndForUi);
        m_simHooked = true;
        Log.Info("COI-Coop: PREVIEW lightweight UpdateEndForUi sampler attached");
    }

    private void AttachGameLoop(IGameLoopEvents gameLoop) {
        if (m_renderHooked || gameLoop == null) return;
        m_gameLoop = gameLoop;
        m_gameLoop.RenderUpdate.AddNonSaveable(this, OnRenderUpdate);
        m_renderHooked = true;
        Log.Info("COI-Coop: REMOTE GHOST RenderUpdate hook attached");
    }

    private void OnRenderUpdate(GameTime gameTime) {
        if (!ReferenceEquals(s_current, this)) return;
        m_remoteGhostRenderer?.RenderUpdate();
    }

    private void OnUpdateEndForUi() {
        if (!ReferenceEquals(s_current, this) || m_session == null) return;

        PumpIncoming();
        if (!m_session.IsConnected) {
            if (m_peerGhostVisible) {
                m_peerGhostVisible = false;
                m_remoteGhostRenderer?.Clear();
            }
            return;
        }

        SamplePlacementPreview();
        SampleSandboxSources();
    }

    private void SamplePlacementPreview() {
        if (m_discovery == null || m_stateCodec == null) return;

        var changedCandidates = m_discovery.RefreshResolvedCandidates();
        if (changedCandidates || m_lastLoggedCandidateCount != m_discovery.CandidateCount) {
            m_lastLoggedCandidateCount = m_discovery.CandidateCount;
            Log.Info(
                "COI-Coop: PREVIEW targeted candidates=" + m_discovery.CandidateCount
                + " [" + m_discovery.CandidateSummary + "]");
        }

        var now = Environment.TickCount;
        if (unchecked(now - m_lastPlacementSampleMs) < PlacementSampleIntervalMs) return;
        m_lastPlacementSampleMs = now;

        Proto prototype;
        TileTransform transform;
        string captureError;
        if (!m_discovery.TryCaptureBuildingGhost(out prototype, out transform, out captureError)) {
            if (m_localGhostVisible) {
                m_session.Clear();
                m_localGhostVisible = false;
                m_lastLocalGhostProtoKey = null;
                Log.Info("COI-Coop: PREVIEW GHOST TX CLEAR");
            }

            // No active placer is the normal steady state, not an error.
            if (!string.Equals(captureError, "no active StaticEntityMassPlacer", StringComparison.Ordinal)
                && !m_localGhostFailureLogged) {
                m_localGhostFailureLogged = true;
                Log.Info("COI-Coop: PREVIEW GHOST CAPTURE WAIT - " + captureError);
            }
            return;
        }

        m_localGhostFailureLogged = false;

        byte[] payload;
        string encodeError;
        if (!PlacementGhostWireCodec.TryEncode(
                prototype,
                transform,
                m_stateCodec,
                out payload,
                out encodeError)) {

            if (!m_localGhostFailureLogged) {
                m_localGhostFailureLogged = true;
                Log.Info("COI-Coop: PREVIEW GHOST ENCODE FAIL - " + encodeError);
            }
            return;
        }

        var revision = m_session.Publish(PlacementKind, payload);
        m_localGhostVisible = true;

        var protoKey = GetProtoKey(prototype);
        if (!string.Equals(protoKey, m_lastLocalGhostProtoKey, StringComparison.Ordinal)) {
            m_lastLocalGhostProtoKey = protoKey;
            Log.Info(
                "COI-Coop: PREVIEW GHOST TX START rev=" + revision
                + " proto=" + protoKey
                + " bytes=" + payload.Length
                + " transform=" + transform);
        }
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
                "COI-Coop: SANDBOX targeted endpoint candidates="
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

            string error = "sandbox adapter or state codec is unavailable";
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
                if (m_peerGhostVisible) {
                    m_peerGhostVisible = false;
                    m_lastPeerGhostProtoKey = null;
                    m_remoteGhostRenderer?.Clear();
                    Log.Info("COI-Coop: PREVIEW GHOST RX CLEAR rev=" + peer.Revision);
                }
                continue;
            }

            if (!string.Equals(peer.Kind, PlacementKind, StringComparison.Ordinal)) {
                continue;
            }

            PlacementGhostWireCodec.DecodedState decoded;
            string decodeError;
            if (m_stateCodec == null
                || !PlacementGhostWireCodec.TryDecode(peer.Payload, m_stateCodec, out decoded, out decodeError)) {

                Log.Info(
                    "COI-Coop: PREVIEW GHOST RX FAIL rev=" + peer.Revision
                    + " error=" + decodeError);
                continue;
            }

            m_peerGhostVisible = true;
            m_remoteGhostRenderer?.Publish(decoded.Prototype, decoded.Transform);

            var protoKey = GetProtoKey(decoded.Prototype);
            if (!string.Equals(protoKey, m_lastPeerGhostProtoKey, StringComparison.Ordinal)) {
                m_lastPeerGhostProtoKey = protoKey;
                Log.Info(
                    "COI-Coop: PREVIEW GHOST RX START rev=" + peer.Revision
                    + " proto=" + protoKey
                    + " bytes=" + (peer.Payload?.Length ?? 0)
                    + " transform=" + decoded.Transform);
            }
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

    private static string GetProtoKey(Proto prototype) {
        if (prototype == null) return "<null>";
        try { return prototype.GetType().Name + "#" + prototype.Id; }
        catch { return prototype.GetType().Name; }
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

        if (m_simLoop != null && m_simHooked) {
            try { m_simLoop.UpdateEndForUi.RemoveNonSaveable(this, OnUpdateEndForUi); } catch { }
        }
        if (m_gameLoop != null && m_renderHooked) {
            try { m_gameLoop.RenderUpdate.RemoveNonSaveable(this, OnRenderUpdate); } catch { }
        }
        m_simHooked = false;
        m_renderHooked = false;

        m_remoteGhostRenderer?.Clear();
        m_remoteGhostRenderer?.Dispose();
        m_remoteGhostRenderer = null;
        m_session?.Dispose();
        m_session = null;
        m_discovery = null;
        m_sandboxDiscovery = null;
        m_stateCodec = null;
        m_simLoop = null;
        m_gameLoop = null;
    }
}
