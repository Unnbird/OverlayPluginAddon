namespace OverlayPluginAddon.Fflogs
{
    /// <summary>
    /// Decides when a pull ended and the GCD record should start over.
    ///
    /// ACT cannot answer that on its own. It ends an encounter whenever combat drops for its idle
    /// timeout, and a scripted phase transition is exactly that: in M8S the first body dies, a
    /// minute passes, the second appears, and ACT opens a second encounter - while FFLogs keeps the
    /// whole thing as one fight and every damage column here reports it as one. Resetting on ACT's
    /// boundary threw the first half's GCDs away and left one column describing a different stretch
    /// of the pull from all the others.
    ///
    /// So the parser's fight is the pull, and ACT's encounter is only consulted when there is no
    /// fight to go on - content the parser has no handler for, or a parser that failed to start.
    /// </summary>
    public sealed class PullBoundary
    {
        /// <summary>The parser's fight this describes, or 0 when it has none.</summary>
        public long FightId { get; private set; }

        /// <summary>Whether the parser is the one deciding right now.</summary>
        public bool FollowingParser => FightId != 0;

        /// <summary>
        /// A snapshot arrived. Returns whether it is a different pull from the one being tracked.
        ///
        /// A collect with no fight in it is the parser between pulls, not the end of one: it keeps
        /// reporting the finished fight until the next one opens, and dropping the record in that
        /// gap would lose the end of the pull that just happened.
        /// </summary>
        public bool NoteFight(long fightId)
        {
            if (fightId == 0 || fightId == FightId) return false;
            FightId = fightId;
            return true;
        }

        /// <summary>
        /// ACT moved to a different encounter. Returns whether that is a new pull, which it only is
        /// when the parser has nothing to say.
        /// </summary>
        public bool NoteEncounterChanged() => FightId == 0;

        /// <summary>
        /// Forgets the fight. For a zone change - no fight survives one - and for the parser being
        /// stopped, after which ACT has to decide again.
        /// </summary>
        public void Reset() => FightId = 0;
    }
}
