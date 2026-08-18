using System;
using System.Collections.Generic;

namespace CoiCoop;

/// <summary>
/// Presentation-only renderer for modular vehicle ramps. A vanilla ramp preview is
/// already a collection of ordinary LayoutEntityPreview objects, so we reuse the
/// proven single-building renderer once per mirrored ramp piece.
/// </summary>
internal sealed class RemoteRampGhostRenderer : IDisposable {
    private readonly Mafi.DependencyResolver m_resolver;
    private readonly Action<string> m_log;
    private readonly List<RemotePlacementGhostRenderer> m_pieceRenderers =
        new List<RemotePlacementGhostRenderer>();

    private int m_visibleCount;
    private int m_lastLoggedCount = -1;

    public RemoteRampGhostRenderer(Mafi.DependencyResolver resolver, Action<string> log) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        m_log = log;
    }

    public void Publish(RampGhostWireCodec.DecodedState state) {
        if (state == null || state.Pieces == null || state.Pieces.Count == 0) {
            Clear();
            return;
        }

        EnsureRendererCount(state.Pieces.Count);

        for (var i = 0; i < state.Pieces.Count; i++) {
            var piece = state.Pieces[i];
            m_pieceRenderers[i].Publish(piece.Prototype, piece.Transform);
        }
        for (var i = state.Pieces.Count; i < m_visibleCount; i++) {
            m_pieceRenderers[i].Clear();
        }

        m_visibleCount = state.Pieces.Count;
        if (m_lastLoggedCount != m_visibleCount) {
            m_lastLoggedCount = m_visibleCount;
            m_log?.Invoke(
                "REMOTE RAMP GHOST pieces=" + m_visibleCount
                + " state=" + (state.ControllerState ?? string.Empty));
        }
    }

    public void Clear() {
        for (var i = 0; i < m_pieceRenderers.Count; i++) {
            m_pieceRenderers[i].Clear();
        }
        m_visibleCount = 0;
        m_lastLoggedCount = -1;
    }

    public void RenderUpdate() {
        for (var i = 0; i < m_pieceRenderers.Count; i++) {
            m_pieceRenderers[i].RenderUpdate();
        }
    }

    private void EnsureRendererCount(int count) {
        while (m_pieceRenderers.Count < count) {
            var index = m_pieceRenderers.Count;
            m_pieceRenderers.Add(new RemotePlacementGhostRenderer(
                m_resolver,
                message => m_log?.Invoke("REMOTE RAMP piece=" + index + " " + message)));
        }
    }

    public void Dispose() {
        for (var i = 0; i < m_pieceRenderers.Count; i++) {
            try { m_pieceRenderers[i].Dispose(); } catch { }
        }
        m_pieceRenderers.Clear();
        m_visibleCount = 0;
        m_lastLoggedCount = -1;
    }
}
