using System;

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

                var windows = DowntimeWindows.Merge(
                    DowntimeWindows.Read(collection.ZoneHandler, collection.LastLineMs, onError));

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
