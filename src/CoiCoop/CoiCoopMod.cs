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
    private DependencyResolver m_resolver;
    private InputScheduler m_scheduler;
    private ISimLoopEvents m_simLoop;
    private CommandRoundTripProbe m_roundTripProbe;
    private FieldInfo m_commandsToProcessField;
    private readonly HashSet<string> m_preprocessSeenTypes = new HashSet<string>(StringComparer.Ordinal);
    private bool m_preprocessPassthroughConfirmed;
    private bool m_preprocessFailureLogged;
    private bool m_gameHooksAttached;
    private int m_transportProbeStarted;

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

        StartTransportProbe();
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
        Log.Info("COI-Coop: pre-processing passthrough probe attached");
        Log.Info("COI-Coop: command observer attached");
    }

    private void OnBeforeCommandProcessing() {
        if (!JsonConfig.GetBool("probe_preprocess_passthrough")
            || m_scheduler == null
            || m_commandsToProcessField == null) {
            return;
        }

        try {
            var value = m_commandsToProcessField.GetValue(m_scheduler);
            if (!(value is Lyst<IInputCommand> commands)) {
                if (!m_preprocessFailureLogged) {
                    m_preprocessFailureLogged = true;
                    Log.Info("COI-Coop: PREPROCESS FAIL - command queue has unexpected runtime type");
                }
                return;
            }

            if (commands.Count == 0) {
                return;
            }

            // Dry-run for the future lockstep path. We take ownership of the queue,
            // then restore the exact same command objects in the exact same order.
            // No command is delayed, cloned, dropped, or executed by the mod here.
            var captured = new List<IInputCommand>(commands.Count);
            foreach (var command in commands) {
                captured.Add(command);
            }

            commands.Clear();
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

    private void OnCommandProcessed(IInputCommand command) {
        var commandType = command.GetType().FullName;

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

    private void StartTransportProbe() {
        if (Interlocked.Exchange(ref m_transportProbeStarted, 1) != 0) {
            return;
        }

        var mode = ReadEnvironmentInt("COI_COOP_MODE", JsonConfig.GetInt("network_mode"));
        var port = ReadEnvironmentInt("COI_COOP_PORT", JsonConfig.GetInt("server_port"));

        if (mode == 0) {
            Log.Info("COI-Coop: networking is disabled (mode 0)");
            return;
        }

        if (port < 1024 || port > 65535) {
            Log.Info($"COI-Coop: invalid port {port}; expected 1024-65535");
            return;
        }

        var thread = new Thread(() => RunTransportProbe(mode, port)) {
            IsBackground = true,
            Name = "COI-Coop transport probe"
        };
        thread.Start();
    }

    private static int ReadEnvironmentInt(string name, int fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static void RunTransportProbe(int mode, int port) {
        try {
            if (mode == 1) {
                Log.Info($"COI-Coop: HOST waiting on 127.0.0.1:{port}");
                var completed = LocalTransportProbe.HostOnce(port);
                Log.Info(completed
                    ? "COI-Coop: HOST handshake + ping completed successfully"
                    : "COI-Coop: HOST probe timed out waiting for a client");
                return;
            }

            if (mode == 2) {
                Log.Info($"COI-Coop: CLIENT connecting to 127.0.0.1:{port}");
                var connected = LocalTransportProbe.ClientOnce(port);
                Log.Info(connected
                    ? "COI-Coop: CLIENT handshake + ping completed successfully"
                    : "COI-Coop: CLIENT handshake was rejected");
                return;
            }

            Log.Info($"COI-Coop: unsupported network mode: {mode}");
        }
        catch (Exception ex) {
            Log.Info("COI-Coop: transport probe failed: " + ex);
        }
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) {
    }

    public void Dispose() {
        if (m_resolver != null) {
            m_resolver.ObjectInstantiated -= OnObjectInstantiated;
        }

        m_resolver = null;
        m_scheduler = null;
        m_simLoop = null;
        m_roundTripProbe = null;
        m_commandsToProcessField = null;
        m_preprocessSeenTypes.Clear();
        m_preprocessPassthroughConfirmed = false;
        m_preprocessFailureLogged = false;
        m_gameHooksAttached = false;
    }
}
