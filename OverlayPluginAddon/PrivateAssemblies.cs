using System;
using System.IO;
using System.Reflection;

namespace OverlayPluginAddon
{
    /// <summary>
    /// Hands the CLR the assemblies this addon ships beside itself.
    ///
    /// ACT loads a plugin from bytes it read itself, so the runtime never learns which folder the
    /// dll came from and will not look there for anything it depends on. Every other addon gets
    /// away with that because everything it uses - ACT's own types, OverlayPlugin's, Newtonsoft -
    /// is already loaded by the time the addon runs. ClearScript is ours alone, and without this
    /// the first line of JavaScript throws FileNotFoundException on the parser's own thread, where
    /// an escaping exception takes the whole of ACT down with it.
    ///
    /// Deliberately free of any reference to ACT or OverlayPlugin, so it loads and can be exercised
    /// on its own - which is the only way to test the case it exists for.
    /// </summary>
    public static class PrivateAssemblies
    {
        private static readonly object gate = new object();
        private static string directory;
        private static bool installed;

        /// <summary>Where the shipped assemblies are, or null when nobody has said yet.</summary>
        public static string Directory
        {
            get { lock (gate) return directory; }
        }

        /// <summary>
        /// Starts resolving our own assemblies out of <paramref name="pluginDirectory"/>. Safe to
        /// call more than once; the newest directory wins and only one handler is ever added.
        /// </summary>
        public static void ResolveFrom(string pluginDirectory)
        {
            lock (gate)
            {
                if (!string.IsNullOrEmpty(pluginDirectory)) directory = pluginDirectory;
                if (installed) return;
                installed = true;
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                // A plain split rather than AssemblyName: constructing one inside a resolve handler
                // can itself trigger a resolve, and that recursion ends in a stack overflow with no
                // exception to catch.
                var name = args.Name.Split(',')[0].Trim();
                if (name.Length == 0 || name.EndsWith(".resources", StringComparison.Ordinal)) return null;

                var dir = Directory;
                if (string.IsNullOrEmpty(dir)) return null;

                // What we ship is the whole answer: the file is here only because the build put it
                // here, and this event only fires when nothing else could satisfy the load. ACT's
                // own assemblies and OverlayPlugin's are already loaded by now and never reach us.
                var path = Path.Combine(dir, name + ".dll");
                if (!File.Exists(path)) return null;

                // LoadFrom rather than the bytes: ClearScript finds its native V8 by looking beside
                // its own assembly, and one loaded from a byte array has no location to look beside.
                return Assembly.LoadFrom(path);
            }
            catch (Exception)
            {
                // A resolver that throws fails the load it was asked to rescue, and does it from
                // wherever that load happened to be triggered - which for us is a worker thread.
                return null;
            }
        }
    }
}
