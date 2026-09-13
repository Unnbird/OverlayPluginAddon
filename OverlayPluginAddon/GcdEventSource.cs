using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin;

namespace OverlayPluginAddon
{
    /// <summary>
    /// Measures every player's GCD uptime off ACT's log-reading loop and publishes it as ACT
    /// export variables, so any overlay that reads CombatData (mopimopi included) gets the
    /// columns without further plumbing.
    ///
    /// The model is xivanalysis' - see <see cref="GcdTracker"/>. This class only feeds it: which
    /// lines are GCDs, when the button actually went down, whether the cast bar ran, and what
    /// haste was on the player at that moment.
    /// </summary>
    public class GcdEventSource : EventSourceBase
    {
        private const string GcdUpdateEvent = "onGcdUpdate";

        // Log line types we care about, as emitted by FFXIV_ACT_Plugin.
        private const int LineChangeZone = 1;
        private const int LinePrimaryPlayer = 2;
        private const int LineAddCombatant = 3;
        private const int LineRemoveCombatant = 4;
        private const int LineDeath = 25;
        private const int LineStartsCasting = 20;
        private const int LineCancelCast = 23;
        private const int LineAbility = 21;
        private const int LineAoeAbility = 22;
        private const int LineStatusApply = 26;
        private const int LineStatusRemove = 30;

        // 21|ts|sourceId|sourceName|abilityId|abilityName|targetId|targetName|...
        private const int AbilityFieldSourceId = 2;
        private const int AbilityFieldSourceName = 3;
        private const int AbilityFieldActionId = 4;
        private const int AbilityMinFields = 6;

        // 20|timestamp|sourceId|sourceName|actionId|actionName|targetId|targetName|castTime(s)|x|y|z|heading
        private const int CastFieldDuration = 8;

        /// <summary>
        /// A cast that started but has not landed yet, per player.
        ///
        /// Pairing the cast bar with the effect answers two things at once. Whether the press was
        /// instant - Dualcast, Swiftcast and every proc make the base cast time in the data wrong
        /// for that particular press, and for a Red Mage that is half the rotation. And *when the
        /// GCD actually started*, which is when the button went down, not when the damage landed.
        /// </summary>
        private struct PendingCast
        {
            public uint ActionId;
            public DateTime StartedAt;

            /// <summary>How long the bar ran, from the cast-start line; 0 when it did not say.</summary>
            public double CastMs;
        }

        private readonly Dictionary<string, PendingCast> casting =
            new Dictionary<string, PendingCast>(StringComparer.Ordinal);

        /// <summary>
        /// A cast bar older than this never produced an effect and was missed by the cancel line.
        /// Longer than the longest cast in the game so a slow Verraise still pairs.
        /// </summary>
        private static readonly TimeSpan MaxCastAge = TimeSpan.FromSeconds(15);

        private readonly object gate = new object();
        private readonly Instrumentation diag = new Instrumentation();
        private readonly StatusTracker statuses = new StatusTracker();
        private GcdTracker gcds;
        private ActionCategories categories;
        private ActionData actionData;

        /// <summary>The encounter the tracker currently describes. A reference change means "reset".</summary>
        private EncounterData currentEncounter;

        private bool hooked;
        private string diagnosticDir;

        public GcdEventSource(TinyIoCContainer container) : base(container)
        {
            Name = "GcdOverlayES";

            RegisterCachedEventTypes(new List<string> { GcdUpdateEvent });

            RegisterEventHandler("getGcdData", _ => BuildSnapshot());

            // Every counter between "ACT read a line" and "a number reached the overlay", plus a
            // few raw lines. The first thing to run when the columns read zero.
            RegisterEventHandler("dumpGcdDiagnostics", _ =>
            {
                var path = WriteDiagnosticsDump();
                return new JObject { ["path"] = path ?? "", ["written"] = path != null };
            });
        }

        public override Control CreateConfigControl() => new UserControl();
        public override void LoadConfig(IPluginConfig config) { }
        public override void SaveConfig(IPluginConfig config) { }

        public override void Start()
        {
            TryLoadCategories();

            actionData = ActionData.Load(ResolveDataPath("actions.json"));
            if (actionData.LoadError != null)
                Log(LogLevel.Warning, "actions.json failed to load ({0}). Every GCD will be treated as a plain 2.5s recast.", actionData.LoadError);
            else
                Log(LogLevel.Info, "actions.json loaded: {0} recast overrides, {1} haste statuses.",
                    actionData.ActionCount, actionData.SpeedStatusCount);

            gcds = new GcdTracker(actionData);

            if (!hooked)
            {
                // Hung off ACT's log-reading loop rather than FFXIVRepository.RegisterLogLineHandler:
                // that is the single thread that reads lines in log order, so a haste status
                // applied on the same tick as a press is on the player before the press is booked.
                ActGlobals.oFormActMain.BeforeLogLineRead += OnLogLine;
                ActGlobals.oFormActMain.OnCombatStart += OnCombatStart;
                ActGlobals.oFormActMain.OnCombatEnd += OnCombatEnd;
                hooked = true;
            }

            diag.EventSourceStarted = true;

            base.Start();
            Log(LogLevel.Info, "GcdEventSource started. Diagnostics are written to {0} after every encounter.",
                DiagnosticPath("OverlayPluginAddon.diagnostics.txt") ?? "(path unavailable)");
        }

        public override void Stop()
        {
            if (hooked)
            {
                ActGlobals.oFormActMain.BeforeLogLineRead -= OnLogLine;
                ActGlobals.oFormActMain.OnCombatStart -= OnCombatStart;
                ActGlobals.oFormActMain.OnCombatEnd -= OnCombatEnd;
                hooked = false;
            }

            base.Stop();
            Log(LogLevel.Info, "GcdEventSource stopped.");
        }

        // ------------------------------------------------------------------ log line intake

        private void OnLogLine(bool isImport, LogLineEventArgs args)
        {
            // originalLogLine, not logLine: FFXIV_ACT_Plugin's own BeforeLogLineRead handler
            // overwrites logLine with its reformatted output, and ours runs after it.
            var line = args.originalLogLine;
            if (string.IsNullOrEmpty(line))
            {
                diag.LinesWithoutOriginal++;
                line = args.logLine;
            }
            if (string.IsNullOrEmpty(line)) return;

            // Cheap prefix check before paying for a Split on every single log line.
            var bar = line.IndexOf('|');
            if (bar < 1 || bar > 3) return;
            if (!int.TryParse(line.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
                return;

            diag.CountLine(type, line);

            switch (type)
            {
                case LineStartsCasting:
                    // 20|timestamp|sourceId|sourceName|actionId|actionName|...
                    HandleCastStart(line.Split('|'), args.detectedTime);
                    break;

                case LineCancelCast:
                    // 23|timestamp|sourceId|sourceName|actionId|... - the cast bar was interrupted,
                    // so the GCD provisionally recorded when it started never happened.
                    HandleCastCancel(line.Split('|'));
                    break;

                case LineAbility:
                case LineAoeAbility:
                    HandleAbilityLine(line, args.detectedTime);
                    break;

                case LineStatusApply:
                case LineStatusRemove:
                    lock (gate) statuses.HandleStatusLine(type == LineStatusApply, line.Split('|'));
                    break;

                case LineDeath:
                    // 25|timestamp|targetId|targetName|sourceId|sourceName - death wipes every status.
                    var death = line.Split('|');
                    if (death.Length > 3) lock (gate) statuses.RemoveActor(death[3]);
                    break;

                case LinePrimaryPlayer:
                    // Tells us the logging player's real name, which ACT hides behind "YOU".
                    lock (gate) statuses.HandlePrimaryPlayer(line.Split('|'));
                    break;

                case LineAddCombatant:
                    // Names actors authoritatively, which is how we learn who is a player.
                    lock (gate) statuses.HandleAddCombatant(line.Split('|'));
                    break;

                case LineRemoveCombatant:
                    // 04|timestamp|actorId|actorName|...
                    var gone = line.Split('|');
                    if (gone.Length > 3) lock (gate) statuses.RemoveActor(gone[3]);
                    break;

                case LineChangeZone:
                    lock (gate) statuses.Clear();
                    break;
            }
        }

        private void HandleCastStart(string[] f, DateTime when)
        {
            diag.CastStartsSeen++;
            if (f.Length <= AbilityFieldActionId) return;
            if (!uint.TryParse(f[AbilityFieldActionId], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actionId))
                return;

            var source = f[AbilityFieldSourceName];
            if (string.IsNullOrEmpty(source)) return;

            // The client states how long this bar will run, with speed, haste and job mechanics
            // already applied. The table only knows the unmodified value.
            var castMs = 0.0;
            if (f.Length > CastFieldDuration
                && double.TryParse(f[CastFieldDuration], NumberStyles.Float, CultureInfo.InvariantCulture, out var castSeconds)
                && castSeconds > 0)
                castMs = castSeconds * 1000.0;

            lock (gate)
            {
                casting[source] = new PendingCast { ActionId = actionId, StartedAt = when, CastMs = castMs };

                // Recorded now rather than when the damage lands. The GCD is already turning while
                // the cast bar fills, so waiting for the effect leaves uptime sagging for the whole
                // cast and snapping back afterwards. When the effect does arrive it carries this
                // same timestamp and is dropped as a duplicate.
                if (gcds != null && categories != null && categories.Available && categories.IsGcd(actionId)
                    && statuses.IsPlayer(source))
                {
                    SyncEncounter();
                    gcds.Record(source, actionId, when, SpeedModifierOn(source, actionId),
                                hardCast: true, isSpell: categories.IsSpell(actionId), actualCastMs: castMs);
                    diag.GcdsRecorded++;
                    diag.HardCastsRecorded++;
                }
            }
        }

        private void HandleCastCancel(string[] f)
        {
            diag.CastCancelsSeen++;
            if (f.Length <= AbilityFieldActionId) return;

            var source = f[AbilityFieldSourceName];
            if (string.IsNullOrEmpty(source)) return;

            lock (gate)
            {
                if (!casting.TryGetValue(source, out var pending)) return;
                casting.Remove(source);

                gcds?.RemoveLast(source, pending.ActionId);
                if (diag.GcdsRecorded > 0) diag.GcdsRecorded--;
                if (diag.HardCastsRecorded > 0) diag.HardCastsRecorded--;
            }
        }

        /// <summary>
        /// Counts a player's GCDs. Driven off the ability line rather than off damage, so that
        /// GCDs which heal or only apply a buff still count - a healer's uptime would be nonsense
        /// otherwise.
        /// </summary>
        private void HandleAbilityLine(string line, DateTime when)
        {
            diag.AbilityLinesSeen++;

            var f = line.Split('|');
            if (f.Length < AbilityMinFields) { diag.AbilityLinesTooShort++; return; }

            var source = f[AbilityFieldSourceName];

            // Ability lines are how we learn who the players are. They fire constantly, unlike
            // AddCombatant, which only turns up on a zone change.
            lock (gate) statuses.NotePlayer(f[AbilityFieldSourceId], source);

            if (gcds == null || categories == null || !categories.Available) return;

            // Only players have a GCD worth measuring; pets and mobs are noise here.
            if (string.IsNullOrEmpty(source)) return;
            if (string.IsNullOrEmpty(f[AbilityFieldSourceId]) || f[AbilityFieldSourceId][0] != '1')
            {
                diag.AbilityLinesNonPlayerSource++;
                return;
            }

            if (!uint.TryParse(f[AbilityFieldActionId], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actionId))
            {
                diag.AbilityLinesBadActionId++;
                return;
            }

            if (!categories.IsGcd(actionId)) { diag.AbilityLinesNotGcd++; return; }

            lock (gate)
            {
                // Landed with a matching cast bar behind it, rather than fired instantly.
                var hardCast = casting.TryGetValue(source, out var pending)
                    && pending.ActionId == actionId
                    && when - pending.StartedAt <= MaxCastAge;

                if (hardCast)
                {
                    // The GCD started turning when the button went down. Timing a caster off the
                    // moment their damage lands mixes cast time into every interval: an instant
                    // following a 2.5s cast looks like a zero-length GCD, and the cast before it
                    // looks like a double-length one.
                    when = pending.StartedAt;
                    casting.Remove(source);
                }

                // The cast bar's length travels with the pending cast, so a hard cast that gets
                // recorded here - the cast-start line fell before the encounter began and was reset
                // away with it - is still measured off the bar rather than the table.
                SyncEncounter();
                gcds.Record(source, actionId, when, SpeedModifierOn(source, actionId),
                            hardCast, categories.IsSpell(actionId), actualCastMs: hardCast ? pending.CastMs : 0);
                diag.GcdsRecorded++;
                if (hardCast) diag.HardCastsRecorded++;
            }
        }

        /// <summary>
        /// Product of the haste statuses on an actor that apply to the action being pressed. Read
        /// at cast time because the status tracker only holds the present, and the GCD that matters
        /// is the one in force when the button was pressed. Per action, not per actor: Inspiration
        /// quickens a Pictomancer's aetherhue spells and not the hammers pressed beside them.
        /// </summary>
        private double SpeedModifierOn(string actor, uint actionId)
        {
            if (actionData == null) return 1.0;

            var modifier = 1.0;
            foreach (var status in statuses.ActiveOn(actor))
                modifier *= actionData.SpeedModifierOf(status.StatusId, actionId);

            return modifier;
        }

        /// <summary>
        /// Drops the tracker when ACT moved on to a different encounter. Keyed on the EncounterData
        /// reference rather than on start time, so Clear / re-parse / a new pull all reset cleanly.
        /// Caller must hold <see cref="gate"/>.
        /// </summary>
        private void SyncEncounter()
        {
            var active = ActGlobals.oFormActMain.ActiveZone?.ActiveEncounter;
            if (ReferenceEquals(active, currentEncounter)) return;

            currentEncounter = active;
            gcds?.Clear();
            casting.Clear();
            // Statuses deliberately survive: a haste status applied a second before the pull
            // started is still up during the opener.
        }

        private void OnCombatStart(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            lock (gate)
            {
                currentEncounter = ActGlobals.oFormActMain.ActiveZone?.ActiveEncounter;
                gcds?.Clear();
            }
        }

        private void OnCombatEnd(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            WriteStatusLog();

            // Written unprompted rather than only on request: reaching the manual handler means
            // opening a CEF devtools console, which is a lot to ask of someone whose columns are
            // all reading zero. Rewritten each time, so it cannot pile up.
            WriteDiagnosticsDump();
        }

        // ------------------------------------------------------------------ ACT export columns

        /// <summary>
        /// Publishes the GCD columns as ACT export variables.
        ///
        /// OverlayPlugin's MiniParse event source walks CombatantData.ExportVariables wholesale when
        /// it builds each CombatData payload, so anything registered here shows up in every overlay
        /// (mopimopi included) with no further plumbing. Registered once, globally - ACT keeps the
        /// dictionary for the life of the process. A key another addon already registered is left
        /// to it.
        /// </summary>
        public void RegisterExportVariables()
        {
            diag.ExportVariablesRegistered = true;

            AddGcd("gcdUptime", "GCD Uptime %", "Share of the time from this player's first GCD to their latest one spent rolling GCDs, as a percentage. Measured at each GCD.",
                (g) => g.Uptime * 100.0);

            AddGcd("gcdCount", "GCDs", "Number of weaponskills and spells cast. Excludes oGCD abilities and auto-attacks.",
                (g) => g.Count);

            AddGcd("gcdClip", "GCD Lost", "Seconds lost to gaps between GCDs longer than this player's own recast.",
                (g) => g.Clip);

            AddGcd("gcdOccupied", "GCD seconds", "Seconds occupied by this player's completed GCDs. The numerator behind gcdUptime; its denominator is the span of their own presses, not the encounter duration.",
                (g) => g.OccupiedSeconds);

            AddGcd("gcdRecast", "GCD", "This player's plain 2.5s-base recast, from the speed stat inferred for them.",
                (g) => g.Recast);
        }

        /// <summary>
        /// GCD columns are not per-second rates: gcdUptime is already a percentage and the others
        /// are counts and seconds, so nothing here is divided by the encounter duration.
        /// </summary>
        private void AddGcd(string key, string label, string description, Func<GcdStats, double> compute)
        {
            if (CombatantData.ExportVariables.ContainsKey(key)) return;

            CombatantData.ExportVariables.Add(key, new CombatantData.TextExportFormatter(
                key, label, description,
                (data, extraFormat) =>
                {
                    try
                    {
                        var name = data?.Name;
                        if (string.IsNullOrEmpty(name)) return "0";

                        // Called from OverlayPlugin's Parallel.ForEach over allies, so this runs
                        // concurrently with the log-read thread still recording presses.
                        lock (gate)
                        {
                            if (gcds == null) return "0";
                            return compute(gcds.StatsFor(statuses.Resolve(name)))
                                .ToString("0.##", CultureInfo.InvariantCulture);
                        }
                    }
                    catch (Exception)
                    {
                        // Never let a bad column take the whole CombatData payload down with it.
                        return "0";
                    }
                }));
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>
        /// Loads the action category table, and says so once either way.
        ///
        /// Retried from the timer rather than settled at startup: the parser embeds its resource
        /// assembly with Costura and only unpacks it when something needs it, so an attempt made
        /// while ACT is still booting can legitimately come up empty and succeed a moment later.
        /// Giving up at Start() left GCD uptime silently stuck at zero for the whole session.
        /// </summary>
        private void TryLoadCategories()
        {
            var loaded = ActionCategories.Load();
            if (!loaded.Available && categories != null && !categories.Available)
            {
                // Still not there. Keep the original failure and stay quiet until it works.
                return;
            }

            categories = loaded;

            if (categories.Available)
                Log(LogLevel.Info, "Action categories loaded: {0} GCD actions (from FFXIV_ACT_Plugin.Resource).", categories.Count);
            else
                Log(LogLevel.Warning, "GCD uptime unavailable for now: {0}", categories.LoadError);
        }

        protected override void Update()
        {
            // Cheap while it is working (a field read), and the only thing that recovers a startup
            // race with the parser's lazily unpacked resources.
            if (categories == null || !categories.Available) TryLoadCategories();

            DispatchAndCacheEvent(new JObject
            {
                ["type"] = GcdUpdateEvent,
                ["detail"] = BuildSnapshot(),
            });
        }

        private JObject BuildSnapshot()
        {
            var players = new JObject();

            lock (gate)
            {
                if (gcds != null)
                {
                    foreach (var player in gcds.Players)
                    {
                        var gcd = gcds.StatsFor(player);
                        players[player] = new JObject
                        {
                            ["gcdUptime"] = Math.Round(gcd.Uptime * 100, 1),
                            ["gcdCount"] = gcd.Count,
                            ["gcdClip"] = Math.Round(gcd.Clip, 1),
                            ["gcdOccupied"] = Math.Round(gcd.OccupiedSeconds, 1),
                            ["gcdRecast"] = Math.Round(gcd.Recast, 2),
                            ["speedStat"] = gcd.SpeedStat,
                            ["recastEstimated"] = gcd.RecastEstimated,
                            ["skillSpeedSamples"] = gcd.SkillSpeedSamples,
                            ["spellSpeedSamples"] = gcd.SpellSpeedSamples,
                        };
                    }
                }

                return new JObject
                {
                    ["duration"] = Math.Round(currentEncounter?.Duration.TotalSeconds ?? 0.0, 1),
                    ["players"] = players,
                    ["gcdActionsKnown"] = categories?.Count ?? 0,
                    ["actionRecastsKnown"] = actionData?.ActionCount ?? 0,
                    ["hasteStatusesKnown"] = actionData?.SpeedStatusCount ?? 0,
                };
            }
        }

        /// <summary>
        /// One block per encounter: each player's numbers and, underneath, one line per GCD with
        /// what it was, when, what recast it was charged, how long until the next one, and what
        /// that turned into. This is the view that settles "the GCD logic is wrong" without another
        /// round of guessing.
        /// </summary>
        private void WriteStatusLog()
        {
            try
            {
                var path = DiagnosticPath("OverlayPluginAddon.status.log");
                if (path == null) return;

                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Delete(path);

                var sb = new StringBuilder();
                lock (gate)
                {
                    var duration = currentEncounter?.Duration.TotalSeconds ?? 0.0;

                    sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss}  {1}  dur={2:0.0}s  gcdsRecorded={3} hardCasts={4}",
                        DateTime.Now, currentEncounter?.Title ?? "(no encounter)", duration,
                        diag.GcdsRecorded, diag.HardCastsRecorded);
                    sb.AppendLine();

                    if (gcds != null)
                    {
                        foreach (var player in gcds.Players)
                        {
                            var gcd = gcds.StatsFor(player, includeTrace: true);
                            if (gcd.Count == 0) continue;

                            sb.AppendFormat("    {0,-24} gcd: {1} casts, uptime {2:0.0}%, lost {3:0.0}s, recast {4:0.00}s (speed stat {5}, {6} skill / {7} spell samples){8}",
                                player, gcd.Count, gcd.Uptime * 100, gcd.Clip, gcd.Recast, gcd.SpeedStat,
                                gcd.SkillSpeedSamples, gcd.SpellSpeedSamples,
                                gcd.RecastEstimated ? "" : " - default, too few casts to measure");
                            sb.AppendLine();

                            if (gcd.Trace != null)
                            {
                                var first = gcd.Trace.Count > 0 ? gcd.Trace[0].TimeMs : 0;
                                sb.AppendLine("        t(s)    action  cast  recast   gap   occupied  lost");
                                foreach (var c in gcd.Trace)
                                    sb.AppendFormat("        {0,6:0.00}  {1,6:X}  {2,-4}  {3,6:0.00}  {4,6:0.00}  {5,8:0.00}  {6,5:0.00}{7}",
                                        (c.TimeMs - first) / 1000.0, c.ActionId, c.HardCast ? "hard" : "inst",
                                        c.RecastMs / 1000.0, c.GapMs / 1000.0, c.OccupiedMs / 1000.0, c.LostMs / 1000.0,
                                        Environment.NewLine);
                            }
                        }
                    }
                }

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                // Diagnostics must never break the overlay.
            }
        }

        /// <summary>
        /// Dumps every counter between "ACT read a line" and "a number reached the overlay", plus
        /// a few raw log lines. This is the first thing to run when the columns read zero.
        /// </summary>
        private string WriteDiagnosticsDump()
        {
            try
            {
                var path = DiagnosticPath("OverlayPluginAddon.diagnostics.txt");
                if (path == null) return null;

                var sb = new StringBuilder();
                lock (gate) diag.Describe(sb, statuses, categories, actionData, gcds);

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Log(LogLevel.Info, "Diagnostics written to {0}", path);
                return path;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "Diagnostics dump failed: {0}", ex.Message);
                return null;
            }
        }

        private static string ResolveDataPath(string fileName) => GcdOverlayAddon.ResolveDataPath(fileName);

        private string DiagnosticPath(string fileName)
        {
            if (diagnosticDir == null)
            {
                diagnosticDir = GcdOverlayAddon.PluginDirectory() ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Advanced Combat Tracker", "Config");
            }

            return diagnosticDir == null ? null : Path.Combine(diagnosticDir, fileName);
        }
    }
}
