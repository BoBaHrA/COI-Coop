using System;
using System.Reflection;
using Mafi.Core.Simulation;

namespace CoiCoop;

internal sealed class LiveSimulationGate {
    private readonly SimLoopEvents m_sim;
    private readonly PropertyInfo m_mode;
    private readonly object m_originalMode;
    private readonly object m_fixedMode;

    internal LiveSimulationGate(SimLoopEvents sim) {
        m_sim = sim;
        m_mode = typeof(SimLoopEvents).GetProperty("AdaptiveSimSpeedMode");
        if (m_mode == null || m_mode.GetSetMethod(true) == null)
            throw new InvalidOperationException("Game build has no verified deterministic step-budget adapter");
        m_originalMode = m_mode.GetValue(sim);
        m_fixedMode = Enum.Parse(m_mode.PropertyType, "Uncapped");
    }

    internal void Hold() { SetPause(true); }

    internal void BeginFrame(bool gameplayPaused) {
        m_mode.SetValue(m_sim, m_fixedMode);
        SetPause(gameplayPaused);
    }

    internal void RestoreSolo(bool gameplayPaused) {
        SetPause(gameplayPaused);
        m_mode.SetValue(m_sim, m_originalMode);
    }

    private void SetPause(bool paused) {
        if (m_sim.IsInSimLoop && m_sim.CurrentState != SimLoopState.CommandsProcessing)
            throw new InvalidOperationException("Live gate must run at Sync or CommandsProcessing");
        m_sim.SetSimPause(paused);
    }
}
