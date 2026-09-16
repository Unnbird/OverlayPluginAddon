using System;
using System.Collections.Generic;
using Microsoft.ClearScript;

namespace OverlayPluginAddon.Fflogs
{
    /// <summary>A stretch of a fight when nothing could be hit, in log time.</summary>
    public struct DowntimeWindow
    {
        public double Start;
        public double End;

        public DowntimeWindow(double start, double end) { Start = start; End = end; }
        public double Seconds => Math.Max(0, End - Start) / 1000.0;
    }

    /// <summary>
    /// The stretches of a fight when nothing could be hit, read off the parser's zone handler.
    ///
    /// <c>fight.downtime</c> is only the total, and the total is all the damage clock needs. The GCD
    /// columns need the windows themselves: a gap between two presses that sat inside one is not a
    /// gap the player could have filled, and charging it reads M8S' minute-long transition as a
    /// minute of clipping.
    ///
    /// The handlers are hand-written per instance and keep their downtime in three different
    /// shapes. All of them have to be read or the GCD columns disagree with the DPS columns about
    /// the same pull - and getting one wrong is worse than getting none: a window read as never
    /// closing swallows every real clip after it.
    ///
    /// Ported from mopimopi's js/fflogs/meter.js downtimeWindows(), which mopimopi's
    /// tests/test-fflogs-downtime.js checks shape by shape.
    /// </summary>
    public static class DowntimeWindows
    {
        /// <summary>
        /// The named start/end field pairs, in the parser's own spelling (see each handler's
        /// totalDowntimeForFightRange in parser-ff.js). downtimeStart/downtimeEnd is P12S, M4S and
        /// Queen Eternal - and FRU, which has no downtimeEnd and relies on the open-window rule
        /// below. The first/second pair is M5S. The rest are the Omega Protocol's transitions,
        /// which it names one by one.
        /// </summary>
        private static readonly string[][] Pairs =
        {
            new[] { "downtimeStart", "downtimeEnd" },
            new[] { "firstDowntimeStart", "firstDowntimeEnd" },
            new[] { "secondDowntimeStart", "secondDowntimeEnd" },
            new[] { "p2DowntimeStart", "p2DowntimeEnd" },
            new[] { "p3TransitionStart", "p3TransitionEnd" },
            new[] { "p4BlueScreenCast", "p5OmegaMTargetable" },
            new[] { "p5DeltaDynamisStart", "p5DeltaDynamisEnd" },
            new[] { "p5SigmaDynamisStart", "p5SigmaDynamisEnd" },
            new[] { "p5OmegaDynamisStart", "p5OmegaDynamisEnd" },
            new[] { "blindFaithCast", "targetableAfterBlindFaith" },
        };

        /// <summary>
        /// Every window the handler currently has, the open one included, running to
        /// <paramref name="lastLineMs"/>.
        ///
        /// The pairs read the way the parser's own downtimeForRange reads them: a start of 0 means
        /// the window never opened; an end of 0, or a field this handler does not have, means it
        /// has not closed yet. A handler that throws, or one this build has never heard of, yields
        /// nothing and the GCD columns go back to charging every gap.
        /// </summary>
        public static List<DowntimeWindow> Read(ScriptObject handler, double lastLineMs, Action<string> onError = null)
        {
            var windows = new List<DowntimeWindow>();
            if (handler == null) return windows;

            void Push(double start, double end)
            {
                if (double.IsNaN(start) || double.IsNaN(end) || end <= start) return;
                windows.Add(new DowntimeWindow(start, end));
            }

            try
            {
                var tracker = ParserOutput.Get(handler, "downtimeTracker");
                if (tracker != null)
                {
                    var committed = ParserOutput.Get(tracker, "committedIntervals");
                    for (var i = 0; i < ParserOutput.Length(committed); i++)
                    {
                        var window = ParserOutput.Obj(committed[i]);
                        Push(ParserOutput.Num(ParserOutput.Raw(window, "start")),
                             ParserOutput.Num(ParserOutput.Raw(window, "end")));
                    }

                    // An open interval has no end yet: it runs to wherever the log has got to.
                    var pending = ParserOutput.Get(tracker, "pendingInterval");
                    if (pending != null)
                    {
                        var start = ParserOutput.Num(ParserOutput.Raw(pending, "start"));
                        var end = ParserOutput.Num(ParserOutput.Raw(pending, "end"));
                        Push(start, end > start ? end : lastLineMs);
                    }
                }

                var periods = ParserOutput.Get(handler, "downtimePeriods");
                for (var i = 0; i < ParserOutput.Length(periods); i++)
                {
                    var window = ParserOutput.Obj(periods[i]);
                    Push(ParserOutput.Num(ParserOutput.Raw(window, "start")),
                         ParserOutput.Num(ParserOutput.Raw(window, "end")));
                }

                foreach (var pair in Pairs)
                {
                    var start = ParserOutput.Num(ParserOutput.Raw(handler, pair[0]));
                    if (!(start > 0)) continue;
                    var end = ParserOutput.Num(ParserOutput.Raw(handler, pair[1]));
                    Push(start, end > start ? end : lastLineMs);
                }
            }
            catch (Exception e)
            {
                onError?.Invoke("downtimeWindows: " + e.Message);
                return new List<DowntimeWindow>();
            }

            return windows;
        }

        /// <summary>
        /// Sorted, with overlaps merged, so any interval's share can be summed in one pass and two
        /// handler fields describing the same window cannot charge it twice.
        /// </summary>
        public static List<DowntimeWindow> Merge(IEnumerable<DowntimeWindow> windows)
        {
            var clean = new List<DowntimeWindow>();
            foreach (var window in windows ?? Array.Empty<DowntimeWindow>())
            {
                if (double.IsNaN(window.Start) || double.IsNaN(window.End) || window.End <= window.Start) continue;
                clean.Add(window);
            }
            clean.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<DowntimeWindow>();
            foreach (var window in clean)
            {
                if (merged.Count > 0 && window.Start <= merged[merged.Count - 1].End)
                {
                    var last = merged[merged.Count - 1];
                    last.End = Math.Max(last.End, window.End);
                    merged[merged.Count - 1] = last;
                    continue;
                }
                merged.Add(window);
            }
            return merged;
        }

        /// <summary>How much of [from, to) nothing could be hit. Windows must already be merged.</summary>
        public static double Between(IReadOnlyList<DowntimeWindow> merged, double fromMs, double toMs)
        {
            if (merged == null || merged.Count == 0 || !(toMs > fromMs)) return 0;
            double total = 0;
            foreach (var window in merged)
            {
                if (window.End <= fromMs) continue;
                if (window.Start >= toMs) break;
                total += Math.Min(window.End, toMs) - Math.Max(window.Start, fromMs);
            }
            return total;
        }
    }
}
