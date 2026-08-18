using System;

namespace CoiCoop.Networking;

internal sealed class StateProbeSnapshot {
    public long AuthorityFrame { get; }
    public long AuthoritySequence { get; }
    public long SimulationStep { get; }
    public int EntityCount { get; }
    public ulong EntityIdHash { get; }
    public ulong EntityStateHash { get; }
    public int HashedMemberCount { get; }

    public StateProbeSnapshot(
        long authorityFrame,
        long authoritySequence,
        long simulationStep,
        int entityCount,
        ulong entityIdHash,
        ulong entityStateHash,
        int hashedMemberCount) {

        AuthorityFrame = authorityFrame;
        AuthoritySequence = authoritySequence;
        SimulationStep = simulationStep;
        EntityCount = entityCount;
        EntityIdHash = entityIdHash;
        EntityStateHash = entityStateHash;
        HashedMemberCount = hashedMemberCount;
    }

    public bool SameCoordinate(StateProbeSnapshot other) {
        return other != null
            && AuthorityFrame == other.AuthorityFrame
            && AuthoritySequence == other.AuthoritySequence
            && SimulationStep == other.SimulationStep;
    }

    public bool SameWorldFingerprint(StateProbeSnapshot other) {
        return other != null
            && EntityCount == other.EntityCount
            && EntityIdHash == other.EntityIdHash
            && EntityStateHash == other.EntityStateHash
            && HashedMemberCount == other.HashedMemberCount;
    }

    public string Diff(StateProbeSnapshot other) {
        if (other == null) {
            return "peer probe missing";
        }

        var parts = new System.Collections.Generic.List<string>();
        if (AuthoritySequence != other.AuthoritySequence) {
            parts.Add("seq " + AuthoritySequence + "/" + other.AuthoritySequence);
        }
        if (SimulationStep != other.SimulationStep) {
            parts.Add("step " + SimulationStep + "/" + other.SimulationStep);
        }
        if (EntityCount != other.EntityCount) {
            parts.Add("entities " + EntityCount + "/" + other.EntityCount);
        }
        if (EntityIdHash != other.EntityIdHash) {
            parts.Add("idHash " + EntityIdHash.ToString("X16") + "/" + other.EntityIdHash.ToString("X16"));
        }
        if (EntityStateHash != other.EntityStateHash) {
            parts.Add("stateHash " + EntityStateHash.ToString("X16") + "/" + other.EntityStateHash.ToString("X16"));
        }
        if (HashedMemberCount != other.HashedMemberCount) {
            parts.Add("members " + HashedMemberCount + "/" + other.HashedMemberCount);
        }

        return parts.Count == 0 ? "no difference" : string.Join(", ", parts);
    }
}
