using System;
using CoiCoop.Networking;
using Mafi;
using Mafi.Core;
using Mafi.Core.GameLoop;

namespace CoiCoop;

/// <summary>
/// Independent latest-wins sidecar for multi-stage transport/bridge/train ghosts.
/// Kept on mainPort+2 during development so failures here cannot regress the
/// already-working building-ghost/sandbox channel on mainPort+1.
/// </summary>
internal sealed class PathPreviewBootstrap : IDisposable {
    private const int SampleIntervalMs = 33;
    private const int ClearGraceMs = 150;
    private const string PathKind = "PATH_GHOST";

    private static readonly object s_lock = new object();
    private static PathPreviewBootstrap s_current;

    private readonly DependencyResolver m_resolver;
    private readonly int m_mode;
    private readonly int m_port;

    private PathPreviewDiscovery m_discovery;
    private CommandRoundTripProbe m_codec;
    private PlacementPreviewSession m_session;
    private RemotePathGhostRenderer m_renderer;
    private IGameLoopEvents m_gameLoop;
    private bool m_renderHooked;
    private bool m_resolverObserved;
    private int m_lastSampleMs;
    private int m_noCaptureSinceMs;
    private bool m_localVisible;
    private bool m_peerVisible;
    private bool m_captureFailureLogged;
    private bool m_encodeFailureLogged;
    private int m_lastCandidateCount = -1;
    private string m_lastLocalLabel;
    private string m_lastPeerLabel;

    private PathPreviewBootstrap(DependencyResolver resolver, int mode, int port) {
        m_resolver = resolver;
        m_mode = mode;
        m_port = port;
    }

    public static void EnsureStarted(DependencyResolver resolver) {
        if (resolver == null) return;

        var mode = ReadEnvironmentInt("COI_COOP_MODE", 0);
        var mainPort = ReadEnvironmentInt("COI_COOP_PORT", 27015);
        if ((mode != 1 && mode != 2) || mainPort < 1024 || mainPort > 65533) return;

        lock (s_lock) {
            if (s_current != null && ReferenceEquals(s_current.m_resolver, resolver)) return;

            s_current?.Dispose();
            var next = new PathPreviewBootstrap(resolver, mode, mainPort + 2);
            s_current = next;
            next.Start();
        }
    }

    private void Start() {
        m_discovery = new PathPreviewDiscovery(m_resolver);
        m_codec = new CommandRoundTripProbe(m_resolver);
        m_renderer = new RemotePathGhostRenderer(
            m_resolver,
            message => Log.Info("COI-Coop: " + message));
        m_session = new PlacementPreviewSession(
            m_mode == 1,
            m_port,
            message => Log.Info("COI-Coop: PATH " + message));
        m_session.Start();

        m_resolver.ObjectInstantiated += OnObjectInstantiated;
        m_resolverObserved = true;

        IGameLoopEvents gameLoop;
        if (m_resolver.TryGetResolvedDependency<IGameLoopEvents>(out gameLoop) && gameLoop != null) {
            AttachGameLoop(gameLoop);
        }

        Log.Info(
            "COI-Coop: PATH PREVIEW sidecar starting on 127.0.0.1:" + m_port
            + " mode=" + (m_mode == 1 ? "HOST" : "CLIENT"));
    }

    private void OnObjectInstantiated(object instance) {
        if (instance == null) return;

        var gameLoop = instance as IGameLoopEvents;
        if (gameLoop != null) AttachGameLoop(gameLoop);

        if (m_discovery != null && m_discovery.ObserveInstance(instance)) {
            Log.Info("COI-Coop: PATH targeted controller " + instance.GetType().FullName);
        }
    }

    private void AttachGameLoop(IGameLoopEvents gameLoop) {
        if (m_renderHooked || gameLoop == null) return;
        m_gameLoop = gameLoop;
        m_gameLoop.RenderUpdate.AddNonSaveable(this, OnRenderUpdate);
        m_renderHooked = true;
        Log.Info("COI-Coop: PATH PREVIEW 30 Hz RenderUpdate sampler attached");
    }

    private void OnRenderUpdate(GameTime gameTime) {
        if (!ReferenceEquals(s_current, this) || m_session == null) return;

        PumpIncoming();

        if (m_session.IsConnected) {
            SampleLocal();
        }
        else {
            if (m_peerVisible) {
                m_peerVisible = false;
                m_lastPeerLabel = null;
                m_renderer?.Clear();
            }
        }

        m_renderer?.RenderUpdate();
    }

    private void SampleLocal() {
        if (m_discovery == null || m_codec == null) return;

        var changed = m_discovery.RefreshResolvedCandidates();
        if (changed || m_lastCandidateCount != m_discovery.CandidateCount) {
            m_lastCandidateCount = m_discovery.CandidateCount;
            Log.Info("COI-Coop: PATH targeted controllers=" + m_lastCandidateCount);
        }

        var now = Environment.TickCount;
        if (unchecked(now - m_lastSampleMs) < SampleIntervalMs) return;
        m_lastSampleMs = now;

        PathPreviewDiscovery.CapturedState captured;
        string captureError;
        if (!m_discovery.TryCapture(out captured, out captureError)) {
            if (m_localVisible) {
                if (m_noCaptureSinceMs == 0) m_noCaptureSinceMs = now;
                if (unchecked(now - m_noCaptureSinceMs) >= ClearGraceMs) {
                    m_session.Clear();
                    m_localVisible = false;
                    m_lastLocalLabel = null;
                    m_noCaptureSinceMs = 0;
                    Log.Info("COI-Coop: PATH GHOST TX CLEAR");
                }
            }

            if (!string.Equals(captureError, "no active multi-stage path controller", StringComparison.Ordinal)
                && captureError != null
                && captureError.IndexOf("no current PreviewRequest", StringComparison.Ordinal) < 0
                && !m_captureFailureLogged) {
                m_captureFailureLogged = true;
                Log.Info("COI-Coop: PATH GHOST CAPTURE WAIT - " + captureError);
            }
            return;
        }

        m_noCaptureSinceMs = 0;
        m_captureFailureLogged = false;

        byte[] payload;
        string encodeError;
        if (!PathPreviewWireCodec.TryEncode(captured, m_codec, out payload, out encodeError)) {
            if (!m_encodeFailureLogged) {
                m_encodeFailureLogged = true;
                Log.Info("COI-Coop: PATH GHOST ENCODE FAIL - " + encodeError);
            }
            return;
        }
        m_encodeFailureLogged = false;

        var revision = m_session.Publish(PathKind, payload);
        m_localVisible = true;

        var label = captured.Family + "/" + (captured.IsContinuation ? "CONT" : "START")
            + "/" + captured.ControllerState;
        if (!string.Equals(label, m_lastLocalLabel, StringComparison.Ordinal)) {
            m_lastLocalLabel = label;
            Log.Info(
                "COI-Coop: PATH GHOST TX START rev=" + revision
                + " family=" + captured.Family
                + " stage=" + (captured.IsContinuation ? "CONT" : "START")
                + " state=" + captured.ControllerState
                + " bytes=" + payload.Length);
        }
    }

    private void PumpIncoming() {
        PlacementPreviewState peer;
        while (m_session.TryTakeLatestPeerState(out peer)) {
            if (peer == null) continue;

            if (string.Equals(peer.Kind, "NONE", StringComparison.Ordinal)) {
                if (m_peerVisible) {
                    m_peerVisible = false;
                    m_lastPeerLabel = null;
                    m_renderer?.Clear();
                    Log.Info("COI-Coop: PATH GHOST RX CLEAR rev=" + peer.Revision);
                }
                continue;
            }

            if (!string.Equals(peer.Kind, PathKind, StringComparison.Ordinal)) continue;

            PathPreviewWireCodec.DecodedState decoded;
            string decodeError;
            if (!PathPreviewWireCodec.TryDecode(peer.Payload, m_codec, out decoded, out decodeError)) {
                Log.Info(
                    "COI-Coop: PATH GHOST RX FAIL rev=" + peer.Revision
                    + " error=" + decodeError);
                continue;
            }

            m_peerVisible = true;
            m_renderer?.Publish(decoded);

            var label = decoded.Family + "/" + (decoded.IsContinuation ? "CONT" : "START")
                + "/" + decoded.ControllerState;
            if (!string.Equals(label, m_lastPeerLabel, StringComparison.Ordinal)) {
                m_lastPeerLabel = label;
                Log.Info(
                    "COI-Coop: PATH GHOST RX START rev=" + peer.Revision
                    + " family=" + decoded.Family
                    + " stage=" + (decoded.IsContinuation ? "CONT" : "START")
                    + " state=" + decoded.ControllerState
                    + " bytes=" + (peer.Payload?.Length ?? 0));
            }
        }
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

        if (m_gameLoop != null && m_renderHooked) {
            try { m_gameLoop.RenderUpdate.RemoveNonSaveable(this, OnRenderUpdate); } catch { }
        }
        m_renderHooked = false;

        m_renderer?.Clear();
        m_renderer?.Dispose();
        m_renderer = null;
        m_session?.Dispose();
        m_session = null;
        m_discovery = null;
        m_codec = null;
        m_gameLoop = null;
    }
}
