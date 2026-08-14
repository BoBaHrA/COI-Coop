using System;
using System.Collections.Generic;

namespace CoiCoop.Networking;

/// <summary>
/// Pure host-side ordering policy. It does not know about Captain of Industry
/// command types; adapters decide which commands are ordered vs superseding.
/// </summary>
internal sealed class AuthorityCommandSequencer {
    private readonly HashSet<string> m_seenClientCommands = new HashSet<string>(StringComparer.Ordinal);
    private long m_nextAuthoritySequence;

    public bool TryAccept(
        string clientId,
        long clientCommandId,
        string conflictKey,
        CommandConflictMode conflictMode,
        byte[] payload,
        out AuthorityCommandEnvelope envelope) {

        var dedupeKey = clientId + ":" + clientCommandId;
        if (!m_seenClientCommands.Add(dedupeKey)) {
            envelope = null;
            return false;
        }

        envelope = new AuthorityCommandEnvelope(
            m_nextAuthoritySequence++,
            clientId,
            clientCommandId,
            conflictKey,
            conflictMode,
            payload);
        return true;
    }

    /// <summary>
    /// Collapses only explicitly superseding intents inside one uncommitted
    /// authority batch. Ordered commands are always preserved.
    ///
    /// Example: two destination commands for the same ship in one batch result
    /// in only the later destination command being committed.
    /// </summary>
    public static List<AuthorityCommandEnvelope> CollapseSuperseded(
        IReadOnlyList<AuthorityCommandEnvelope> commands) {

        if (commands == null) throw new ArgumentNullException(nameof(commands));

        var lastSupersedingIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < commands.Count; i++) {
            var command = commands[i];
            if (command.ConflictMode == CommandConflictMode.Supersede) {
                lastSupersedingIndexByKey[command.ConflictKey] = i;
            }
        }

        var result = new List<AuthorityCommandEnvelope>(commands.Count);
        for (var i = 0; i < commands.Count; i++) {
            var command = commands[i];
            if (command.ConflictMode == CommandConflictMode.Ordered) {
                result.Add(command);
                continue;
            }

            if (lastSupersedingIndexByKey.TryGetValue(command.ConflictKey, out var lastIndex)
                && lastIndex == i) {
                result.Add(command);
            }
        }

        return result;
    }
}
