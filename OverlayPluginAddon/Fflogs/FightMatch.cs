using System;

namespace OverlayPluginAddon.Fflogs
{
    /// <summary>The two clocks one fight is measured against.</summary>
    public struct FightClocks
    {
        /// <summary>The whole pull, end to end.</summary>
        public double Seconds;

        /// <summary>Of which nothing could be hit.</summary>
        public double Downtime;

        /// <summary>What damage is divided by: the pull minus its downtime.</summary>
        public double Active;
    }

    /// <summary>
    /// Whether the parser's fight and ACT's encounter are the same pull, and what each is measured
    /// against.
    ///
    /// Ported from mopimopi's js/fflogs/apply.js. The rules are the same; the inputs are better,
    /// because in the addon ACT's encounter can be read directly rather than reconstructed from a
    /// CombatData message.
    /// </summary>
    public static class FightMatch
    {
        /// <summary>
        /// How far ACT's encounter clock and the parser's fight clock may disagree and still be one
        /// pull. Both run off the same log lines and both start near the first hit, so on the same
        /// pull they stay within seconds of each other. After a wipe ACT opens a new encounter at
        /// the next hit while the parser is still reporting the finished fight, and the gap is the
        /// whole previous pull - which is the case this has to refuse.
        /// </summary>
        public const double FightToleranceSeconds = 90;

        /// <summary>
        /// A fight that has ended may still claim an ACT encounter that began this much after its
        /// last event: durations are floored to whole seconds and the clock is the latest log line,
        /// so an encounter that really began a hair before the end can read as a hair after it. A
        /// new pull after a wipe starts far later than this.
        /// </summary>
        public const double ClosedFightSlackSeconds = 5;

        public static bool DurationsMatch(double actDurationSeconds, double fightDurationSeconds,
            double toleranceSeconds = FightToleranceSeconds)
        {
            if (double.IsNaN(actDurationSeconds) || double.IsNaN(fightDurationSeconds)) return false;
            return Math.Abs(actDurationSeconds - fightDurationSeconds) <= toleranceSeconds;
        }

        /// <summary>
        /// Whether ACT's encounter began while the parser's fight was running - the case the
        /// duration rule cannot see.
        ///
        /// ACT ends its encounter and opens a new one whenever combat drops for its idle timeout,
        /// and a scripted phase transition is exactly that: in M8S the first body dies, a minute
        /// passes, the second body appears, and ACT's second encounter starts there while FFLogs
        /// keeps the pull as one fight. From then on ACT's duration counts from the transition and
        /// the parser's from the pull, minutes apart.
        ///
        /// While the fight is in progress "inside" is open-ended: the encounter may begin in a lull
        /// the parser has booked nothing for yet. Once the fight has ended it is not - a new pull
        /// after a wipe begins after the old fight's last event and must not inherit its figures.
        /// </summary>
        public static bool EncounterWithinFight(double actStartMs, double fightStartMs, double fightEndMs,
            bool fightInProgress, double toleranceSeconds = FightToleranceSeconds)
        {
            if (double.IsNaN(actStartMs) || double.IsNaN(fightStartMs) || double.IsNaN(fightEndMs)) return false;
            if (actStartMs < fightStartMs - toleranceSeconds * 1000) return false;
            if (fightInProgress) return true;
            return actStartMs <= fightEndMs + ClosedFightSlackSeconds * 1000;
        }

        /// <summary>
        /// The two clocks FFLogs keeps for one fight, and which figures each of them divides.
        ///
        /// Damage per second is measured over the fight minus its downtime - the stretches where the
        /// boss cannot be hit at all. Healing per second is measured over the whole fight. That
        /// asymmetry is not a convention of ours; it is the line FFLogs' own uploader runs:
        ///
        ///     ((fight.endTime - fight.startTime) - (type === 'friendlyDamage' ? fight.downtime : 0)) / 1000
        ///
        /// and it is why the parser recomputes fight.downtime on every collectMeters(). Dividing
        /// damage by the whole fight instead reads about 8% low on a pull with a minute of downtime.
        ///
        /// Active falls back to the whole fight when downtime is absent, nonsense, or the entire
        /// fight - a divisor of zero is worse than a slightly generous one.
        /// </summary>
        public static FightClocks Clocks(double durationSeconds, double downtimeSeconds)
        {
            var seconds = Math.Max(0, Sane(durationSeconds));
            var downtime = Math.Max(0, Sane(downtimeSeconds));
            return new FightClocks
            {
                Seconds = seconds,
                Downtime = downtime,
                Active = downtime > 0 && downtime < seconds ? seconds - downtime : seconds,
            };
        }

        public static FightClocks Clocks(FightFigures fight) =>
            fight == null ? Clocks(0, 0) : Clocks(fight.DurationSeconds, fight.DowntimeSeconds);

        private static double Sane(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
    }
}
