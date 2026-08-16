using System;
using System.Collections.Generic;
using CoiCoop.Networking;
using Mafi;
using Mafi.Core.Entities;

namespace CoiCoop;

/// <summary>
/// Lightweight runtime world probe.
///
/// The previous development implementation reflected over every stable-looking
/// field of every entity. On a normal island that meant roughly 200k+ member
/// reads on the simulation/UI thread every probe and caused visible periodic
/// stalls. Runtime co-op must never do that.
///
/// This probe intentionally keeps only a cheap structural fingerprint:
/// entity count plus a deterministic hash of sorted EntityIds. Deep subsystem
/// fingerprints belong behind an explicit diagnostics command/tool, not in the
/// normal gameplay loop.
/// </summary>
internal sealed class WorldStateFingerprintBuilder {
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly DependencyResolver m_resolver;
    private EntitiesManager m_entities;

    public WorldStateFingerprintBuilder(DependencyResolver resolver) {
        m_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        // The LAN bridge must own the client-side loopback ports before any of the
        // proven loopback sessions begin their retry loops.
        LanTransportBootstrap.EnsureStarted();
        PlacementPreviewBootstrap.EnsureStarted(resolver);
        PathPreviewBootstrap.EnsureStarted(resolver);
        BlueprintPreviewBootstrap.EnsureStarted(resolver);
    }

    public bool TryCapture(
        long authorityFrame,
        long authoritySequence,
        long simulationStep,
        out StateProbeSnapshot snapshot,
        out string error) {

        snapshot = null;
        error = null;

        try {
            if (m_entities == null) {
                EntitiesManager entities;
                if (!m_resolver.TryGetResolvedDependency<EntitiesManager>(out entities) || entities == null) {
                    error = "EntitiesManager is unavailable";
                    return false;
                }
                m_entities = entities;
            }

            var ids = new List<int>(m_entities.EntitiesCount);
            foreach (IEntity entity in m_entities.Entities) {
                if (entity != null) ids.Add(entity.Id.Value);
            }
            ids.Sort();

            var idHash = FnvOffset;
            for (var i = 0; i < ids.Count; i++) {
                unchecked {
                    var value = (uint)ids[i];
                    idHash ^= (byte)value;
                    idHash *= FnvPrime;
                    idHash ^= (byte)(value >> 8);
                    idHash *= FnvPrime;
                    idHash ^= (byte)(value >> 16);
                    idHash *= FnvPrime;
                    idHash ^= (byte)(value >> 24);
                    idHash *= FnvPrime;
                }
            }

            // EntityStateHash is deliberately structural-only during normal play.
            // A value derived from the same ordered ids preserves the existing wire
            // format without pretending that deep mutable state was inspected.
            var structuralStateHash = idHash ^ ((ulong)(uint)ids.Count * 0x9E3779B185EBCA87UL);

            snapshot = new StateProbeSnapshot(
                authorityFrame,
                authoritySequence,
                simulationStep,
                ids.Count,
                idHash,
                structuralStateHash,
                ids.Count);
            return true;
        }
        catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
