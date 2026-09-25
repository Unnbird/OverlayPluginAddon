using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin;
using OverlayPluginAddon.Fflogs;

namespace OverlayPluginAddon
{
    /// <summary>
    /// The one place every number is worked out.
    ///
    /// Two measurements run off the same log lines. GCD uptime is measured here, on ACT's
    /// log-reading loop, to xivanalysis' model (see <see cref="GcdTracker"/>). The rDPS family
    /// comes from FFLogs' own parser, hosted in <see cref="ParserHost"/> and read back through
    /// <see cref="MeterSnapshot"/>. Both are published as ACT export variables, so any overlay that
    /// reads CombatData - mopimopi included - gets the columns with no further plumbing and no
    /// second copy of the arithmetic.
    /// </summary>
    public class AddonEventSource : EventSourceBase
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

        /// <summary>The ACT encounter the tracker currently describes. Only consulted when the
        /// parser has no fight of its own to go on - see <see cref="SyncEncounter"/>.</summary>
        private EncounterData currentEncounter;

        /// <summary>What counts as a new pull. See <see cref="PullBoundary"/>.</summary>
        private readonly PullBoundary pull = new PullBoundary();

        private bool hooked;
        private string diagnosticDir;

        /// <summary>FFLogs' parser, on a thread of its own. Null when it failed to start.</summary>
        private ParserHost parser;

        /// <summary>
        /// Turns each collect into the table the export formatters read. Lives outside this class
        /// so a replay of a saved log can drive the same chain without ACT.
        /// </summary>
        private volatile MeterPipeline pipeline;

        /// <summary>What the export formatters read. Empty until the parser has said something.</summary>
        private MeterSnapshot meters => pipeline?.Current ?? MeterSnapshot.Empty;

        public AddonEventSource(TinyIoCContainer container) : base(container)
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
                Log(LogLevel.Info, "actions.json loaded: {0} recast overrides, {1} haste statuses, {2} onGcd ids.",
                    actionData.ActionCount, actionData.SpeedStatusCount, actionData.OnGcdCount);

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

            StartParser();

            diag.EventSourceStarted = true;

            base.Start();
            Log(LogLevel.Info, "AddonEventSource started. Diagnostics are written to {0} after every encounter.",
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

            if (parser != null)
            {
                if (pipeline != null) parser.Collected -= pipeline.Accept;
                parser.Dispose();
                parser = null;
            }
            pipeline?.Reset();
            pipeline = null;
            lock (gate) pull.Reset();

            base.Stop();
            Log(LogLevel.Info, "AddonEventSource stopped.");
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

            // The parser wants the whole log, not the handful of line types the GCD half reads, and
            // it wants them in order. Feed() only appends to a list; all the work happens on the
            // parser's own thread, so ACT's log loop is never held up by it.
            parser?.Feed(line);

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
                    // No fight survives a zone change, so ACT decides again until the parser opens
                    // one - which in content it has no handler for is for the rest of the session.
                    lock (gate) { statuses.Clear(); pull.Reset(); }
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
                if (gcds != null && categories != null && categories.Available && IsGcdAction(actionId)
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

            if (!IsGcdAction(actionId)) { diag.AbilityLinesNotGcd++; return; }

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
        /// Whether the action rolls the GCD.
        ///
        /// xivanalysis' own onGcd flag decides first. FFXIV files a Ninja's mudras and every
        /// Ninjutsu under ActionCategory 4, "Ability" - correctly, they are not weaponskills - yet
        /// Ten, Chi, Raiton are 0.5s + 0.5s + 1.5s of GCD, and asking the category alone left that
        /// whole stretch reading as lost time, several points off every Ninja's uptime. Monk's
        /// meditations and Samurai's Meditate are the same case; a scan of every job found no
        /// action the category calls a GCD that xivanalysis says is not. The category table, read
        /// live out of the parser, decides for everything xivanalysis does not list - which is how
        /// an action added by a patch still counts before actions.json has been regenerated.
        /// </summary>
        private bool IsGcdAction(uint actionId) =>
            (actionData != null && actionData.IsOnGcd(actionId)) || categories.IsGcd(actionId);

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
        /// Drops the tracker when the pull changed, for the case where the parser cannot say when
        /// that was: content it has no handler for, or a parser that failed to start. Keyed on the
        /// EncounterData reference rather than on start time, so Clear / re-parse / a new pull all
        /// reset cleanly. Caller must hold <see cref="gate"/>.
        ///
        /// While the parser is supplying fights it decides instead (see OnSnapshotPublished), and
        /// ACT ending an encounter at a phase transition changes nothing here.
        /// </summary>
        private void SyncEncounter()
        {
            EncounterData active = null;
            try { active = ActGlobals.oFormActMain.ActiveZone?.ActiveEncounter; }
            catch (Exception) { /* ACT is between zones */ }
            if (ReferenceEquals(active, currentEncounter)) return;

            currentEncounter = active;
            if (!pull.NoteEncounterChanged()) return;

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
                // Same rule as SyncEncounter: ACT opening an encounter is not by itself a new pull.
                // In M8S it is the second half of one.
                if (pull.NoteEncounterChanged()) gcds?.Clear();
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

        // ------------------------------------------------------------------ FFLogs parser

        /// <summary>
        /// Brings FFLogs' parser up. A failure here is not fatal: the GCD columns keep working and
        /// the rDPS ones stay empty, which is what an overlay sees when the addon is not installed
        /// at all.
        /// </summary>
        private void StartParser()
        {
            pipeline = new MeterPipeline(MatchesEncounter, message => Log(LogLevel.Debug, "{0}", message));
            pipeline.Published += OnSnapshotPublished;

            var path = ResolveDataPath("parser-ff.js");
            parser = new ParserHost(path, OnParserLog, OverlayAddon.PluginDirectory());
            parser.Collected += pipeline.Accept;

            // The parser ignores lines older than this. Live, that is now: ACT replays the tail of
            // the current log file on startup and those lines belong to a pull that is already over.
            if (parser.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DetectRegion())) return;

            Log(LogLevel.Warning, "FFLogs parser did not start ({0}). rDPS columns will stay empty; the GCD columns are unaffected.", parser.LastError);
            if (parser.LastError.IndexOf("ClearScript", StringComparison.OrdinalIgnoreCase) >= 0)
                Log(LogLevel.Error, "ClearScript could not be loaded. ClearScript.Core.dll, ClearScript.V8.dll, ClearScriptV8.win-x64.dll and ClearScript.V8.ICUData.dll all have to sit in the same folder as OverlayPluginAddon.dll - extract the whole release archive rather than just the dll.");
            parser.Collected -= pipeline.Accept;
            parser.Dispose();
            parser = null;
        }

        /// <summary>
        /// Which FFLogs region the parser should read lines as. Detected rather than configured:
        /// ACT already knows which client it is watching, and asking the user to pick again is one
        /// more thing to get wrong.
        ///
        /// The numbers are FFLogs' own region ids, which are not Machina's - hence the mapping.
        /// Anything unrecognised reads as Global, which is what a misconfigured overlay defaulted
        /// to before.
        /// </summary>
        private int DetectRegion()
        {
            try
            {
                var region = container.Resolve<FFXIVRepository>().GetMachinaRegion();
                switch (region)
                {
                    case GameRegion.Chinese: return 5;
                    case GameRegion.Korean: return 4;
                    default:
                        if (region != GameRegion.Global)
                            Log(LogLevel.Info, "Game region {0} has no FFLogs region of its own; reading logs as Global.", region);
                        return 1;
                }
            }
            catch (Exception e)
            {
                Log(LogLevel.Warning, "Could not detect the game region ({0}); reading logs as Global.", e.Message);
                return 1;
            }
        }

        private void OnParserLog(string level, string message)
        {
            switch (level)
            {
                case "error": Log(LogLevel.Error, "{0}", message); break;
                case "warn": Log(LogLevel.Warning, "{0}", message); break;
                default: Log(LogLevel.Debug, "{0}", message); break;
            }
        }

        /// <summary>
        /// The GCD half needs three things from each snapshot.
        ///
        /// The downtime windows, or M8S' minute-long transition reads as a minute of clipping for
        /// everyone. The fight's end, once the parser has closed it: the record is not reset until
        /// the next fight opens, and a Monk meditating after the wipe is not part of the pull. And
        /// the fight's identity: a new fight is a new pull and the tracker starts over, which is
        /// the only reset that happens while the parser is supplying fights.
        /// </summary>
        private void OnSnapshotPublished(MeterSnapshot snapshot)
        {
            lock (gate)
            {
                gcds?.SetDowntimeWindows(snapshot.Downtime);
                gcds?.SetFightEnd(snapshot.FightEnded ? snapshot.FightEndMs : (double?)null);

                if (!pull.NoteFight(snapshot.FightId)) return;

                gcds?.Clear();
                casting.Clear();
                // Whatever ACT calls the encounter right now is this fight's, so SyncEncounter does
                // not read the change as a second reset.
                try { currentEncounter = ActGlobals.oFormActMain.ActiveZone?.ActiveEncounter; }
                catch (Exception) { currentEncounter = null; }
            }
        }

        /// <summary>
        /// Whether the parser's fight and ACT's encounter are the same pull.
        ///
        /// Either rule is enough. The durations agree on an ordinary pull, and that test needs no
        /// clock alignment at all. The start-time test covers what the duration test cannot see:
        /// ACT ends an encounter whenever combat drops for its idle timeout, and a scripted phase
        /// transition is exactly that, so in M8S ACT opens a second encounter at the transition
        /// while FFLogs keeps the pull as one fight and the two durations never agree again.
        /// </summary>
        private bool MatchesEncounter(FightFigures fight, out string reason)
        {
            if (fight == null || fight.Damage.Count == 0)
            {
                reason = "no fight yet";
                return false;
            }

            EncounterData encounter = null;
            try { encounter = ActGlobals.oFormActMain.ActiveZone?.ActiveEncounter; }
            catch (Exception) { /* ACT is between zones */ }

            if (encounter == null)
            {
                reason = "no encounter";
                return false;
            }

            if (FightMatch.DurationsMatch(encounter.Duration.TotalSeconds, fight.DurationSeconds))
            {
                reason = "durations match";
                return true;
            }

            var startMs = double.NaN;
            try { startMs = new DateTimeOffset(encounter.StartTime).ToUnixTimeMilliseconds(); }
            catch (Exception) { /* an encounter with no usable start time */ }

            if (FightMatch.EncounterWithinFight(startMs, fight.StartTimeMs, fight.EndTimeMs, fight.InProgress))
            {
                reason = "encounter began inside the fight";
                return true;
            }

            reason = "fight does not match the encounter";
            return false;
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

            RegisterFflogsColumns();
        }

        /// <summary>
        /// The FFLogs columns, in two groups.
        ///
        /// The first keeps the names the overlays already know - rdps, adps and the rest - because
        /// ACT has no column of its own by those names, so an overlay that used to get them from
        /// the retired RdpsOverlay keeps working unchanged.
        ///
        /// The second is prefixed, because ACT owns damage, healed, maxhit and the rest, and
        /// ExportVariables can only add keys, never replace them. An overlay that wants FFLogs'
        /// table rather than ACT's reads the prefixed column and falls back to ACT's when it is
        /// empty - which is exactly what empty means here: FFLogs has nothing for this row.
        ///
        /// Every row FFLogs does know reads as a figure, including zero. The distinction matters
        /// for pets: one the parser folded into its owner reads zero, not empty, so an overlay that
        /// sums pet rows into the owner cannot count it twice.
        /// </summary>
        private void RegisterFflogsColumns()
        {
            AddFflogs("rdps", "rDPS", "Damage per second with raid buffs handed back to whoever cast them. Divided by the fight minus its downtime.", f => Rate(f.Rdps));
            AddFflogs("adps", "aDPS", "Damage per second with single-target buffs taken off the receiver but left with the giver.", f => Rate(f.Adps));
            AddFflogs("ndps", "nDPS", "Damage per second with every buff received taken off and nothing credited back.", f => Rate(f.Ndps));
            AddFflogs("cdps", "cDPS", "Damage per second keeping raid buffs received and crediting buffs given.", f => Rate(f.Cdps));
            AddFflogs("rdpsDelta", "rDPS delta", "rDPS minus this player's own damage per second: what supporting the raid was worth, less what the raid gave them.", f => Rate(f.RdpsDelta));
            AddFflogs("rdpsPct", "rDPS %", "This player's share of the raid's rDPS, as a percentage.", f => Rate(f.RdpsPct));

            // Damage and healing per second, divided here rather than left for the overlay.
            //
            // The rDPS family above was always divided here, and that is the only reason a solo
            // pull used to disagree with itself: rDPS came off the exact clock while DPS was the
            // overlay dividing a rounded damage by a clock it had re-read out of a string. Same
            // shape as fflogsDamage - own, with the kept-apart pets carrying their own share on
            // their own rows - so an overlay that sums pets into the owner still adds up.
            AddFflogs("fflogsDps", "DPS (FFLogs)", "Damage per second over the fight minus its downtime. Already divided; show it as it came. Excludes pets FFLogs keeps as rows of their own, which carry theirs.", f => Rate(f.Dps));
            // Empty whenever FFLogs has no healing table for this fight, which is nearly always:
            // see PlayerFigures.HasHealing. Reporting the zero instead let it overwrite the only
            // healing anyone has.
            AddFflogs("fflogsHps", "HPS (FFLogs)", "Healing per second over the whole fight, overheal included the way ACT counts it. Already divided; show it as it came.",
                f => f.HasHealing ? Rate(f.Hps) : "");

            AddFflogs("fflogsDamage", "Damage (FFLogs)", "Damage as FFLogs' parser books it, excluding pets it keeps as rows of their own.", f => Whole(f.Damage));
            AddFflogs("fflogsHits", "Hits (FFLogs)", "Hit count as FFLogs' parser books it.", f => Whole(f.Hits.HitCount));
            AddFflogs("fflogsCrithits", "Crits (FFLogs)", "Critical hit count as FFLogs' parser books it.", f => Whole(f.Hits.CriticalCount));
            AddFflogs("fflogsDirectHitCount", "Direct hits (FFLogs)", "Direct hit count as FFLogs' parser books it.", f => Whole(f.Hits.DirectHitCount));
            AddFflogs("fflogsCritDirectHitCount", "Critical direct hits (FFLogs)", "Critical direct hit count as FFLogs' parser books it.", f => Whole(f.Hits.CriticalDirectHitCount));
            AddFflogs("fflogsMaxhit", "Biggest hit (FFLogs)", "Biggest single hit and the ability that dealt it, as ACT spells it: Ability-12345.", MaxHitText);
            AddFflogs("fflogsMAXHIT", "Biggest hit value (FFLogs)", "Biggest single hit, the bare number.", f => Whole(f.Hits.MaxHit));

            AddFflogs("fflogsHealed", "Healing (FFLogs)", "Healing including overheal, the way ACT counts it. FFLogs keeps the two apart; this adds them back. Empty when this build of the parser measured no player healing at all.",
                f => f.HasHealing ? Whole(f.Healed) : "");
            AddFflogs("fflogsOverHeal", "Overheal (FFLogs)", "Healing that landed on full health.", f => f.HasHealing ? Whole(f.OverHeal) : "");
            AddFflogs("fflogsHeals", "Heal count (FFLogs)", "Number of healing hits.", f => f.HasHealing ? Whole(f.HealHits.HitCount) : "");
            AddFflogs("fflogsCritheals", "Crit heals (FFLogs)", "Number of critical healing hits.", f => f.HasHealing ? Whole(f.HealHits.CriticalCount) : "");
            AddFflogs("fflogsMaxheal", "Biggest heal (FFLogs)", "Biggest single heal and the ability that cast it.", MaxHealText);
            AddFflogs("fflogsMAXHEAL", "Biggest heal value (FFLogs)", "Biggest single heal, the bare number.", f => f.HasHealing ? Whole(f.HealHits.MaxHit) : "");

            AddFflogs("fflogsDeaths", "Deaths (FFLogs)", "Deaths as FFLogs' parser counts them.", f => Whole(f.Deaths));

            // Two clocks, and they are not the same one. FFLogs divides damage by the fight minus
            // its downtime and healing by the whole fight; an overlay that divides both by one
            // clock disagrees with the report it is mirroring.
            AddFflogs("fflogsDuration", "Duration (FFLogs)", "Seconds the damage columns are divided by: the fight minus the stretches when nothing could be hit.", _ => Clock(meters.Clocks.Active));
            // The clock, not a healing figure: it is the fight's whether or not FFLogs measured any
            // healing, and ACT's healing divided by it is still a rate over this pull.
            AddFflogs("fflogsHealDuration", "Heal duration (FFLogs)", "Seconds the healing columns are divided by: the whole fight, downtime included.", _ => Clock(meters.Clocks.Seconds));

            AddEncounter("fflogsDamage", "Damage (FFLogs)", "The raid's damage as FFLogs' parser books it.", m => Whole(m.EncounterDamage));
            AddEncounter("fflogsHealed", "Healing (FFLogs)", "The raid's healing including overheal. Empty when the parser measured no player healing.",
                m => m.HasHealing ? Whole(m.EncounterHealed) : "");
            AddEncounter("fflogsRdps", "rDPS total (FFLogs)", "The raid's rDPS numerator, which every row's rDPS % is a share of.", m => Whole(m.EncounterRdpsAmount));
            // The overlay prints these as the header totals. Divided here, against the same clocks
            // the rows were, so the header cannot describe a different pull from the table under it.
            AddEncounter("fflogsEncdps", "Raid DPS (FFLogs)", "The raid's damage per second, over the fight minus its downtime.",
                m => Whole(m.EncounterDamage / (m.Clocks.Active > 0 ? m.Clocks.Active : 1)));
            AddEncounter("fflogsEnchps", "Raid HPS (FFLogs)", "The raid's healing per second, over the whole fight.",
                m => m.HasHealing ? Whole(m.EncounterHealed / (m.Clocks.Seconds > 0 ? m.Clocks.Seconds : 1)) : "");
            AddEncounter("fflogsDuration", "Duration (FFLogs)", "Seconds the damage columns are divided by.", m => Clock(m.Clocks.Active));
            AddEncounter("fflogsHealDuration", "Heal duration (FFLogs)", "Seconds the healing columns are divided by.", m => Clock(m.Clocks.Seconds));
            AddEncounter("fflogsDowntime", "Downtime (FFLogs)", "Seconds of this fight when nothing could be hit.", m => Clock(m.Clocks.Downtime));

            // The pull's clock as a clock, for the overlay's header.
            //
            // ACT's own duration restarts at a phase transition, because ACT ends the encounter
            // when combat drops and opens another one when it resumes. Every figure beside it is
            // the whole pull's, so the header read "00:42" over a table describing fourteen
            // minutes. This is the fight's own elapsed time, downtime included - the clock FFLogs
            // shows a pull under, and the one the damage in the same message was measured over.
            AddEncounter("fflogsDurationText", "Duration (FFLogs, mm:ss)", "The pull's elapsed time as the overlay shows it, from the parser's fight rather than ACT's encounter.",
                m => DurationText(m.Clocks.Seconds));

            // Which pull these figures describe. An overlay that sees the id hold while ACT's
            // encounter restarts knows the two halves of a split pull are one fight - and that a
            // row ACT has not booked yet is a row it is about to, not a player who left.
            AddEncounter("fflogsFightId", "Fight id (FFLogs)", "The parser's id for this pull. Unchanged across a phase transition that ACT reports as two encounters.",
                m => m.FightId.ToString(CultureInfo.InvariantCulture));
            // These two are the answer to "why is the table ACT's?", so they report even when
            // nothing was applied - which is exactly when someone wants to read them.
            AddEncounter("fflogsApplied", "FFLogs applied", "1 when the parser's fight is the pull ACT is reporting, 0 when every column is ACT's own.",
                m => m.Applied ? "1" : "0", always: true);
            AddEncounter("fflogsParserVersion", "FFLogs parser", "Which build of FFLogs' parser produced these figures.",
                _ => ParserHost.ParserVersion, always: true);
        }

        private static string Rate(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Whole(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// A clock, to the millisecond, and not rounded to whole seconds.
        ///
        /// The rDPS family is divided here, by the exact figure; every other per-second column is
        /// divided by the overlay, using this. Rounding it meant the two sides divided the same
        /// damage by 30 and by 30.4 - so a solo pull, where rDPS is by definition just DPS with
        /// nobody to give or take buffs, showed the two columns 1-2% apart.
        /// </summary>
        private static string Clock(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>A clock the way ACT writes one: mm:ss, and h:mm:ss once past an hour.</summary>
        private static string DurationText(double seconds)
        {
            var whole = (int)Math.Max(0, Math.Floor(seconds));
            var hours = whole / 3600;
            var minutes = whole % 3600 / 60;
            return hours > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, whole % 60)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes, whole % 60);
        }

        /// <summary>The biggest hit as ACT spells it: the ability's name, a dash, and the number.</summary>
        private static string MaxHitText(PlayerFigures f) =>
            f.Hits.MaxHit <= 0 ? "" : (f.MaxHitAbility.Length > 0 ? f.MaxHitAbility : "?") + "-" + Whole(f.Hits.MaxHit);

        private static string MaxHealText(PlayerFigures f) => !f.HasHealing ? "" :
            f.HealHits.MaxHit <= 0 ? "" : (f.MaxHealAbility.Length > 0 ? f.MaxHealAbility : "?") + "-" + Whole(f.HealHits.MaxHit);

        /// <summary>
        /// One FFLogs column. Empty string when the parser has nothing for this row, so the overlay
        /// keeps whatever ACT put there.
        /// </summary>
        private void AddFflogs(string key, string label, string description, Func<PlayerFigures, string> compute)
        {
            if (CombatantData.ExportVariables.ContainsKey(key)) return;

            CombatantData.ExportVariables.Add(key, new CombatantData.TextExportFormatter(
                key, label, description,
                (data, extraFormat) =>
                {
                    try
                    {
                        var name = data?.Name;
                        if (string.IsNullOrEmpty(name)) return "";

                        // ACT calls the logging player "YOU"; the parser knows their real name.
                        string resolved;
                        lock (gate) resolved = statuses.Resolve(name);

                        // meters is volatile and never changed after publication, so this runs
                        // lock-free inside OverlayPlugin's Parallel.ForEach over allies.
                        var figures = meters.Lookup(resolved);
                        return figures == null ? "" : compute(figures);
                    }
                    catch (Exception)
                    {
                        // Never let a bad column take the whole CombatData payload down with it.
                        return "";
                    }
                }));
        }

        private void AddEncounter(string key, string label, string description, Func<MeterSnapshot, string> compute,
            bool always = false)
        {
            if (EncounterData.ExportVariables.ContainsKey(key)) return;

            EncounterData.ExportVariables.Add(key, new EncounterData.TextExportFormatter(
                key, label, description,
                (data, allies, extraFormat) =>
                {
                    try
                    {
                        var snapshot = meters;
                        return always || snapshot.Applied ? compute(snapshot) : "";
                    }
                    catch (Exception)
                    {
                        return "";
                    }
                }));
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

                            sb.AppendFormat("    {0,-24} gcd: {1} casts, uptime {2:0.0}%, lost {3:0.0}s, recast {4:0.00}s (speed stat {5}, {6} skill / {7} spell samples){8}{9}",
                                player, gcd.Count, gcd.Uptime * 100, gcd.Clip, gcd.Recast, gcd.SpeedStat,
                                gcd.SkillSpeedSamples, gcd.SpellSpeedSamples,
                                gcd.RecastEstimated ? "" : " - default, too few casts to measure",
                                gcd.AfterEnd > 0 ? " - " + gcd.AfterEnd + " pressed after the fight ended, not counted" : "");
                            sb.AppendLine();

                            if (gcd.Trace != null)
                            {
                                var first = gcd.Trace.Count > 0 ? gcd.Trace[0].TimeMs : 0;
                                // "down" is how much of the gap nothing could be hit. A transition
                                // that reads gap 47.67, down 0.00, lost 45.21 is the windows not
                                // reaching the tracker; gap 47.67, down 45.30, lost 0.00 is right.
                                sb.AppendLine("        t(s)    action  cast  recast   gap   down   occupied  lost");
                                foreach (var c in gcd.Trace)
                                    sb.AppendFormat("        {0,6:0.00}  {1,6:X}  {2,-4}  {3,6:0.00}  {4,6:0.00}  {5,5:0.00}  {6,8:0.00}  {7,5:0.00}{8}",
                                        (c.TimeMs - first) / 1000.0, c.ActionId, c.HardCast ? "hard" : "inst",
                                        c.RecastMs / 1000.0, c.GapMs / 1000.0, c.DownMs / 1000.0, c.OccupiedMs / 1000.0, c.LostMs / 1000.0,
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
                lock (gate) diag.Describe(sb, statuses, categories, actionData, gcds, parser, meters);

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

        private static string ResolveDataPath(string fileName) => OverlayAddon.ResolveDataPath(fileName);

        private string DiagnosticPath(string fileName)
        {
            if (diagnosticDir == null)
            {
                diagnosticDir = OverlayAddon.PluginDirectory() ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Advanced Combat Tracker", "Config");
            }

            return diagnosticDir == null ? null : Path.Combine(diagnosticDir, fileName);
        }
    }
}
