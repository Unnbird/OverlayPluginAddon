using System;
using System.Collections.Generic;
using System.Linq;

namespace OverlayPluginAddon.Fflogs
{
    /// <summary>
    /// Whether this fight is the pull the caller is reporting, and why. Live that is ACT's
    /// encounter; a replay has no encounter and takes every fight.
    /// </summary>
    public delegate bool AppliedDecision(FightFigures fight, out string reason);

    /// <summary>
    /// Everything between "the parser collected" and "the columns have numbers".
    ///
    /// Kept apart from the event source so the same chain can be driven without ACT - a replay of a
    /// saved network log runs exactly this, which is the only way to check the figures against an
    /// FFLogs report offline.
    /// </summary>
    public sealed class MeterPipeline
    {
        private readonly AppliedDecision decide;
        private readonly Action<string> onError;

        /// <summary>The last fight converted out of the parser. Touched only by the parser thread.</summary>
        private FightFigures lastFight;

        /// <summary>
        /// This pull's downtime windows, kept across collects and thrown away when the fight
        /// changes.
        ///
        /// The windows are read off the parser's live zone handler, and the parser drops that
        /// handler the moment the fight ends - so the last collect of a pull, the one whose figures
        /// everybody actually reads, arrived with no windows at all. Every GCD number was then
        /// re-measured with nothing to exclude, and a minute-long transition that had been
        /// correctly ignored all fight turned back into a minute of clipping right at the end.
        ///
        /// Keyed by start rather than appended: a window that is still open is reported again on
        /// every collect with a later end, and the last word about a start is the true one.
        /// </summary>
        private readonly Dictionary<double, double> windowEndByStart = new Dictionary<double, double>();
        private long windowFightId;

        /// <summary>
        /// The table the export formatters read. Replaced wholesale, never modified, so a reader on
        /// another thread either sees the previous snapshot or the next one and never a half-built
        /// table - and needs no lock to do it.
        /// </summary>
        public MeterSnapshot Current => current;
        private volatile MeterSnapshot current = MeterSnapshot.Empty;

        /// <summary>Raised on the parser thread after each snapshot is published.</summary>
        public event Action<MeterSnapshot> Published;

        public MeterPipeline(AppliedDecision decide, Action<string> onError = null)
        {
            this.decide = decide ?? ((FightFigures f, out string reason) => { reason = "no rule"; return false; });
            this.onError = onError ?? (message => { });
        }

        /// <summary>
        /// Takes whatever fight the parser has. This is the rule for a replay, which has no ACT and
        /// therefore no encounter to disagree with.
        /// </summary>
        public static bool TakeEveryFight(FightFigures fight, out string reason)
        {
            reason = fight == null ? "no fight yet" : "no encounter to match against";
            return fight != null && fight.Damage.Count > 0;
        }

        public void Reset()
        {
            lastFight = null;
            windowEndByStart.Clear();
            windowFightId = 0;
            current = MeterSnapshot.Empty;
        }

        /// <summary>
        /// One collectMeters(), on the parser's thread. Everything that is still a script object is
        /// converted here, because the parser hands a finished fight over exactly once - the collect
        /// that returns it also drops it from the list.
        /// </summary>
        public void Accept(ParserCollection collection)
        {
            if (collection == null) return;

            try
            {
                lastFight = ParserOutput.Read(collection.Meters, collection.Output, lastFight);

                var fightId = lastFight?.Id ?? 0;
                if (fightId != windowFightId)
                {
                    windowFightId = fightId;
                    windowEndByStart.Clear();
                }
                foreach (var window in DowntimeWindows.Read(collection.ZoneHandler, collection.LastLineMs, onError))
                    windowEndByStart[window.Start] = window.End;

                var windows = DowntimeWindows.Merge(
                    windowEndByStart.Select(pair => new DowntimeWindow(pair.Key, pair.Value)));

                var applied = decide(lastFight, out var reason);
                var snapshot = MeterSnapshot.Build(lastFight, applied, reason, windows);
                current = snapshot;
                Published?.Invoke(snapshot);
            }
            catch (Exception e)
            {
                onError("collect: " + e.Message);
            }
        }
    }
}
