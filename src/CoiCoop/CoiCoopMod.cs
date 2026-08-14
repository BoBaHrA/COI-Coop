using System;
using System.Reflection;
using System.Threading;
using CoiCoop.Networking;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Input;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;

namespace CoiCoop;

public sealed class CoiCoopMod : IMod {
    private DependencyResolver m_resolver;
    private InputScheduler m_scheduler;
    private ISimLoopEvents m_simLoop;
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
    }

    public void RegisterPrototypes(ProtoRegistrator registrator) {
    }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded) {
    }

    public void EarlyInit(DependencyResolver resolver) {
    }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded) {
        m_resolver = resolver;
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

        m_scheduler.OnCommandProcessed.AddNonSaveable(this, OnCommandProcessed);
        m_gameHooksAttached = true;

        Log.Info("COI-Coop: COMPATIBILITY OK - InputScheduler command queue found");
        Log.Info("COI-Coop: command observer attached");
    }

    private void OnCommandProcessed(IInputCommand command) {
        if (!JsonConfig.GetBool("trace_commands")) {
            return;
        }

        Log.Info("COI-Coop: INPUT " + command.GetType().FullName);
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
                LocalTransportProbe.HostOnce(port);
                Log.Info("COI-Coop: HOST handshake + ping completed successfully");
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
        m_gameHooksAttached = false;
    }
}
