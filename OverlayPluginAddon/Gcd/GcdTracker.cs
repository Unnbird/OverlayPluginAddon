using System;
using System.Collections.Generic;
using OverlayPluginAddon.Fflogs;

namespace OverlayPluginAddon
{
    public struct GcdCastTrace
    {
        public uint ActionId;
        public double TimeMs;
        public bool HardCast;
        public double RecastMs;
        public double GapMs;

        /// <summary>How much of the gap nothing could be hit, and so was never this player's to fill.</summary>
        public double DownMs;
        public double OccupiedMs;
        public double LostMs;
    }

    public struct GcdStats
    {
        public int Count;

        /// <summary>This player's plain 2.5s-base GCD in seconds, after their speed stat.</summary>
        public double Recast;

        /// <summary>The speed stat inferred for this player, or 0 when nothing could be inferred.</summary>
        public int SpeedStat;

        /// <summary>
        /// Fraction of the time from this player's first press to their latest one that a GCD
        /// was occupying, 0..1. Measured at the latest press - see StatsFor.
        /// </summary>
        public double Uptime;

        /// <summary>Total seconds lost to gaps longer than the GCD that preceded them.</summary>
        public double Clip;

        /// <summary>
        /// Seconds the player's closed casts occupied - Uptime's numerator, before any division.
        /// The cast still in flight is not in it.
        /// </summary>
        public double OccupiedSeconds;

        /// <summary>
        /// Seconds from this player's first press to their latest one with the downtime windows
        /// taken out - the span the lost time is measured against.
        /// </summary>
        public double ActiveSeconds;

        /// <summary>Whether the speed stat came from observation rather than the default.</summary>
        public bool RecastEstimated;

        public int SkillSpeedSamples;
        public int SpellSpeedSamples;

        /// <summary>Every cast with the numbers that went into it. Filled only when asked for.</summary>
        public List<GcdCastTrace> Trace;
    }

    /// <summary>
    /// Per-player GCD uptime, following the model xivanalysis uses.
    ///
    /// The naive approach - measure each action's recast from how long it takes that player to
    /// press the next button - falls apart because recasts differ per action (dance steps 1s,
    /// Ninjutsu and Hypercharge filler 1.5s, Six-sided Star 5s) and there is rarely enough of any
    /// one action to measure it. xivanalysis inverts the problem:
    ///
    ///   1. Every action's *base* recast is known data (data/actions.json, from xivanalysis).
    ///   2. So an observed interval can be normalised: divide out the leading action's base recast
    ///      and any haste that was active, and every interval in the fight collapses onto one
    ///      distribution regardless of which button produced it.
    ///   3. The mode of that distribution is the player's 2.5s-base GCD, which inverts to a single
    ///      speed stat.
    ///   4. That one stat then re-derives the true recast of every action they press.
    ///
    /// One learned parameter explains the whole rotation, and an action pressed twice all fight
    /// gets as good a recast as the one pressed two hundred times.
    ///
    /// Intervals led by an action with no speed attribute are excluded from step 2 - a 1s dance
    /// step is 1s at any amount of skill speed, so it carries no information about the stat - but
    /// those actions still occupy their flat recast in step 4.
    /// </summary>
    public class GcdTracker
    {
        /// <summary>
        /// Log timestamps arrive quantised, so a 2.50s recast shows up spread across a band rather
        /// than as one value. Batching before taking the mode is what stops the estimate landing
        /// on whichever single millisecond happened to repeat most. 45ms matches xivanalysis.
        /// </summary>
        private const int BatchSizeMs = 45;

        /// <summary>Batches either side of the mode folded into the weighted average.</summary>
        private const int BatchRadius = 2;

        private const int MinIntervalSample = 5;

        /// <summary>
        /// The game never lets a GCD recast go below this, and an action whose base recast is
        /// already at or under it is not scaled by speed at all. Mirrors MIN_RECAST_TIME in
        /// xivanalysis' CastTime.
        /// </summary>
        private const double MinRecastMs = 1500;

        /// <summary>
        /// Slack before a gap between GCDs is called lost time: 100ms of caster tax plus 50ms of
        /// timestamp jitter. Without it every clean rotation reports a few seconds of "clip" made
        /// entirely of log noise. GCD_ERROR_OFFSET in xivanalysis.
        /// </summary>
        private const double GcdErrorOffsetMs = 150;

        /// <summary>
        /// A hard cast can be slidecast: movement is allowed in its last half second and the effect
        /// still lands, so the recast has effectively been turning that long already. Only applied
        /// to gap measurement, as xivanalysis does. SLIDECAST_OFFSET.
        /// </summary>
        private const double SlidecastOffsetMs = 500;

        /// <summary>Intervals outside this are downtime or log artefacts, not a recast.</summary>
        private const double MinUsableIntervalMs = 400;
        private const double MaxUsableIntervalMs = 7000;

        /// <summary>
        /// How far a cast bar may sit from the table's cast time before it stops being evidence
        /// of spell speed. Speed alone cannot take more than ~20% off; anything shorter is a
        /// halved cast or a proc, anything longer is slowed.
        /// </summary>
        private const double MinCastFactor = 0.80;
        private const double MaxCastFactor = 1.02;

        private struct Cast
        {
            public uint ActionId;
            public double TimeMs;

            /// <summary>Haste multiplier active on the caster at the time, 1 when none.</summary>
            public double SpeedModifier;

            /// <summary>
            /// Whether this press was actually cast rather than fired instantly. The base cast
            /// time in the data is only what it costs when nothing made it instant, and for a Red
            /// Mage under Dualcast that is half the rotation.
            /// </summary>
            public bool HardCast;

            /// <summary>Set when actions.json knows nothing and the category had to decide.</summary>
            public bool IsSpell;

            /// <summary>
            /// How long the cast bar actually ran, from the cast-start line; 0 when unknown. The
            /// client reports this with spell speed, haste and every job mechanic already folded
            /// in, where the table only knows the unmodified value - and a Black Mage's Blizzard III
            /// under Astral Fire III runs at half of that.
            /// </summary>
            public double ActualCastMs;
        }

        private class PlayerGcd
        {
            public readonly List<Cast> Casts = new List<Cast>();
            public uint LastActionId;
            public double LastTimeMs = double.NaN;

            // One distribution per speed attribute: a Red Mage's spells and weaponskills are
            // governed by different stats and must not be pooled.
            public readonly List<double> SkillSpeedIntervals = new List<double>();
            public readonly List<double> SpellSpeedIntervals = new List<double>();

            // What the overlay is showing. Measured when a cast is recorded or removed, and only
            // then - see StatsFor.
            public GcdStats Snapshot;

            /// <summary>Which set of downtime windows Snapshot was measured against.</summary>
            public int SnapshotVersion = -1;
        }

        private readonly Dictionary<string, PlayerGcd> byPlayer =
            new Dictionary<string, PlayerGcd>(StringComparer.Ordinal);

        private readonly ActionData actions;

        public GcdTracker(ActionData actions)
        {
            this.actions = actions;
        }

        /// <summary>Sorted, merged stretches when nothing could be hit. See SetDowntimeWindows.</summary>
        private IReadOnlyList<DowntimeWindow> windows = Array.Empty<DowntimeWindow>();

        /// <summary>Bumped whenever the windows change, so a stored snapshot knows it is stale.</summary>
        private int windowsVersion;

        public void Clear() => byPlayer.Clear();

        /// <summary>
        /// The stretches of the fight when nothing could be hit, from the FFLogs parser's zone
        /// handler (see <see cref="DowntimeWindows"/>).
        ///
        /// Time inside one of them is taken out of both halves of the measurement: the gap it sits
        /// in is not lost GCD time, and it is not in the span the lost time is measured against.
        /// This is xivanalysis' model, whose denominator is the fight minus its downtime windows,
        /// and it is the difference between M8S' minute-long transition reading as a minute of
        /// clipping and reading as nothing at all. With no windows every gap is charged.
        /// </summary>
        /// <returns>Whether anything changed.</returns>
        public bool SetDowntimeWindows(IEnumerable<DowntimeWindow> incoming)
        {
            var merged = DowntimeWindows.Merge(incoming);
            if (merged.Count == windows.Count)
            {
                var same = true;
                for (var i = 0; i < merged.Count && same; i++)
                    same = merged[i].Start == windows[i].Start && merged[i].End == windows[i].End;
                if (same) return false;
            }

            windows = merged;
            windowsVersion++;
            return true;
        }

        /// <summary>How much of [fromMs, toMs) nothing could be hit.</summary>
        public double DowntimeBetween(double fromMs, double toMs) =>
            DowntimeWindows.Between(windows, fromMs, toMs);

        /// <summary>
        /// Records one GCD cast.
        /// </summary>
        /// <param name="speedModifier">
        /// Product of the haste statuses active on the caster right now. Captured at record time
        /// rather than reconstructed later, since the status tracker only holds current state.
        /// </param>
        public void Record(string player, uint actionId, DateTime when, double speedModifier,
                           bool hardCast, bool isSpell, double actualCastMs = 0)
        {
            if (string.IsNullOrEmpty(player)) return;

            if (!byPlayer.TryGetValue(player, out var state))
                byPlayer[player] = state = new PlayerGcd();

            var timeMs = when.Ticks / (double)TimeSpan.TicksPerMillisecond;

            // An action that hits eight targets emits one line per target, sharing id and timestamp.
            if (state.LastActionId == actionId && state.LastTimeMs == timeMs) return;

            if (!double.IsNaN(state.LastTimeMs))
                RecordInterval(state, timeMs - state.LastTimeMs);

            state.Casts.Add(new Cast
            {
                ActionId = actionId,
                TimeMs = timeMs,
                SpeedModifier = speedModifier,
                HardCast = hardCast,
                IsSpell = isSpell,
                ActualCastMs = hardCast ? actualCastMs : 0,
            });
            state.LastActionId = actionId;
            state.LastTimeMs = timeMs;
            state.Snapshot = Measure(state, null);
            state.SnapshotVersion = windowsVersion;
        }

        /// <summary>
        /// Normalises one raw interval against the action that opened it and files it under that
        /// action's speed attribute. This is what lets a 1.5s Ninjutsu and a 2.5s weaponskill vote
        /// on the same estimate.
        /// </summary>
        private void RecordInterval(PlayerGcd state, double rawMs)
        {
            if (rawMs < MinUsableIntervalMs || rawMs > MaxUsableIntervalMs) return;
            if (state.Casts.Count == 0) return;

            var previous = state.Casts[state.Casts.Count - 1];
            var def = actions.Get(previous.ActionId);
            var attribute = AttributeFor(previous, def);

            // A flat recast tells us nothing about the player's speed stat - whether the data
            // says so outright or the base is already at the 1.5s floor the game never scales.
            if (attribute == SpeedAttribute.None || def.Recast <= MinRecastMs) return;

            var recast = (double)def.Recast;
            var animationLock = 0.0;
            double adjusted;

            // Whether the cast bar or the recast gated the next press. When the bar's length is
            // known it decides: a bar that could not have reached the recast even at the slowest
            // plausible speed (Blizzard I's 1.97s against a 2.5s recast, Blizzard III halved under
            // Astral Fire III) left the recast in charge, whatever the table says the cast is -
            // and the table is what goes stale between patches.
            var castGates = previous.HardCast && (previous.ActualCastMs > 0
                ? previous.ActualCastMs >= def.Recast * previous.SpeedModifier * MinCastFactor
                : def.CastTime >= def.Recast);

            if (castGates)
            {
                // The cast gates the next press, not the recast, and caster tax follows it.
                if (previous.ActualCastMs > 0)
                {
                    // The cast bar itself is the measurement: its length over the table's is the
                    // player's speed factor, with no tax or jitter in it. A bar far shorter than
                    // the table allows for - Blizzard III halved under Astral Fire III, Fire III
                    // under Umbral Ice III - is a mechanic, not speed, and says nothing about the
                    // stat. Fed in, one such cast read as a 1.7s GCD and dragged the estimate down.
                    var factor = previous.ActualCastMs / (def.CastTime * previous.SpeedModifier);
                    if (factor < MinCastFactor || factor > MaxCastFactor) return;
                    adjusted = ActionData.BaseGcdMs * factor;
                }
                else
                {
                    animationLock = ActionData.AnimationLockMs;
                    recast = def.CastTime;
                    var scale = recast / ActionData.BaseGcdMs;
                    if (scale <= 0 || previous.SpeedModifier <= 0) return;
                    adjusted = (rawMs - animationLock) / scale / previous.SpeedModifier;
                }
            }
            else
            {
                var castTimeScale = recast / ActionData.BaseGcdMs;
                if (castTimeScale <= 0 || previous.SpeedModifier <= 0) return;
                adjusted = rawMs / castTimeScale / previous.SpeedModifier;
            }

            if (adjusted < MinUsableIntervalMs || adjusted > MaxUsableIntervalMs) return;

            if (attribute == SpeedAttribute.SpellSpeed) state.SpellSpeedIntervals.Add(adjusted);
            else state.SkillSpeedIntervals.Add(adjusted);
        }

        /// <summary>Drops the most recent cast, for a cast bar that was interrupted before it landed.</summary>
        public void RemoveLast(string player, uint actionId)
        {
            if (!byPlayer.TryGetValue(player ?? "", out var state)) return;
            if (state.Casts.Count == 0) return;

            var last = state.Casts[state.Casts.Count - 1];
            if (last.ActionId != actionId) return;

            state.Casts.RemoveAt(state.Casts.Count - 1);

            if (state.Casts.Count == 0)
            {
                state.LastTimeMs = double.NaN;
                state.Snapshot = Measure(state, null);
            state.SnapshotVersion = windowsVersion;
                return;
            }

            // Rewind so the next cast measures its interval from the one before the interruption.
            // That interval spans the wasted cast time, which is exactly the loss it represents.
            var previous = state.Casts[state.Casts.Count - 1];
            state.LastActionId = previous.ActionId;
            state.LastTimeMs = previous.TimeMs;
            state.Snapshot = Measure(state, null);
            state.SnapshotVersion = windowsVersion;
        }

        /// <summary>
        /// This player's GCD numbers as of their last press.
        ///
        /// Measured once per GCD, at the press, over the player's own presses only: the numerator
        /// is what every earlier cast occupied, the denominator is the time from their first press
        /// to this one. Nothing about the encounter is consulted.
        ///
        /// The denominator is the point. An earlier version divided by ACT's encounter duration,
        /// and ACT's clock is driven by damage: it advances when a hit lands and not otherwise.
        /// Casts are recorded when the button goes down, and for a hard cast the hit lands a cast
        /// time later - a real Red Mage log showed Jolt's damage 1.47s after its cast bar started.
        /// So at the moment a hard cast begins, the instant before it (Verthunder under Dualcast,
        /// anything under Swiftcast or Acceleration) closes and is charged its full recast, while
        /// the denominator still ends at that instant's hit. Numerator up, denominator unmoved,
        /// uptime jumps - and falls back when the next press drags the clock forward. Every
        /// hard cast following an instant produced that sawtooth.
        ///
        /// Measured from press to press, both halves move at the same moments by construction:
        /// a press closes exactly one interval, adds exactly its recast to the top and exactly its
        /// gap to the bottom. A clean rotation reads 100% at every press, and the number cannot
        /// depend on when a hit happened to land or when the overlay happened to look.
        ///
        /// Two things are outside the window. The cast in flight, whose recast is still turning:
        /// it is in the count but not yet in either half. And the tail after the final press: idle
        /// time is charged by the press that ends it, so a player who dies with two minutes left
        /// keeps the uptime they had when they died.
        /// </summary>
        public GcdStats StatsFor(string player, bool includeTrace = false)
        {
            if (string.IsNullOrEmpty(player) || !byPlayer.TryGetValue(player, out var state))
                return new GcdStats { Recast = ActionData.BaseGcdMs / 1000.0 };

            if (!includeTrace)
            {
                // A window that opened or closed since the last press changes what the presses
                // before it mean, so a stale snapshot is re-measured rather than handed back.
                if (state.SnapshotVersion != windowsVersion)
                {
                    state.Snapshot = Measure(state, null);
                    state.SnapshotVersion = windowsVersion;
                }
                return state.Snapshot;
            }

            // Nothing outside the cast list goes into the measurement, so re-deriving it with a
            // trace attached reproduces the snapshot exactly: the rows add up to the number shown.
            return Measure(state, new List<GcdCastTrace>(state.Casts.Count));
        }

        private GcdStats Measure(PlayerGcd state, List<GcdCastTrace> trace)
        {
            var stats = new GcdStats { Recast = ActionData.BaseGcdMs / 1000.0 };

            stats.Count = state.Casts.Count;
            if (stats.Count == 0) return stats;

            var skillStat = EstimateStat(state.SkillSpeedIntervals);
            var spellStat = EstimateStat(state.SpellSpeedIntervals);

            stats.RecastEstimated = skillStat != null || spellStat != null;
            var headline = skillStat ?? spellStat;
            if (headline != null)
            {
                stats.SpeedStat = headline.Value;
                stats.Recast = ActionData.AdjustedDuration(headline.Value, ActionData.BaseGcdMs) / 1000.0;
            }

            // Accumulated per closed *interval*: each cast except the newest opens one, running
            // to the next press. The numerator is what those casts occupied, the denominator is the
            // sum of those intervals - first press to latest press - so the two halves cover the
            // same window by construction and the newest cast, whose recast is still turning, is
            // in neither.
            //
            // Occupied is the xivanalysis model: every closed cast contributes its full GCD
            // duration, never min(recast, gap). Log timestamps are batched at ~45ms, so a clean
            // 2.5s rotation shows gaps of 2.46 to 2.54; charging the gap would shave every
            // early-looking press and never read 100%. A press that lands 40ms early because of
            // jitter still cost a whole GCD. The same jitter can push the ratio a hair over one,
            // hence the clamp.
            //
            // Lost time is the separate question xivanalysis' downtime windows answer, and it does
            // look at gaps - with 150ms of slack for caster tax and jitter, and the slidecast
            // window after a hard cast, so that a clean rotation reports zero rather than a few
            // seconds of noise. The gap after the newest cast is not closed yet, which is why an
            // idle stretch is charged by the press that ends it.
            double occupiedMs = 0;
            double clipMs = 0;

            for (var i = 0; i + 1 < state.Casts.Count; i++)
            {
                var cast = state.Casts[i];
                var recast = OccupiedMs(cast, skillStat, spellStat, out var castGated);
                var gap = state.Casts[i + 1].TimeMs - cast.TimeMs;

                occupiedMs += recast;

                // Only the part of the gap when there was something to hit can be lost: the rest is
                // the boss being untargetable, which is nobody's clipping. A press right before a
                // minute-long transition and another right after it is a clean rotation.
                var down = DowntimeBetween(cast.TimeMs, state.Casts[i + 1].TimeMs);
                var idle = gap - down;

                // Slidecast slack belongs only to a cast that gated the GCD: that is the one whose
                // bar the next press waited on. A Blizzard I runs 1.97s under a 2.45s recast - the
                // recast gates, the bar is irrelevant, and a press 0.5s after the recast ended is
                // half a second of idle, not a slidecast.
                var slack = GcdErrorOffsetMs + (castGated ? SlidecastOffsetMs : 0);
                var lost = idle > recast + slack ? idle - recast : 0;
                clipMs += lost;

                trace?.Add(new GcdCastTrace
                {
                    ActionId = cast.ActionId, TimeMs = cast.TimeMs, HardCast = cast.HardCast,
                    RecastMs = recast, GapMs = gap, DownMs = down, OccupiedMs = recast, LostMs = lost,
                });
            }

            // The newest cast, for the trace only: its recast is known, its gap is not yet.
            var newest = state.Casts[state.Casts.Count - 1];
            trace?.Add(new GcdCastTrace
            {
                ActionId = newest.ActionId, TimeMs = newest.TimeMs, HardCast = newest.HardCast,
                RecastMs = OccupiedMs(newest, skillStat, spellStat, out _), GapMs = 0, DownMs = 0, OccupiedMs = 0, LostMs = 0,
            });

            stats.Trace = trace;
            stats.SkillSpeedSamples = state.SkillSpeedIntervals.Count;
            stats.SpellSpeedSamples = state.SpellSpeedIntervals.Count;
            stats.OccupiedSeconds = occupiedMs / 1000.0;
            stats.Clip = clipMs / 1000.0;

            // Uptime is one minus lost time, xivanalysis' downtime model, and NOT occupied over
            // span. The two agree when the recast estimate is exact and differ in the one way
            // that matters when it is not: an estimate a few percent high hands every cast more
            // occupancy than the gap it sat in, and thirty such casts manufacture the seconds a
            // real pause cost. Keep attacking after a break and the reading climbed back to 100%.
            // Lost time is charged only past the slack, so jitter never counts and a break never
            // stops counting.
            //
            // The span is the player's own presses with the downtime windows taken out, matching
            // the numerator: both halves count only the time there was something to hit.
            var spanMs = newest.TimeMs - state.Casts[0].TimeMs
                - DowntimeBetween(state.Casts[0].TimeMs, newest.TimeMs);
            stats.ActiveSeconds = Math.Max(0, spanMs) / 1000.0;
            if (spanMs > 0)
                stats.Uptime = Math.Max(0.0, Math.Min(1.0, 1.0 - clipMs / spanMs));

            return stats;
        }

        /// <summary>
        /// How long one cast tied up the GCD: the greater of its recast and its cast time, with
        /// caster tax added when the cast is at least a full GCD.
        /// </summary>
        private double OccupiedMs(Cast cast, int? skillStat, int? spellStat, out bool castGated)
        {
            castGated = false;
            var def = actions.Get(cast.ActionId);
            var attribute = AttributeFor(cast, def);

            double recast = def.Recast;
            double castTime = def.CastTime;

            // xivanalysis' getAdjustedTime, step for step: a base recast already at the 1.5s floor
            // is left alone entirely; otherwise the speed stat applies, then haste, then the
            // result is floored to 10ms and clamped back to the floor.
            if (attribute != SpeedAttribute.None && recast > MinRecastMs)
            {
                var stat = attribute == SpeedAttribute.SpellSpeed
                    ? (spellStat ?? skillStat)
                    : (skillStat ?? spellStat);

                if (stat != null)
                {
                    recast = ActionData.AdjustedDuration(stat.Value, recast);
                    if (castTime > 0) castTime = ActionData.AdjustedDuration(stat.Value, castTime);
                }

                recast = Math.Max(MinRecastMs, Math.Floor(recast * cast.SpeedModifier / 10) * 10);
                if (castTime > 0) castTime = Math.Floor(castTime * cast.SpeedModifier / 10) * 10;
            }

            if (!cast.HardCast)
            {
                castTime = 0;
            }
            else
            {
                // Prefer what the client reported over the table when it is known - the table
                // cannot see Astral Fire halving a Blizzard III.
                if (cast.ActualCastMs > 0) castTime = cast.ActualCastMs;

                // Caster tax follows a cast that gates the GCD: one at least as long as the recast
                // it sits on. Compared at the same scale - both already adjusted - and with the
                // table's unmodified pair as the tie-breaker, because after spell speed a 2.5s cast
                // on a 2.5s recast is 2.35s on 2.35s and must still pay. Checking the adjusted cast
                // against the flat 2500 instead meant no cast-equals-recast spell ever paid tax
                // once the player had any spell speed at all: Blizzard I occupied 2.35s, the next
                // press came at 2.45s, and uptime read 96% on a flawless rotation.
                castGated = castTime > 0 && (castTime >= recast - 10 || def.CastTime >= def.Recast && cast.ActualCastMs <= 0);
                if (castGated) castTime += ActionData.AnimationLockMs;
            }

            return Math.Max(recast, castTime);
        }

        /// <summary>
        /// Which speed stat governs this press.
        ///
        /// actions.json only carries the actions that differ from the default, so everything else
        /// has to fall back on the action's category - a caster's whole rotation pooling into the
        /// skill-speed distribution would leave both estimates built from the wrong samples.
        /// </summary>
        private SpeedAttribute AttributeFor(Cast cast, ActionDef def)
        {
            if (actions.Knows(cast.ActionId)) return def.SpeedAttribute;
            return cast.IsSpell ? SpeedAttribute.SpellSpeed : SpeedAttribute.SkillSpeed;
        }

        /// <summary>
        /// The player's 2.5s-base GCD, as a speed stat. Null when there is not enough to go on.
        /// </summary>
        private static int? EstimateStat(List<double> normalisedIntervals)
        {
            if (normalisedIntervals.Count < MinIntervalSample) return null;

            var batches = new Dictionary<int, int>();
            var modeBatch = 0;
            var modeCount = 0;

            foreach (var interval in normalisedIntervals)
            {
                var batch = (int)Math.Floor(interval / BatchSizeMs);
                batches.TryGetValue(batch, out var count);
                batches[batch] = ++count;
                if (count > modeCount) { modeCount = count; modeBatch = batch; }
            }

            // Weighted average over the mode and its neighbours, so an estimate that straddles a
            // batch boundary is not thrown off by which side happened to win.
            double intervalSum = 0;
            double countSum = 0;

            for (var batch = modeBatch - BatchRadius; batch <= modeBatch + BatchRadius; batch++)
            {
                if (!batches.TryGetValue(batch, out var count) || count == 0) continue;

                var averageInterval = (batch * BatchSizeMs + (batch + 1) * BatchSizeMs - 1) / 2.0;
                intervalSum += averageInterval * count;
                countSum += count;
            }

            if (countSum == 0) return null;

            // Tooltip GCDs are tiered to 0.01s, so round there rather than carrying noise into
            // the stat conversion.
            var estimate = Math.Round(intervalSum / countSum / 10) * 10;
            return ActionData.SpeedStatFor(estimate);
        }

        public IEnumerable<string> Players => byPlayer.Keys;
    }
}
