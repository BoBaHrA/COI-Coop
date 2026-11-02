using System.Reflection;
using CoiCoop;
using Mafi;
using Mafi.Core.Input;
using Mafi.Core.Simulation;

Console.WriteLine("REAL GAME API: Mafi.Core " + typeof(SimLoopEvents).Assembly.GetName().Version);
var host = new Replica();
var guest = new Replica();
for (var i = 0; i < 500; i++) guest.Run(ready: false, paused: false, speed: 3);
Require(guest.Step == 100, "Network waiting advanced the real simulation");
Require(guest.UiCallbacks == 500, "Paused network wait blocked UI end callbacks");
Console.WriteLine("PASS: 500 network wait iterations keep real sim step fixed and UI events alive");

for (var frame = 0; frame < 300; frame++) {
    var speed = new[] { 1, 3, 20 }[frame % 3];
    var paused = frame % 7 == 0;
    host.Run(true, paused, speed);
    for (var stall = 0; stall < frame % 9; stall++) guest.Run(false, paused, speed);
    guest.Run(true, paused, speed);
    Require(host.Step == guest.Step, "Different wallclock wait schedules changed simulation coordinates");
}
Console.WriteLine("PASS: 300 real outer frames match with pauses, 1x/3x/20x and unequal network waits");
host.Gate.RestoreSolo(false);
Require(!host.Sim.IsSimPaused, "Host did not resume when detached");
Require(host.Sim.AdaptiveSimSpeedMode == SimAdaptiveSpeedMode.Predictive, "Host's original adaptive mode was not restored");
Console.WriteLine("PASS: detaching restores solo pause/speed policy");
Console.WriteLine("GAME API SMOKE PASS (simulation gate only; not a complete island/Unity session)");

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class Replica : IInputCommandsProcessor {
    public readonly SimLoopEvents Sim = new SimLoopEvents();
    public readonly LiveSimulationGate Gate;
    public int Step => Sim.CurrentStep.Value;
    public int UiCallbacks;
    private bool m_outer = true;
    private bool m_ready;
    private bool m_paused;
    private int m_speed;
    private static readonly MethodInfo InvokeStep = typeof(SimLoopEvents).GetMethod("InvokeGameStep", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public Replica() {
        typeof(SimLoopEvents).GetMethod("TestOnly_SetCurrentStep", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Sim, new object[] { new SimStep(100) });
        typeof(SimLoopEvents).GetProperty("AdaptiveSimSpeedMode")!.SetValue(Sim, SimAdaptiveSpeedMode.Predictive);
        Gate = new LiveSimulationGate(Sim);
        Sim.UpdateBeforeCmdProc.AddNonSaveable(this, Before);
        Sim.UpdateEndForUi.AddNonSaveable(this, End);
    }
    private void Before() {
        if (!m_outer) return;
        m_outer = false;
        if (m_ready) { Gate.BeginFrame(m_paused); Sim.SetSimSpeed(m_speed); }
        else Gate.Hold();
    }
    private void End() { m_outer = true; UiCallbacks++; }
    public void ProcessCommands() { }
    public void Run(bool ready, bool paused, int speed) {
        m_ready = ready; m_paused = paused; m_speed = speed;
        InvokeStep.Invoke(Sim, new object[] { this });
    }
}
