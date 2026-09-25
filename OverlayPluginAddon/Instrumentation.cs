using System;
using System.Collections.Generic;
using System.Text;
using OverlayPluginAddon.Fflogs;

namespace OverlayPluginAddon
{
    /// <summary>
    /// Counters and raw-line samples covering every stage between "ACT read a log line" and "a
    /// number reached the overlay", for both halves - the GCD model measured here and the rDPS
    /// family measured by FFLogs' parser.
    ///
    /// Every field offset this addon uses was written from the documented shape of the FFXIV log
    /// lines. When the output is wrong the failure is silent - a mis-indexed name simply matches
    /// nobody and every column reads zero - so the only way to tell the stages apart is to count
    /// them individually and keep a few raw lines to eyeball.
    /// </summary>
    public class Instrumentation
    {
        public bool ExportVariablesRegistered;
        public bool EventSourceStarted;

        /// <summary>Every log line ACT handed us, by line type.</summary>
        public readonly Dictionary<int, int> LinesByType = new Dictionary<int, int>();

        public int TotalLines;
        public int LinesWithoutOriginal;

        public int CastStartsSeen;
        public int CastCancelsSeen;

        public int AbilityLinesSeen;
        public int AbilityLinesTooShort;
        public int AbilityLinesNonPlayerSource;
        public int AbilityLinesBadActionId;
        public int AbilityLinesNotGcd;
        public int GcdsRecorded;
        public int HardCastsRecorded;

        private const int MaxSamples = 3;
        private readonly Dictionary<int, List<string>> samples = new Dictionary<int, List<string>>();

        public void CountLine(int type, string line)
        {
            TotalLines++;
            LinesByType.TryGetValue(type, out var count);
            LinesByType[type] = count + 1;

            // A handful of each interesting type, kept verbatim. Comparing these against the field
            // offsets in StatusTracker / EventSource settles in seconds what no amount of
            // reasoning about the format will.
            if (type != 20 && type != 21 && type != 22 && type != 23 && type != 26 && type != 30 && type != 3 && type != 2) return;

            if (!samples.TryGetValue(type, out var list))
                samples[type] = list = new List<string>();
            if (list.Count < MaxSamples) list.Add(line);
        }

        public void Reset()
        {
            LinesByType.Clear();
            samples.Clear();

            TotalLines = LinesWithoutOriginal = 0;
            CastStartsSeen = CastCancelsSeen = 0;
            AbilityLinesSeen = AbilityLinesTooShort = AbilityLinesNonPlayerSource = 0;
            AbilityLinesBadActionId = AbilityLinesNotGcd = GcdsRecorded = HardCastsRecorded = 0;
        }

        public void Describe(StringBuilder sb, StatusTracker statuses, ActionCategories categories,
                             ActionData actionData, GcdTracker gcds, ParserHost parser, MeterSnapshot meters)
        {
            sb.AppendLine("=== OverlayPluginAddon diagnostics ===");
            sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss}{1}", DateTime.Now, Environment.NewLine);
            sb.AppendLine();

            sb.AppendLine("-- startup --");
            sb.AppendFormat("  event source started      : {0}{1}", EventSourceStarted, Environment.NewLine);
            sb.AppendFormat("  export variables added    : {0}{1}", ExportVariablesRegistered, Environment.NewLine);
            sb.AppendFormat("  actions.json              : {0}{1}",
                actionData == null ? "not loaded"
                    : actionData.LoadError ?? string.Format("{0} recasts, {1} haste statuses, {2} onGcd ids", actionData.ActionCount, actionData.SpeedStatusCount, actionData.OnGcdCount),
                Environment.NewLine);
            sb.AppendFormat("  action categories         : {0}{1}",
                categories == null ? "not loaded"
                    : categories.LoadError ?? string.Format("{0} GCD actions", categories.Count),
                Environment.NewLine);
            sb.AppendLine();

            // The rDPS half. "applied = False" with a fight present is the usual answer to "the
            // rDPS columns are empty": the parser has a fight, it is just not the pull ACT is
            // reporting, and Reason says which rule refused it.
            sb.AppendLine("-- FFLogs parser --");
            if (parser == null)
            {
                sb.AppendFormat("  running                   : no (parser-ff.js failed to load){0}", Environment.NewLine);
            }
            else
            {
                sb.AppendFormat("  running                   : {0} (build {1}){2}", parser.Available, ParserHost.ParserVersion, Environment.NewLine);
                sb.AppendFormat("  lines parsed / errors     : {0} / {1}{2}", parser.LinesParsed, parser.LineErrors, Environment.NewLine);
                sb.AppendFormat("  collects                  : {0}{1}", parser.Collections, Environment.NewLine);
                sb.AppendFormat("  queued                    : {0}{1}", parser.QueueLength, Environment.NewLine);
                if (!string.IsNullOrEmpty(parser.ParserWarning))
                    sb.AppendFormat("  warning                   : {0}{1}", parser.ParserWarning, Environment.NewLine);
                if (!string.IsNullOrEmpty(parser.LastError))
                    sb.AppendFormat("  last error                : {0}{1}", parser.LastError, Environment.NewLine);
            }
            if (meters != null)
            {
                sb.AppendFormat("  fight                     : {0} ({1}){2}", meters.FightId, meters.FightState, Environment.NewLine);
                sb.AppendFormat("  applied                   : {0} ({1}){2}", meters.Applied, meters.Reason, Environment.NewLine);
                sb.AppendFormat("  clocks                    : {0:N1}s total, {1:N1}s downtime, {2:N1}s for damage{3}",
                    meters.Clocks.Seconds, meters.Clocks.Downtime, meters.Clocks.Active, Environment.NewLine);
                sb.AppendFormat("  downtime windows          : {0}{1}", meters.Downtime.Count, Environment.NewLine);
                foreach (var window in meters.Downtime)
                    sb.AppendFormat("      {0:N1}s long{1}", window.Seconds, Environment.NewLine);
                sb.AppendFormat("  fight end for GCD         : {0}{1}",
                    meters.FightEnded
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)meters.FightEndMs).LocalDateTime.ToString("HH:mm:ss.fff") + " (presses after it are not counted)"
                        : "none, fight in progress",
                    Environment.NewLine);
                sb.AppendFormat("  rows                      : {0}{1}", string.Join(", ", meters.Names), Environment.NewLine);
            }
            sb.AppendLine();

            sb.AppendLine("-- log lines --");
            sb.AppendFormat("  total seen                : {0}{1}", TotalLines, Environment.NewLine);
            sb.AppendFormat("  missing originalLogLine   : {0}{1}", LinesWithoutOriginal, Environment.NewLine);
            foreach (var kv in LinesByType)
                sb.AppendFormat("    type {0,-3}                 : {1}{2}", kv.Key, kv.Value, Environment.NewLine);
            sb.AppendLine();

            sb.AppendLine("-- status parsing (26 / 30, for haste) --");
            sb.AppendFormat("  applies                   : {0}{1}", statuses?.AppliesSeen ?? 0, Environment.NewLine);
            sb.AppendFormat("  removes                   : {0}{1}", statuses?.RemovesSeen ?? 0, Environment.NewLine);
            sb.AppendFormat("  malformed                 : {0}{1}", statuses?.MalformedLines ?? 0, Environment.NewLine);
            sb.AppendLine();

            sb.AppendLine("-- players identified --");
            sb.AppendFormat("  local player (from line 02)  : {0}{1}",
                statuses?.LocalPlayerName ?? "UNKNOWN - ACT's \"YOU\" cannot be translated", Environment.NewLine);
            var players = statuses?.KnownPlayers;
            sb.AppendFormat("  count                     : {0}{1}", players?.Count ?? 0, Environment.NewLine);
            if (players != null)
                foreach (var name in players)
                    sb.AppendFormat("    {0}{1}", name, Environment.NewLine);
            sb.AppendLine();

            sb.AppendLine("-- cast bars (20 / 23) --");
            sb.AppendFormat("  cast starts               : {0}{1}", CastStartsSeen, Environment.NewLine);
            sb.AppendFormat("  cast cancels              : {0}{1}", CastCancelsSeen, Environment.NewLine);
            sb.AppendLine();

            sb.AppendLine("-- ability lines (21 / 22, for GCD counting) --");
            sb.AppendFormat("  seen                      : {0}{1}", AbilityLinesSeen, Environment.NewLine);
            sb.AppendFormat("  too few fields            : {0}{1}", AbilityLinesTooShort, Environment.NewLine);
            sb.AppendFormat("  source not a player id    : {0}{1}", AbilityLinesNonPlayerSource, Environment.NewLine);
            sb.AppendFormat("  action id unparseable     : {0}{1}", AbilityLinesBadActionId, Environment.NewLine);
            sb.AppendFormat("  not a GCD                 : {0}{1}", AbilityLinesNotGcd, Environment.NewLine);
            sb.AppendFormat("  recorded as GCD           : {0}{1}", GcdsRecorded, Environment.NewLine);
            sb.AppendFormat("    of which hard cast      : {0}{1}", HardCastsRecorded, Environment.NewLine);
            sb.AppendLine();

            sb.AppendLine("-- GCD tracker --");
            var entries = 0;
            if (gcds != null)
            {
                foreach (var player in gcds.Players)
                {
                    entries++;
                    var st = gcds.StatsFor(player);
                    sb.AppendFormat("    {0,-28} casts={1} uptime={2:0.0}% lost={3:0.0}s recast={4:0.00}s speedStat={5}{6}{7}{8}",
                        player, st.Count, st.Uptime * 100, st.Clip, st.Recast, st.SpeedStat,
                        st.RecastEstimated ? "" : " (default)",
                        st.AfterEnd > 0 ? " afterEnd=" + st.AfterEnd : "", Environment.NewLine);
                }
            }
            if (entries == 0) sb.AppendLine("    (empty)");
            sb.AppendLine();

            sb.AppendLine("-- raw log line samples --");
            sb.AppendLine("   Compare these against the field offsets in StatusTracker.cs and");
            sb.AppendLine("   EventSource.cs. Field 0 is the line type.");
            foreach (var kv in samples)
            {
                sb.AppendFormat("  type {0}:{1}", kv.Key, Environment.NewLine);
                foreach (var line in kv.Value)
                {
                    sb.AppendFormat("    {0}{1}", line, Environment.NewLine);
                    var f = line.Split('|');
                    for (var i = 0; i < f.Length && i < 12; i++)
                        sb.AppendFormat("      [{0,2}] {1}{2}", i, f[i], Environment.NewLine);
                    sb.AppendLine();
                }
            }
        }
    }
}
