using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverlayPluginAddon
{
    /// <summary>One active status on one actor.</summary>
    public struct ActiveStatus
    {
        public uint StatusId;
        public string SourceName;
    }

    /// <summary>
    /// Tracks "who currently has which status, applied by whom", fed from the raw FFXIV log lines.
    ///
    /// The GCD tracker needs two things out of it: the haste statuses sitting on a player at the
    /// moment they press a GCD (Ley Lines, Presence of Mind, Inspiration, ...), and which actors
    /// are players at all, so mobs and pets stay out of the uptime table.
    ///
    /// Only statuses whose *source* is a player are recorded - every haste status is self-applied,
    /// and a boss debuffing itself is nobody's business here.
    ///
    /// Field offsets below are the network status lines emitted by FFXIV_ACT_Plugin. The
    /// diagnostics dump keeps a few raw lines next to these constants so a layout change shows up
    /// as a mis-indexed field rather than a silent column of zeroes.
    /// </summary>
    public class StatusTracker
    {
        // 26|timestamp|statusId|statusName|duration|sourceId|sourceName|targetId|targetName|stacks|...
        // 30|timestamp|statusId|statusName|duration|sourceId|sourceName|targetId|targetName|stacks|...
        private const int FieldStatusId = 2;
        private const int FieldSourceId = 5;
        private const int FieldSourceName = 6;
        private const int FieldTargetId = 7;
        private const int FieldTargetName = 8;
        private const int MinFields = 9;

        private readonly Dictionary<string, Dictionary<uint, string>> byTarget =
            new Dictionary<string, Dictionary<uint, string>>(StringComparer.Ordinal);

        /// <summary>
        /// Names positively identified as players, from actor ids in status lines, ability lines
        /// and AddCombatant.
        /// </summary>
        private readonly HashSet<string> players = new HashSet<string>(StringComparer.Ordinal);

        public int AppliesSeen { get; private set; }
        public int RemovesSeen { get; private set; }
        public int MalformedLines { get; private set; }

        /// <summary>
        /// ACT renames the logging player's combatant to this, while the log lines keep using their
        /// real character name. Everything keyed off a log line therefore lives under one name and
        /// everything keyed off ACT lives under another, and without translating between them the
        /// local player matches nothing anywhere.
        /// </summary>
        private const string LocalPlayerAlias = "YOU";

        /// <summary>Real character name of the player running ACT, from log line 02.</summary>
        public string LocalPlayerName { get; private set; }

        /// <summary>Handles a 02 ChangePrimaryPlayer line: 02|timestamp|actorId|actorName.</summary>
        public void HandlePrimaryPlayer(string[] f)
        {
            if (f.Length > 3 && !string.IsNullOrEmpty(f[3])) LocalPlayerName = f[3];
            if (f.Length > 3) NotePlayer(f[2], f[3]);
        }

        /// <summary>
        /// Turns a name ACT gave us into the one the log lines use. Identity for everybody except
        /// the logging player, and identity for them too if ACT is not aliasing.
        /// </summary>
        public string Resolve(string actName)
        {
            if (string.IsNullOrEmpty(actName)) return actName;
            if (LocalPlayerName == null) return actName;

            if (string.Equals(actName, LocalPlayerAlias, StringComparison.Ordinal)) return LocalPlayerName;

            // Pets carry the owner in brackets, and the owner is aliased the same way.
            var open = actName.IndexOf(" (" + LocalPlayerAlias + ")", StringComparison.Ordinal);
            if (open > 0) return actName.Substring(0, open) + " (" + LocalPlayerName + ")";

            return actName;
        }

        public bool IsPlayer(string name) => name != null && players.Contains(name);

        /// <summary>Everyone positively identified as a player. Empty here explains an empty GCD table.</summary>
        public ICollection<string> KnownPlayers => players;

        /// <summary>
        /// Notes an actor as a player, from any line that carries an actor id. Ability lines are
        /// the useful source: AddCombatant only fires on zone change, so a session that starts
        /// mid-instance would otherwise never identify anybody.
        /// </summary>
        public void NotePlayer(string hexId, string name)
        {
            if (IsPlayerId(hexId) && !string.IsNullOrEmpty(name)) players.Add(name);
        }

        public void Clear()
        {
            byTarget.Clear();
        }

        /// <summary>
        /// Actor ids in the 0x1........ range are players; everything else (0x4........ and friends)
        /// is an NPC/enemy.
        /// </summary>
        private static bool IsPlayerId(string hexId)
        {
            return !string.IsNullOrEmpty(hexId) && hexId[0] == '1';
        }

        /// <summary>Handles a 03 AddCombatant line, which names actors authoritatively.</summary>
        public void HandleAddCombatant(string[] f)
        {
            // 03|timestamp|actorId|actorName|jobId|level|...
            if (f.Length > 3 && IsPlayerId(f[2]) && !string.IsNullOrEmpty(f[3]))
                players.Add(f[3]);
        }

        /// <summary>Handles one 26 (apply) or 30 (remove) line. Returns false if it could not be parsed.</summary>
        public bool HandleStatusLine(bool isApply, string[] f)
        {
            if (f.Length < MinFields)
            {
                MalformedLines++;
                return false;
            }

            if (!uint.TryParse(f[FieldStatusId], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var statusId))
            {
                MalformedLines++;
                return false;
            }

            var sourceName = f[FieldSourceName];
            var targetName = f[FieldTargetName];
            if (string.IsNullOrEmpty(targetName)) return false;

            if (IsPlayerId(f[FieldTargetId])) players.Add(targetName);
            if (IsPlayerId(f[FieldSourceId]) && !string.IsNullOrEmpty(sourceName)) players.Add(sourceName);

            // Only player-applied statuses matter here; haste is always self-applied.
            if (!IsPlayerId(f[FieldSourceId])) return false;

            if (isApply)
            {
                AppliesSeen++;

                if (!byTarget.TryGetValue(targetName, out var onTarget))
                    byTarget[targetName] = onTarget = new Dictionary<uint, string>();
                onTarget[statusId] = sourceName;
            }
            else
            {
                RemovesSeen++;

                if (byTarget.TryGetValue(targetName, out var onTarget))
                    onTarget.Remove(statusId);
            }

            return true;
        }

        /// <summary>Statuses currently active on <paramref name="actorName"/>. Never null.</summary>
        public IEnumerable<ActiveStatus> ActiveOn(string actorName)
        {
            if (actorName == null || !byTarget.TryGetValue(actorName, out var onTarget))
                yield break;

            foreach (var kv in onTarget)
                yield return new ActiveStatus { StatusId = kv.Key, SourceName = kv.Value };
        }

        /// <summary>Death or removal wipes every status off the actor.</summary>
        public void RemoveActor(string name)
        {
            if (name == null) return;
            byTarget.Remove(name);
        }
    }
}
