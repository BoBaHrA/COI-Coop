using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoiCoop.Networking;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.Input;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;

namespace CoiCoop;

public sealed class CoiCoopMod : IMod {
    private sealed class ReplayMarker {
        public IInputCommand Command { get; }
        public long AuthoritySequence { get; }
        public string OriginClientId { get; }

        public ReplayMarker(IInputCommand command, long authoritySequence, string originClientId) {
            Command = command;
            AuthoritySequence = authoritySequence;
            OriginClientId = originClientId;
        }
    }

    private DependencyResolver m_resolver;
    private InputScheduler m_scheduler;
    private ISimLoopEvents m_simLoop;
    private CommandRoundTripProbe m_roundTripProbe;
    private PersistentCommandSession m_networkSession;
    private FieldInfo m_commandsToProcessField;
    private readonly HashSet<string> m_preprocessSeenTypes = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> m_replayBypassSeenTypes = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<ReplayMarker> m_replayMarkers = new List<ReplayMarker>();
    private readonly Dictionary<long, IInputCommand> m_pendingLocalReplay = new Dictionary<long, IInputCommand>();
    private bool m_preprocessPassthroughConfirmed;
    private bool m_preprocessFailureLogged;
    private bool m_authoritativeReplayEnabled;
    private bool m_authoritativeReplayFaulted;
    private bool m_replayWaitingLogged;
    private bool m_replayEverConnected;
    private long m_nextExpectedAuthoritySequence;
    private bool m_gameHooksAttached;
    private int m_networkStarted;

    public ModManifest Manifest { get; }

    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }

    public ModJsonConfig JsonConfig { get; }

    public CoiCoopMod(ModManifest manifest) {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
        Log.Info("COI-Coop: constructed");
        Log.Info("COI-Coop: Mafi.Core assembly version " + typeof(InputScheduler).Assembly.GetName().Version);
    }

    public void RegisterPrototypes(ProtoRegistrator registrator) {
    }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded) {
    }

    public void EarlyInit(DependencyResolver resolver) {
    }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded) {
        m_resolver = resolver;
        m_roundTripProbe = new CommandRoundTripProbe(resolver);
        Log.Info("COI-Coop: Initialize; gameWasLoaded=" + gameWasLoaded);

        InputScheduler scheduler;
        if (resolver.TryGetResolvedDependency<InputScheduler>(out scheduler)) {
            m_scheduler = scheduler;
        }

        ISimLoopEvents simLoop;
        if (resolver.TryGetResolvedDependency<ISimLoopEvents>(out simLoop)) {
            m_simLoop = simLoop;
        }

        TryAttachGameHooks();
        if (!m_gameHooksAttached) {
            resolver.ObjectInstantiated += OnObjectInstantiated;
            Log.Info("COI-Coop: waiting for InputScheduler / ISimLoopEvents to be instantiated");
        }

        StartNetworking();
    }

    private void OnObjectInstantiated(object instance) {
        if (instance is InputScheduler scheduler) {
            m_scheduler = scheduler;
        }

        if (instance is ISimLoopEvents simLoop) {
            m_simLoop = simLoop;
        }

        TryAttachGameHooks();
        if (m_gameHooksAttached && m_resolver != null) {
            m_resolver.ObjectInstantiated -= OnObjectInstantiated;
        }
    }

    private void TryAttachGameHooks() {
        if (m_gameHooksAttached || m_scheduler == null || m_simLoop == null) {
            return;
        }

        var commandsField = typeof(InputScheduler).GetField(
            "m_commandsToProcess",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (commandsField == null) {
            Log.Info("COI-Coop: COMPATIBILITY FAIL - InputScheduler.m_commandsToProcess was not found. Game API changed.");
            return;
        }

        m_commandsToProcessField = commandsField;
        m_simLoop.UpdateBeforeCmdProc.AddNonSaveable(this, OnBeforeCommandProcessing);
        m_scheduler.OnCommandProcessed.AddNonSaveable(this, OnCommandProcessed);
        m_gameHooksAttached = true;

        Log.Info("COI-Coop: COMPATIBILITY OK - InputScheduler command queue found");
        Log.Info("COI-Coop: pre-processing command hook attached");
        Log.Info("COI-Coop: command observer attached");
    }

    private void OnBeforeCommandProcessing() {
        if (m_scheduler == null || m_commandsToProcessField == null) {
            DrainReceivedNetworkCommands(null);
            return;
        }

        Lyst<IInputCommand> commands;
        try {
            var value = m_commandsToProcessField.GetValue(m_scheduler);
            commands = value as Lyst<IInputCommand>;
            if (commands == null) {
                if (!m_preprocessFailureLogged) {
                    m_preprocessFailureLogged = true;
                    Log.Info("COI-Coop: PREPROCESS FAIL - command queue has unexpected runtime type");
                }
                DrainReceivedNetworkCommands(null);
                return;
            }
        }
        catch (Exception ex) {
            if (!m_preprocessFailureLogged) {
                m_preprocessFailureLogged = true;
                Log.Info("COI-Coop: PREPROCESS FAIL - " + ex.GetType().Name + ": " + ex.Message);
            }
            DrainReceivedNetworkCommands(null);
            return;
        }

        if (m_authoritativeReplayEnabled) {
            if (m_networkSession != null && m_networkSession.IsConnected && !m_authoritativeReplayFaulted) {
                m_replayEverConnected = true;
                m_replayWaitingLogged = false;
                RunAuthoritativeReplay(commands);
                return;
            }

            if (m_replayEverConnected && !m_authoritativeReplayFaulted) {
                HaltAuthoritativeReplay("network session disconnected after authority replay started");
            }

            if (!m_replayWaitingLogged) {
                m_replayWaitingLogged = true;
                Log.Info(
                    m_authoritativeReplayFaulted
                        ? "COI-Coop: REPLAY BLOCKED - synchronized session is faulted; reload before continuing co-op"
                        : "COI-Coop: REPLAY WAITING - peer is not connected; local player commands are blocked");
            }

            if (commands.Count > 0) {
                Log.Info("COI-Coop: REPLAY BLOCKED local command count=" + commands.Count);
                commands.Clear();
            }

            DrainReceivedNetworkCommands(null);
            return;
        }

        DrainReceivedNetworkCommands(null);

        if (!JsonConfig.GetBool("probe_preprocess_passthrough")) {
            return;
        }

        RunPreprocessPassthrough(commands);
    }

    private void RunPreprocessPassthrough(Lyst<IInputCommand> commands) {
        try {
            if (commands.Count == 0) {
                return;
            }

            // Dry-run for the lockstep path. Capture the queue, optionally send
            // decode-only network diagnostics, then restore the exact same command
            // objects in the exact same order.
            var captured = new List<IInputCommand>(commands.Count);
            foreach (var command in commands) {
                captured.Add(command);
            }

            commands.Clear();
            SendCapturedCommandsDecodeOnly(captured);

            foreach (var command in captured) {
                commands.Add(command);
            }

            if (!m_preprocessPassthroughConfirmed) {
                m_preprocessPassthroughConfirmed = true;
                Log.Info("COI-Coop: PREPROCESS PASSTHROUGH OK count=" + captured.Count);
            }

            foreach (var command in captured) {
                var typeName = command.GetType().FullName;
                if (m_preprocessSeenTypes.Add(typeName)) {
                    Log.Info("COI-Coop: PREPROCESS SEEN " + typeName);
                }
            }
        }
        catch (Exception ex) {
            if (!m_preprocessFailureLogged) {
                m_preprocessFailureLogged = true;
                Log.Info("COI-Coop: PREPROCESS FAIL - " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private void RunAuthoritativeReplay(Lyst<IInputCommand> commands) {
        var captured = new List<IInputCommand>(commands.Count);
        foreach (var command in commands) {
            captured.Add(command);
        }
        commands.Clear();

        if (captured.Count > 0) {
            var payloads = new List<byte[]>(captured.Count);
            foreach (var command in captured) {
                byte[] payload;
                string error = "round-trip probe is unavailable";
                if (m_roundTripProbe == null
                    || !m_roundTripProbe.TrySerialize(command, out payload, out error)) {
                    HaltAuthoritativeReplay(
                        "could not serialize local command " + command.GetType().FullName + ": " + error);
                    return;
                }
                payloads.Add(payload);
            }

            for (var i = 0; i < captured.Count; i++) {
                long localCommandId;
                if (m_networkSession == null
                    || !m_networkSession.SubmitLocalCommand(payloads[i], out localCommandId)) {
                    HaltAuthoritativeReplay(
                        "network submit failed for local command " + captured[i].GetType().FullName);
                    return;
                }

                if (m_pendingLocalReplay.ContainsKey(localCommandId)) {
                    HaltAuthoritativeReplay("duplicate local command id " + localCommandId);
                    return;
                }

                // Keep the exact originating command object alive until its
                // authority COMMIT comes back. Some COI UI tools keep identity-
                // based state/callbacks on the original command instance. The
                // remote peer still executes a deserialized clone.
                m_pendingLocalReplay.Add(localCommandId, captured[i]);

                Log.Info(
                    "COI-Coop: REPLAY SUBMIT localId=" + localCommandId
                    + " type=" + captured[i].GetType().FullName
                    + " bytes=" + payloads[i].Length);
            }
        }

        // Only authority COMMITs are put back into the scheduler. On the command's
        // originating peer we restore the exact local object; on the other peer we
        // replay the deserialized clone. Both still follow the same authority order.
        DrainReceivedNetworkCommands(commands);
    }

    private void SendCapturedCommandsDecodeOnly(List<IInputCommand> captured) {
        if (m_networkSession == null
            || !m_networkSession.IsConnected
            || m_roundTripProbe == null) {
            return;
        }

        foreach (var command in captured) {
            byte[] payload;
            string error;
            if (!m_roundTripProbe.TrySerialize(command, out payload, out error)) {
                Log.Info(
                    "COI-Coop: NETWORK TX FAIL type=" + command.GetType().FullName
                    + " error=" + error);
                continue;
            }

            if (m_networkSession.SubmitLocalCommand(payload)) {
                Log.Info(
                    "COI-Coop: NETWORK TX type=" + command.GetType().FullName
                    + " bytes=" + payload.Length);
            }
        }
    }

    private void DrainReceivedNetworkCommands(Lyst<IInputCommand> replayTarget) {
        if (m_networkSession == null || m_roundTripProbe == null) {
            return;
        }

        ReceivedAuthorityCommand received;
        while (m_networkSession.TryDequeueReceived(out received)) {
            IInputCommand decodedCommand;
            string error;
            if (!m_roundTripProbe.TryDeserialize(received.Payload, out decodedCommand, out error)) {
                Log.Info(
                    "COI-Coop: NETWORK RX FAIL seq=" + received.AuthoritySequence
                    + " origin=" + received.OriginClientId
                    + " error=" + error);
                if (m_authoritativeReplayEnabled) {
                    HaltAuthoritativeReplay("authority payload could not be deserialized");
                }
                continue;
            }

            if (replayTarget == null || !m_authoritativeReplayEnabled || m_authoritativeReplayFaulted) {
                Log.Info(
                    "COI-Coop: NETWORK RX OK seq=" + received.AuthoritySequence
                    + " origin=" + received.OriginClientId
                    + " id=" + received.ClientCommandId
                    + " type=" + decodedCommand.GetType().FullName
                    + " bytes=" + received.Payload.Length
                    + " replay=OFF");
                continue;
            }

            if (received.AuthoritySequence < m_nextExpectedAuthoritySequence) {
                Log.Info(
                    "COI-Coop: REPLAY DUPLICATE ignored seq=" + received.AuthoritySequence
                    + " expected=" + m_nextExpectedAuthoritySequence);
                continue;
            }

            if (received.AuthoritySequence != m_nextExpectedAuthoritySequence) {
                HaltAuthoritativeReplay(
                    "authority sequence gap: expected " + m_nextExpectedAuthoritySequence
                    + " but received " + received.AuthoritySequence);
                continue;
            }

            var replayCommand = decodedCommand;
            var usedLocalOriginal = false;
            if (string.Equals(
                    received.OriginClientId,
                    m_networkSession.LocalClientId,
                    StringComparison.Ordinal)) {

                IInputCommand localOriginal;
                if (!m_pendingLocalReplay.TryGetValue(received.ClientCommandId, out localOriginal)) {
                    HaltAuthoritativeReplay(
                        "local authority COMMIT has no pending original: id="
                        + received.ClientCommandId);
                    continue;
                }

                replayCommand = localOriginal;
                m_pendingLocalReplay.Remove(received.ClientCommandId);
                usedLocalOriginal = true;
            }

            replayTarget.Add(replayCommand);
            m_replayMarkers.Add(new ReplayMarker(
                replayCommand,
                received.AuthoritySequence,
                received.OriginClientId));

            Log.Info(
                "COI-Coop: REPLAY QUEUED seq=" + received.AuthoritySequence
                + " origin=" + received.OriginClientId
                + " id=" + received.ClientCommandId
                + " type=" + replayCommand.GetType().FullName
                + " localOriginal=" + (usedLocalOriginal ? "YES" : "NO")
                + " bytes=" + received.Payload.Length);

            m_nextExpectedAuthoritySequence++;
        }
    }

    private void HaltAuthoritativeReplay(string reason) {
        if (m_authoritativeReplayFaulted) {
            return;
        }

        m_authoritativeReplayFaulted = true;
        Log.Info("COI-Coop: REPLAY HALT - " + reason);
    }

    private bool TryConsumeReplayMarker(IInputCommand command, out ReplayMarker marker) {
        for (var i = 0; i < m_replayMarkers.Count; i++) {
            if (!ReferenceEquals(m_replayMarkers[i].Command, command)) {
                continue;
            }

            marker = m_replayMarkers[i];
            m_replayMarkers.RemoveAt(i);
            return true;
        }

        marker = null;
        return false;
    }

    private void OnCommandProcessed(IInputCommand command) {
        var commandType = command.GetType().FullName;

        ReplayMarker replayMarker;
        var wasAuthorityReplay = TryConsumeReplayMarker(command, out replayMarker);
        if (m_authoritativeReplayEnabled && m_networkSession != null && m_networkSession.IsConnected) {
            if (wasAuthorityReplay) {
                Log.Info(
                    "COI-Coop: REPLAY APPLIED seq=" + replayMarker.AuthoritySequence
                    + " origin=" + replayMarker.OriginClientId
                    + " type=" + commandType);
            }
            else if (m_replayBypassSeenTypes.Add(commandType)) {
                Log.Info(
                    "COI-Coop: REPLAY DERIVED/BYPASS type=" + commandType
                    + " (processed without an authority marker)");
            }
        }

        if (JsonConfig.GetBool("trace_commands")) {
            Log.Info("COI-Coop: INPUT " + commandType);
        }

        if (!JsonConfig.GetBool("probe_command_serialization") || m_roundTripProbe == null) {
            return;
        }

        int payloadLength;
        string cloneType;
        string error;
        if (m_roundTripProbe.TryRoundTrip(command, out payloadLength, out cloneType, out error)) {
            Log.Info(
                "COI-Coop: SERIALIZE OK type=" + commandType
                + " bytes=" + payloadLength
                + " clone=" + cloneType);
        }
        else {
            Log.Info(
                "COI-Coop: SERIALIZE FAIL type=" + commandType
                + " error=" + error);
        }
    }

    private void StartNetworking() {
        if (Interlocked.Exchange(ref m_networkStarted, 1) != 0) {
            return;
        }

        var mode = ReadEnvironmentInt("COI_COOP_MODE", JsonConfig.GetInt("network_mode"));
        var port = ReadEnvironmentInt("COI_COOP_PORT", JsonConfig.GetInt("server_port"));

        if (mode == 0) {
            m_authoritativeReplayEnabled = false;
            Log.Info("COI-Coop: networking is disabled (mode 0)");
            return;
        }

        if (port < 1024 || port > 65535) {
            m_authoritativeReplayEnabled = false;
            Log.Info($"COI-Coop: invalid port {port}; expected 1024-65535");
            return;
        }

        if (mode != 1 && mode != 2) {
            m_authoritativeReplayEnabled = false;
            Log.Info($"COI-Coop: unsupported network mode: {mode}");
            return;
        }

        m_authoritativeReplayEnabled = ReadEnvironmentBool(
            "COI_COOP_REPLAY",
            JsonConfig.GetBool("experimental_authoritative_replay"));

        var isHost = mode == 1;
        m_networkSession = new PersistentCommandSession(
            isHost,
            port,
            message => Log.Info("COI-Coop: NET " + message));
        m_networkSession.Start();

        Log.Info(
            isHost
                ? $"COI-Coop: persistent HOST session starting on 127.0.0.1:{port}"
                : $"COI-Coop: persistent CLIENT session connecting to 127.0.0.1:{port}");
        Log.Info(
            m_authoritativeReplayEnabled
                ? "COI-Coop: EXPERIMENTAL AUTHORITATIVE REPLAY = ON"
                : "COI-Coop: network command replay is OFF; received commands are decode-only");
    }

    private static int ReadEnvironmentInt(string name, int fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool ReadEnvironmentBool(string name, bool fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            return fallback;
        }

        if (string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return fallback;
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) {
    }

    public void Dispose() {
        if (m_resolver != null) {
            m_resolver.ObjectInstantiated -= OnObjectInstantiated;
        }

        m_networkSession?.Dispose();
        m_networkSession = null;
        m_resolver = null;
        m_scheduler = null;
        m_simLoop = null;
        m_roundTripProbe = null;
        m_commandsToProcessField = null;
        m_preprocessSeenTypes.Clear();
        m_replayBypassSeenTypes.Clear();
        m_replayMarkers.Clear();
        m_pendingLocalReplay.Clear();
        m_preprocessPassthroughConfirmed = false;
        m_preprocessFailureLogged = false;
        m_authoritativeReplayEnabled = false;
        m_authoritativeReplayFaulted = false;
        m_replayWaitingLogged = false;
        m_replayEverConnected = false;
        m_nextExpectedAuthoritySequence = 0;
        m_gameHooksAttached = false;
    }
}
