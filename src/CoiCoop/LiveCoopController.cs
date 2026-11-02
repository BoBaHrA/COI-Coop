using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using CoiCoop.Networking;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.GameLoop;
using Mafi.Core.Input;
using Mafi.Core.SaveGame;
using Mafi.Core.Simulation;

namespace CoiCoop;

/// <summary>
/// Live host world coordinator. Network workers never touch game objects.
/// Snapshots run at Sync, commands at CommandsProcessing, and network waiting
/// uses the game's pause API instead of blocking the simulation thread.
/// </summary>
internal sealed class LiveCoopController : IDisposable {
    private readonly bool m_host;
    private readonly int m_port;
    private readonly SimLoopEvents m_sim;
    private readonly IGameLoopEvents m_loop;
    private readonly SaveManager m_saves;
    private readonly Lyst<IInputCommand> m_mainCommands;
    private readonly Lyst<IInputCommand> m_simCommands;
    private readonly Lyst<IInputCommand> m_commands;
    private readonly CommandRoundTripProbe m_codec;
    private readonly WorldStateFingerprintBuilder m_fingerprints;
    private readonly List<IInputCommand> m_local = new List<IInputCommand>();
    private readonly List<IInputCommand> m_heldDerived = new List<IInputCommand>();
    private readonly HashSet<IInputCommand> m_derived = new HashSet<IInputCommand>();
    private readonly SortedDictionary<long, IInputCommand> m_originals = new SortedDictionary<long, IInputCommand>();
    private readonly SortedDictionary<long, ReceivedAuthorityCommand> m_received = new SortedDictionary<long, ReceivedAuthorityCommand>();
    private readonly Dictionary<long, StateProbeSnapshot> m_localProbes = new Dictionary<long, StateProbeSnapshot>();
    private readonly Dictionary<long, StateProbeSnapshot> m_peerProbes = new Dictionary<long, StateProbeSnapshot>();
    private readonly LiveSimulationGate m_gate;
    private readonly string m_statusPath;
    private LiveWorldServer m_server;
    private LiveWorldServer.Request m_request;
    private LiveWorldServer.Request m_joinRequest;
    private PersistentCommandSession m_session;
    private bool m_outer = true;
    private bool m_ranFrame;
    private bool m_readyOnce;
    private bool m_failed;
    private bool m_paused;
    private bool m_disposed;
    private bool m_runtimeReady;
    private long m_frame;
    private long m_nextSequence;
    private long m_baselineStep;
    private DateTime m_joinDeadline;
    private string m_status;

    internal LiveCoopController(DependencyResolver resolver, string modDll) {
        m_host = Environment.GetEnvironmentVariable("COI_COOP_MODE") == "1";
        m_port = int.Parse(Required("COI_COOP_PORT"));
        if (m_port < 1024 || m_port > 65534) throw new InvalidOperationException("Live ports must be 1024-65534");
        if (!resolver.TryResolve(out m_sim)
            || !resolver.TryResolve(out InputScheduler scheduler)
            || !resolver.TryResolve(out m_loop)
            || !resolver.TryResolve(out m_saves))
            throw new InvalidOperationException("Required live game dependencies were not resolved");
        m_mainCommands = Queue(scheduler, "m_mainCommands");
        m_simCommands = Queue(scheduler, "m_simCommands");
        m_commands = Queue(scheduler, "m_commandsToProcess");
        m_gate = new LiveSimulationGate(m_sim);
        m_codec = new CommandRoundTripProbe(resolver);
        m_fingerprints = new WorldStateFingerprintBuilder(resolver);
        m_statusPath = Environment.GetEnvironmentVariable("COI_COOP_STATUS_PATH");
        var compatibility = LiveBuildIdentity.Calculate(Required("COI_COOP_GAME_ROOT"), modDll);

        if (m_host) {
            var key = Required("COI_COOP_JOIN_KEY");
            if (key.Length != 64) throw new InvalidOperationException("Invalid host connection key");
            m_server = new LiveWorldServer(m_port + 1, key, compatibility, LogMessage);
            Status("HOST SOLO — world available for a friend");
        }
        else {
            if (!resolver.TryGetResolvedDependency<GameNameConfig>(out var gameName))
                throw new InvalidOperationException("Loaded world identity unavailable");
            var cacheName = Required("COI_COOP_SESSION_CACHE_NAME");
            if (!cacheName.StartsWith("__COI_COOP_LIVE_", StringComparison.Ordinal)
                || !gameName.LoadedFile.HasValue
                || gameName.LoadedFile.Value.GameName != cacheName
                || gameName.LoadedFile.Value.NameNoExtension != cacheName)
                throw new InvalidOperationException("Join loaded a different save; refusing to attach it to the host");
            var hash = Required("COI_COOP_BASELINE_SHA256");
            if (!string.Equals(LiveBuildIdentity.FileHash(Required("COI_COOP_SESSION_CACHE_PATH")), hash, StringComparison.Ordinal)
                || compatibility != Required("COI_COOP_BUILD_ID"))
                throw new InvalidOperationException("Downloaded snapshot or game/mod build no longer matches");
            m_baselineStep = long.Parse(Required("COI_COOP_SNAPSHOT_STEP"));
            m_paused = Required("COI_COOP_SNAPSHOT_PAUSED") == "1";
            m_joinDeadline = DateTime.UtcNow.AddMinutes(5);
            // Guest autosaves are not a second campaign. Manual saves remain
            // confined by GameNameConfig to this disposable cache directory.
            m_saves.SetCustomAutoSaveInterval(Duration.Zero);
            m_session = new PersistentCommandSession(false, m_port, LogMessage, hash,
                Required("COI_COOP_HOST"), sessionKey: Required("COI_COOP_TICKET"));
            m_session.Start();
            Status("JOINING — verified host snapshot loaded");
        }
        m_sim.Sync.AddNonSaveable(this, OnSync);
        m_loop.SyncUpdateEnd.AddNonSaveable(this, OnSyncEnd);
        m_sim.UpdateBeforeCmdProc.AddNonSaveable(this, BeforeCommands);
        m_sim.UpdateAfterCmdProc.AddNonSaveable(this, AfterCommands);
        m_sim.UpdateEndForUi.AddNonSaveable(this, EndFrame);
    }

    private static Lyst<IInputCommand> Queue(InputScheduler scheduler, string name) {
        var field = typeof(InputScheduler).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(scheduler) as Lyst<IInputCommand>
            ?? throw new InvalidOperationException("InputScheduler API changed: " + name);
    }

    private static string Required(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Missing " + name);
        return value;
    }

    private void OnSyncEnd(GameTime time) {
        m_runtimeReady = true;
        // CollectCommands runs after this event. Only player/main-thread input
        // enters the network. Deterministic sim-thread commands execute once on
        // each replica, rather than being submitted a second time by the guest.
        foreach (var command in m_simCommands) m_derived.Add(command);
    }

    private void OnSync() {
        if (m_disposed || !m_host || !m_runtimeReady) return;
        if (m_request == null && m_server.TryTakeRequest(out var request)) m_request = request;
        if (m_request == null) return;
        if (m_request.Cancelled) { m_request = null; return; }
        if (m_session != null) {
            m_request.Complete(null, "A guest is connected or still loading");
            m_request = null;
            return;
        }
        if (m_saves.IsSavePending) return;
        var pending = m_request;
        m_request = null;
        try {
            m_paused = m_sim.IsSimPaused;
            m_gate.Hold();
            // Exclude host UI commands waiting in this sync from the snapshot.
            // Their original objects are retained and submitted after READY.
            foreach (var command in m_mainCommands) m_local.Add(command);
            m_mainCommands.Clear();
            byte[] bytes;
            using (var stream = new MemoryStream()) {
                m_saves.SaveGame(stream, SaveCompressionType.Gzip);
                bytes = stream.ToArray();
            }
            if (bytes.Length > 256 * 1024 * 1024) throw new IOException("World snapshot exceeds 256 MiB");
            var snapshot = new LiveWorldServer.Snapshot {
                Bytes = bytes, Hash = LiveWorldServer.Hash(bytes),
                Ticket = LiveWorldServer.NewTicket(), Step = m_sim.CurrentStep.Value, Paused = m_paused
            };
            ResetEpoch();
            m_baselineStep = snapshot.Step;
            m_joinDeadline = DateTime.UtcNow.AddMinutes(5);
            m_session = new PersistentCommandSession(true, m_port, LogMessage, snapshot.Hash,
                bindAddress: IPAddress.Any, sessionKey: snapshot.Ticket);
            m_session.Start();
            m_joinRequest = pending;
            pending.Complete(snapshot);
            Status("HOST WAITING — friend loading current world");
        }
        catch (Exception ex) {
            pending.Complete(null, ex.Message);
            ReturnHostToSolo("snapshot failed: " + ex.Message, m_mainCommands);
        }
    }

    private void BeforeCommands() {
        if (!m_outer || m_disposed) return;
        m_outer = false;
        m_ranFrame = false;
        if (m_host && m_session == null) { m_derived.Clear(); return; }
        try {
            foreach (var command in m_commands) {
                if (m_derived.Remove(command)) m_heldDerived.Add(command);
                else m_local.Add(command);
            }
            m_commands.Clear();
            if (m_failed) { m_local.Clear(); Hold(); return; }
            if (m_session == null) throw new IOException("Session missing");
            if (m_joinRequest != null && m_joinRequest.Cancelled)
                throw new IOException("Snapshot transfer was cancelled");
            if (m_session.RequiresResync || (m_readyOnce && !m_session.IsConnected))
                throw new IOException("Guest or host disconnected");
            if (DateTime.UtcNow > m_joinDeadline && !m_readyOnce)
                throw new IOException("Join timed out");
            if (!m_session.IsConnected) { Hold(); return; }
            if (!m_session.IsGameplayReady) {
                if (m_sim.CurrentStep.Value != m_baselineStep)
                    throw new IOException("Loaded simulation coordinate differs from snapshot");
                m_session.MarkGameplayReady();
            }
            if (m_host && !m_session.PeerGameplayReady) { Hold(); return; }
            // Bound host lead when the guest runs on a slower machine. Waiting
            // still processes the UI and retains local input for the next frame.
            if (m_host && m_frame >= 8
                && (!m_session.TryGetPeerProgress(out var peerFrame, out _)
                    || m_frame - peerFrame > 8)) { Hold(); return; }
            if (!m_host && m_session.LatestAnnouncedAuthorityFrame < m_frame) { Hold(); return; }
            if (!m_readyOnce) {
                m_readyOnce = true;
                Status(m_host ? "HOST CONNECTED — shared world" : "CONNECTED — host owns this world");
            }
            // Do not use wallclock/performance-adaptive step budgets in lockstep.
            if (m_host && m_session.AdvanceHostAuthorityFrame() != m_frame)
                throw new IOException("Host frame sequence changed unexpectedly");
            PumpCommands();
            m_gate.BeginFrame(m_paused);
            foreach (var command in m_heldDerived) m_commands.Add(command);
            m_heldDerived.Clear();
            ApplyFrame(m_frame, m_commands);
            SubmitLocal();
            m_ranFrame = true;
        }
        catch (Exception ex) {
            if (m_host) ReturnHostToSolo(ex.Message, m_commands);
            else FailGuest(ex.Message);
        }
    }

    private void Hold() {
        m_gate.Hold();
        // m_commands is empty; held deterministic work is restored only when
        // an authoritative frame is available. No socket wait on the sim thread.
    }

    private void SubmitLocal() {
        while (m_local.Count > 0) {
            var command = m_local[0];
            if (!m_codec.TrySerialize(command, out var bytes, out var error))
                throw new IOException("Cannot encode " + command.GetType().Name + ": " + error);
            if (!m_session.SubmitLocalCommand(bytes, out var id)) throw new IOException("Command submission failed");
            m_originals.Add(id, command);
            m_local.RemoveAt(0);
        }
    }

    private void PumpCommands() {
        while (m_session.TryDequeueReceived(out var command)) {
            if (command.AuthoritySequence < m_nextSequence) continue;
            if (m_received.ContainsKey(command.AuthoritySequence)) throw new IOException("Duplicate authority command");
            m_received.Add(command.AuthoritySequence, command);
        }
    }

    private void ApplyFrame(long frame, Lyst<IInputCommand> target) {
        var applied = new List<long>();
        foreach (var item in m_received) {
            var envelope = item.Value;
            if (envelope.AuthorityFrame > frame) break;
            if (envelope.AuthorityFrame < frame || envelope.AuthoritySequence != m_nextSequence)
                throw new IOException("Authority frame or command sequence gap");
            target.Add(Decode(envelope));
            applied.Add(item.Key);
            m_nextSequence++;
        }
        foreach (var id in applied) m_received.Remove(id);
    }

    private IInputCommand Decode(ReceivedAuthorityCommand envelope) {
        if (envelope.OriginClientId == (m_host ? "host" : "client")) {
            if (!m_originals.TryGetValue(envelope.ClientCommandId, out var original))
                throw new IOException("Local command lost its original UI callback");
            m_originals.Remove(envelope.ClientCommandId);
            return original;
        }
        if (!m_codec.TryDeserialize(envelope.Payload, out var decoded, out var error))
            throw new IOException("Cannot decode guest/host command: " + error);
        return decoded;
    }

    private void AfterCommands() {
        if (m_ranFrame && !m_failed) m_paused = m_sim.IsSimPaused;
        else if (!m_host || m_session != null) m_gate.Hold();
    }

    private void EndFrame() {
        m_outer = true;
        if (!m_ranFrame || m_session == null || m_failed) return;
        m_ranFrame = false;
        try {
            m_session.ReportProgress(m_frame, m_nextSequence - 1);
            if (m_frame % 120 == 0) {
                if (!m_fingerprints.TryCapture(m_frame, m_nextSequence - 1, m_sim.CurrentStep.Value,
                    out var probe, out var error)) throw new IOException("State probe failed: " + error);
                m_localProbes[m_frame] = probe;
                m_session.ReportStateProbe(probe);
            }
            while (m_session.TryDequeueStateProbe(out var peer)) m_peerProbes[peer.AuthorityFrame] = peer;
            var done = new List<long>();
            foreach (var pair in m_peerProbes) {
                if (!m_localProbes.TryGetValue(pair.Key, out var local)) continue;
                if (!local.SameCoordinate(pair.Value) || !local.SameWorldFingerprint(pair.Value))
                    throw new IOException("World divergence at frame " + pair.Key);
                done.Add(pair.Key);
                LogMessage("LIVE STATE MATCH frame=" + pair.Key + " step=" + local.SimulationStep);
            }
            foreach (var key in done) { m_peerProbes.Remove(key); m_localProbes.Remove(key); }
            if (m_localProbes.Count > 20 || m_peerProbes.Count > 20)
                throw new IOException("Peer stopped confirming world state");
            m_frame++;
        }
        catch (Exception ex) {
            // EndForUi may not mutate world state. Flag the fault and perform
            // detach/freeze at the next CommandsProcessing boundary.
            m_session.Dispose();
            LogMessage("LIVE RESYNC REQUIRED: " + ex.Message);
        }
    }

    private void ReturnHostToSolo(string reason, Lyst<IInputCommand> target) {
        if (m_session != null) {
            m_session.Dispose(); // stop RX before draining its final accepted work
            PumpCommands();
            foreach (var pair in m_received) {
                if (pair.Key < m_nextSequence) continue;
                try { target.Add(Decode(pair.Value)); }
                catch (Exception ex) { LogMessage("Rejected detached peer command: " + ex.Message); }
            }
        }
        foreach (var pair in m_originals) target.Add(pair.Value);
        foreach (var command in m_heldDerived) target.Add(command);
        foreach (var command in m_local) target.Add(command);
        m_local.Clear();
        m_heldDerived.Clear();
        m_session = null;
        m_joinRequest = null;
        ResetEpoch();
        m_gate.RestoreSolo(m_paused);
        Status("HOST SOLO — " + reason + "; friend can join again");
    }

    private void FailGuest(string reason) {
        m_failed = true;
        m_session?.Dispose();
        m_commands.Clear();
        m_local.Clear();
        Hold();
        Status("DISCONNECTED — " + reason + ". Close game and Join again for current host world.");
    }

    private void ResetEpoch() {
        m_frame = 0;
        m_nextSequence = 0;
        m_readyOnce = false;
        m_ranFrame = false;
        m_failed = false;
        m_received.Clear();
        m_originals.Clear();
        m_localProbes.Clear();
        m_peerProbes.Clear();
    }

    private static void LogMessage(string text) => Log.Info("COI-Coop: " + text);

    private void Status(string text) {
        if (text == m_status) return;
        m_status = text;
        LogMessage(text);
        if (!string.IsNullOrWhiteSpace(m_statusPath)) {
            try { File.WriteAllText(m_statusPath, text); } catch (Exception ex) { LogMessage("Status file: " + ex.Message); }
        }
    }

    public void Dispose() {
        if (m_disposed) return;
        m_disposed = true;
        m_sim.Sync.RemoveNonSaveable(this, OnSync);
        m_loop.SyncUpdateEnd.RemoveNonSaveable(this, OnSyncEnd);
        m_sim.UpdateBeforeCmdProc.RemoveNonSaveable(this, BeforeCommands);
        m_sim.UpdateAfterCmdProc.RemoveNonSaveable(this, AfterCommands);
        m_sim.UpdateEndForUi.RemoveNonSaveable(this, EndFrame);
        m_session?.Dispose();
        m_server?.Dispose();
        m_request?.Complete(null, "Host world closed");
        Status("WORLD CLOSED");
    }
}
