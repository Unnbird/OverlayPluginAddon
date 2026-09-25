using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace OverlayPluginAddon.Fflogs
{
    /// <summary>
    /// Runs FFLogs' own log parser (data/parser-ff.js) inside the addon, on a thread of its own.
    ///
    /// The parser is the one piece that stays JavaScript. It is proprietary, obfuscated and
    /// reissued by RPGLogs on their schedule; porting it would mean reimplementing FFLogs' rDPS
    /// accounting and re-deriving it after every patch, which is the thing this whole addon exists
    /// to avoid. Everything downstream of it - folding the actor table, matching rows to ACT's
    /// combatants, the two clocks, GCD uptime - is .NET.
    ///
    /// Threading: a V8ScriptEngine belongs to one thread. ACT's log-read thread only enqueues
    /// strings (<see cref="Feed"/>); this class's own worker drains the queue into the parser and
    /// collects meters off it, so nothing the parser does can slow the log loop down.
    /// </summary>
    public sealed class ParserHost : IDisposable
    {
        /// <summary>
        /// Which build of FFLogs' parser data/parser-ff.js is. Their own constant for it, recorded
        /// in data/PARSER-VERSION.md along with where the file came from; update both together.
        /// </summary>
        public const string ParserVersion = "3075";

        /// <summary>How often the queue is handed to the parser. The parser wants batches, not lines.</summary>
        private const int ParseIntervalMs = 100;

        /// <summary>How often the meters are read back out. Overlays update about this often.</summary>
        private const int CollectIntervalMs = 500;

        /// <summary>
        /// The smallest browser the parser will start in. It is a page script: it hangs its class
        /// off <c>window</c>, reports through four globals, and a bundled entropy collector pokes
        /// at <c>document</c> and <c>window.crypto</c> behind feature checks. Nothing here is
        /// parser logic - it is the environment, and it matches the one mopimopi's replay tool
        /// gives the same file under Node.
        /// </summary>
        private const string Shim = @"
var window = globalThis;
var self = globalThis;
window.document = {
  attachEvent: function () {}, detachEvent: function () {},
  addEventListener: function () {}, removeEventListener: function () {}, dispatchEvent: function () {}
};
window.navigator = { userAgent: 'OverlayPluginAddon' };
window.location = { search: '', href: '' };
window.addEventListener = function () {};
window.removeEventListener = function () {};
if (typeof window.performance === 'undefined') window.performance = { now: function () { return Date.now(); } };
if (typeof window.setTimeout === 'undefined') { window.setTimeout = function () { return 0; }; window.clearTimeout = function () {}; }
if (typeof window.setInterval === 'undefined') { window.setInterval = function () { return 0; }; window.clearInterval = function () {}; }
window.sendLogMessage = function (m) { __host.Info(String(m)); };
window.sendEventMessage = function () {};
window.setWarningText = function (t) { __host.Warn(String(t)); };
window.setErrorText = function (t) { __host.Error(String(t)); };
";

        /// <summary>What the shim's four report globals reach. Kept tiny on purpose: it is the
        /// only .NET surface the script can touch.</summary>
        public sealed class ScriptBridge
        {
            private readonly Action<string, string> sink;
            public ScriptBridge(Action<string, string> sink) { this.sink = sink; }
            public void Info(string message) { sink("info", message); }
            public void Warn(string message) { sink("warn", message); }
            public void Error(string message) { sink("error", message); }
        }

        private readonly object queueGate = new object();
        private List<string> queue = new List<string>();

        private readonly Action<string, string> log;
        private readonly string parserPath;

        /// <summary>Where V8's native DLL lives, when it is not simply beside ClearScript.V8.dll.</summary>
        private readonly string nativeSearchPath;

        private Thread worker;
        private volatile bool running;
        private ManualResetEventSlim stopSignal;

        private V8ScriptEngine engine;
        private ScriptObject parser;
        private ScriptObject emptyArray;
        private ScriptObject fileInfo;

        /// <summary>Byte position the parser is told the log file is at. It only wants a running total.</summary>
        private long position;

        /// <summary>Timestamp of the newest line parsed, in log time. The page's "now".</summary>
        public long LastLineMs { get; private set; } = long.MinValue;

        public string LastError { get; private set; } = string.Empty;
        public string ParserWarning { get; private set; } = string.Empty;
        public long LinesParsed { get; private set; }
        public long LineErrors { get; private set; }
        public long Collections { get; private set; }
        public bool Available => running && parser != null;

        /// <summary>Lines waiting to be handed to the parser. A replay uses it to know when the
        /// engine has caught up with the file.</summary>
        public int QueueLength { get { lock (queueGate) return queue.Count; } }

        /// <summary>
        /// Raised on the worker thread after each collectMeters(), with the parser's live output.
        /// The handler runs inside the engine's thread, so it may read script objects directly;
        /// it must not keep them past the call.
        /// </summary>
        public event Action<ParserCollection> Collected;

        public ParserHost(string parserPath, Action<string, string> log, string nativeSearchPath = null)
        {
            this.parserPath = parserPath;
            this.log = log ?? ((level, message) => { });
            this.nativeSearchPath = nativeSearchPath;
        }

        /// <param name="liveLoggingStartMs">
        /// Lines older than this are ignored by the parser. Live use wants "now"; a replay wants the
        /// first line of the log.
        /// </param>
        /// <param name="region">FFLogs region id; 1 unless a caller knows better.</param>
        public bool Start(long liveLoggingStartMs, int region = 1)
        {
            if (running) return true;
            if (!File.Exists(parserPath))
            {
                LastError = "parser-ff.js not found at " + parserPath;
                log("error", LastError);
                return false;
            }

            stopSignal = new ManualResetEventSlim(false);
            running = true;
            var started = new ManualResetEventSlim(false);
            var ok = false;

            worker = new Thread(() =>
            {
                try
                {
                    ok = Boot(liveLoggingStartMs, region);
                }
                catch (Exception e)
                {
                    LastError = Describe("boot", e);
                    log("error", LastError);
                    ok = false;
                }
                finally
                {
                    started.Set();
                }

                try
                {
                    if (ok) Loop();
                }
                catch (Exception e)
                {
                    LastError = Describe("parser thread", e);
                    log("error", LastError);
                }
                finally
                {
                    // Teardown touches the engine, so entering it is itself an assembly load.
                    try { Teardown(); } catch (Exception e) { log("error", Describe("teardown", e)); }
                }
            })
            {
                Name = "FflogsParser",
                IsBackground = true,
            };
            worker.Start();

            // The engine takes about a second to come up; the caller wants to know whether it did.
            started.Wait(TimeSpan.FromSeconds(30));
            if (!ok) running = false;
            return ok;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            stopSignal?.Set();
            worker?.Join(TimeSpan.FromSeconds(5));
            worker = null;
        }

        public void Dispose()
        {
            Stop();
            stopSignal?.Dispose();
            stopSignal = null;
        }

        /// <summary>Queues one raw network log line. Called from ACT's log thread; does no work.</summary>
        public void Feed(string line)
        {
            if (!running || string.IsNullOrEmpty(line)) return;
            lock (queueGate) queue.Add(line);
        }

        /// <summary>Throws the queue away - a new log file, or a parser restart.</summary>
        public void ClearQueue()
        {
            lock (queueGate) queue.Clear();
        }

        // ------------------------------------------------------------------ the engine's thread

        private bool Boot(long liveLoggingStartMs, int region)
        {
            var sw = Stopwatch.StartNew();

            // ClearScript looks for ClearScriptV8.win-x64.dll beside its own assembly. That works
            // when PrivateAssemblies handed it over with LoadFrom, and this covers the case where
            // it did not.
            if (!string.IsNullOrEmpty(nativeSearchPath)) HostSettings.AuxiliarySearchPath = nativeSearchPath;

            // EnableDateTimeConversion so setLogStartDate can be handed a DateTime; the parser folds
            // line timestamps onto that date.
            engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableDateTimeConversion);
            engine.AddHostObject("__host", new ScriptBridge(OnScriptMessage));
            engine.Execute("shim", Shim);
            engine.Execute("parser-ff.js", File.ReadAllText(parserPath));

            var ctor = engine.Script.window.LogParser as ScriptObject;
            if (ctor == null)
            {
                LastError = "parser-ff.js loaded but defined no window.LogParser";
                log("error", LastError);
                return false;
            }

            emptyArray = (ScriptObject)engine.Evaluate("[]");
            fileInfo = (ScriptObject)engine.Evaluate("({ filePath: 'live.log', currentPosition: 0, startingPosition: 0 })");

            // LogParser(region, skipOutput, raidsToUpload, disableLineSigning, gameContentDetection,
            //           metersEnabled, gameContentTypes)
            //
            // disableLineSigning is true deliberately. Every network log line carries a signature the
            // parser verifies before committing it, and a line it cannot verify makes it throw. We
            // never upload anything, so the signature buys nothing and only couples us to ACT
            // handing the line over byte for byte.
            //
            // metersEnabled is what makes the parser keep the live rDPS accounting at all
            // (amount / amountTaken / singleTargetAmountTaken / amountGiven per actor).
            parser = (ScriptObject)ctor.Invoke(true, region, false, emptyArray, true, false, true, null);

            var day = DateTime.Now.Date;
            parser.InvokeMethod("setLogStartDate", day);
            parser.InvokeMethod("setLiveLoggingStartTime", (double)liveLoggingStartMs);

            log("info", $"FFLogs parser started in {sw.ElapsedMilliseconds} ms (V8, {new FileInfo(parserPath).Length / 1024} KiB).");
            return true;
        }

        private void Loop()
        {
            var sinceCollect = 0;
            while (running)
            {
                if (stopSignal.Wait(ParseIntervalMs)) break;

                try
                {
                    Drain();
                }
                catch (Exception e)
                {
                    LastError = Describe("drain", e);
                    log("error", LastError);
                }

                sinceCollect += ParseIntervalMs;
                if (sinceCollect < CollectIntervalMs) continue;
                sinceCollect = 0;

                try
                {
                    Collect();
                }
                catch (Exception e)
                {
                    LastError = Describe("collectMeters", e);
                    log("error", LastError);
                }
            }
        }

        private void Drain()
        {
            List<string> batch;
            lock (queueGate)
            {
                if (queue.Count == 0) return;
                batch = queue;
                queue = new List<string>();
            }

            var start = position;
            foreach (var line in batch) position += line.Length + 1;

            fileInfo.SetProperty("startingPosition", (double)start);
            fileInfo.SetProperty("currentPosition", (double)position);
            parser.InvokeMethod("prepareToParseLines", false, 1, emptyArray, fileInfo, false, true, null);

            foreach (var line in batch)
            {
                try
                {
                    parser.InvokeMethod("parseLine", line);
                    LinesParsed++;
                }
                catch (Exception e)
                {
                    LineErrors++;
                    LastError = Describe("parseLine", e);
                }
            }

            // The newest timestamp in the batch is "now" for everything that reasons in log time -
            // an open downtime window runs to it. Read back to front: the first one that parses wins.
            for (var i = batch.Count - 1; i >= 0; i--)
            {
                var ms = TimestampOf(batch[i]);
                if (ms == long.MinValue) continue;
                LastLineMs = ms;
                break;
            }
        }

        private void Collect()
        {
            var meters = parser.InvokeMethod("collectMeters") as ScriptObject;
            Collections++;
            var handler = Collected;
            if (handler == null) return;

            handler(new ParserCollection(
                meters,
                parser.GetProperty("logParserOutput") as ScriptObject,
                ZoneHandler(),
                LastLineMs));
        }

        /// <summary>
        /// The zone handler for the fight in progress, where the downtime windows live. Null
        /// whenever the parser has no fight open - between pulls, and in a zone it has no handler
        /// for.
        /// </summary>
        private ScriptObject ZoneHandler()
        {
            try
            {
                var output = parser.GetProperty("logParserOutput") as ScriptObject;
                var fight = output?.GetProperty("meterFight") as ScriptObject;
                return fight?.GetProperty("zoneHandler") as ScriptObject;
            }
            catch (Exception e)
            {
                LastError = Describe("zoneHandler", e);
                return null;
            }
        }

        private void Teardown()
        {
            parser = null;
            emptyArray = null;
            fileInfo = null;
            try { engine?.Dispose(); } catch { /* shutting down anyway */ }
            engine = null;
        }

        // ------------------------------------------------------------------ helpers

        private void OnScriptMessage(string level, string message)
        {
            switch (level)
            {
                case "warn": ParserWarning = message; break;
                case "error": LastError = "parser: " + message; break;
            }
            log(level, "parser: " + message);
        }

        /// <summary>
        /// Milliseconds of a log line's own timestamp, which is its second field.
        /// long.MinValue when the line has none that parses.
        /// </summary>
        public static long TimestampOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return long.MinValue;
            var bar = line.IndexOf('|');
            if (bar < 0) return long.MinValue;
            var next = line.IndexOf('|', bar + 1);
            var text = next < 0 ? line.Substring(bar + 1) : line.Substring(bar + 1, next - bar - 1);
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                return long.MinValue;
            return when.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// A ClearScript failure carries the JavaScript stack in ErrorDetails and next to nothing in
        /// Message, so an unqualified ToString() on one reads "Error encountered." and no more.
        ///
        /// ErrorDetails is read by reflection rather than through a cast, because the exception this
        /// has to describe may be the one saying ClearScript could not be loaded at all - and naming
        /// the type would send the loader after the same missing assembly a second time, from inside
        /// a catch block, on a thread with nobody above it to catch anything. That took ACT down.
        /// </summary>
        private static string Describe(string what, Exception e)
        {
            if (e == null) return what;
            string details = null;
            try
            {
                if (e.GetType().Name == "ScriptEngineException")
                    details = e.GetType().GetProperty("ErrorDetails")?.GetValue(e, null) as string;
            }
            catch (Exception)
            {
                // Describing a failure must never be able to fail.
            }
            return what + ": " + (string.IsNullOrEmpty(details) ? e.Message : details);
        }
    }

    /// <summary>One collectMeters() result, as the parser still holds it. Valid only for the
    /// duration of the <see cref="ParserHost.Collected"/> call that carried it.</summary>
    public sealed class ParserCollection
    {
        public ParserCollection(ScriptObject meters, ScriptObject output, ScriptObject zoneHandler, long lastLineMs)
        {
            Meters = meters;
            Output = output;
            ZoneHandler = zoneHandler;
            LastLineMs = lastLineMs;
        }

        public ScriptObject Meters { get; }

        /// <summary>The parser's own output tables - where the pet bookkeeping lives.</summary>
        public ScriptObject Output { get; }

        public ScriptObject ZoneHandler { get; }
        public long LastLineMs { get; }
    }
}
