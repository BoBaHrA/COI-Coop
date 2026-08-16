using System;
using System.Collections.Generic;

namespace CoiCoop;

/// <summary>
/// Presentation-only renderer for drag rows / duplicated ordinary placements.
/// Each mirrored entity is an ordinary LayoutEntityPreview, so this composes the
/// already-proven single-building renderer instead of creating simulated entities.
/// </summary>
internal sealed class RemoteMultiPlacementGhostRenderer : IDisposable {
    private readonly Mafi.DependencyResolver m_resolver;
    private readonly Action<string> m_log;
    private readonly List<RemotePlacementGhostRenderer> m_pieceRenderers =
        new List<RemotePlacementGhostRenderer>();

    private int m_visibleCount;
    private int m_lastLoggedCount = -1;

    public RemoteMultiPlacementGhostRenderer(Mafi.DependencyResolver resolver, Action<string> log) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        m_log = log;
    }

    public void Publish(MultiPlacementGhostWireCodec.DecodedState state) {
        if (state == null || state.Pieces == null || state.Pieces.Count < 2) {
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
            m_log?.Invoke("REMOTE MULTI GHOST pieces=" + m_visibleCount);
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
                message => m_log?.Invoke("REMOTE MULTI piece=" + index + " " + message)));
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
