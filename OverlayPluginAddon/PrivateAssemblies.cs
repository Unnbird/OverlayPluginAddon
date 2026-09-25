using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

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
    /// What gets loaded is a copy, never the shipped file. LoadFrom holds a managed dll open for as
    /// long as the process lives and LoadLibrary maps the native V8 the same way, while
    /// OverlayPlugin's updater replaces an addon by deleting each file and moving the new one in -
    /// with ACT still running. A shipped ClearScript.Core.dll that is loaded fails that delete with
    /// UnauthorizedAccessException and the whole update with it. So the shipped set sits in lib/,
    /// is copied once per version into .shadow/{fingerprint}/, and loads from there; lib/ stays
    /// free for the updater to overwrite.
    ///
    /// Deliberately free of any reference to ACT or OverlayPlugin, so it loads and can be exercised
    /// on its own - which is the only way to test the case it exists for.
    /// </summary>
    public static class PrivateAssemblies
    {
        /// <summary>Where the package puts everything we ship besides our own dll.</summary>
        public const string LibDirectoryName = "lib";

        /// <summary>Where the copies that actually get loaded go, under the plugin directory.</summary>
        public const string ShadowDirectoryName = ".shadow";

        private const string OwnAssemblyFile = "OverlayPluginAddon.dll";

        private static readonly object gate = new object();
        private static string directory;
        private static bool installed;

        /// <summary>Where the assemblies are loaded from, or null when nobody has said yet.</summary>
        public static string Directory
        {
            get { lock (gate) return directory; }
        }

        /// <summary>
        /// Starts resolving our own assemblies for the addon in <paramref name="pluginDirectory"/>.
        /// Safe to call more than once; the newest directory wins and only one handler is ever added.
        /// </summary>
        public static void ResolveFrom(string pluginDirectory)
        {
            // Outside the lock: the first start after an update copies ~40 MB, and nothing can be
            // waiting on the gate this early anyway.
            var dir = string.IsNullOrEmpty(pluginDirectory) ? null : Prepare(pluginDirectory);

            lock (gate)
            {
                if (dir != null) directory = dir;
                if (installed) return;
                installed = true;
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        // The folder to load from: the shadow copy when one could be made, otherwise the shipped
        // files themselves - which the next update will trip over, but which work, and working
        // beats refusing to.
        private static string Prepare(string pluginDirectory)
        {
            // No lib/ means a build output run straight out of out/, or an install from before
            // lib/ existed. Both keep everything flat beside the dll.
            var lib = Path.Combine(pluginDirectory, LibDirectoryName);
            var source = System.IO.Directory.Exists(lib) ? lib : pluginDirectory;

            string loadFrom;
            try
            {
                loadFrom = ShadowCopy(source, Path.Combine(pluginDirectory, ShadowDirectoryName)) ?? source;
            }
            catch (Exception)
            {
                loadFrom = source;
            }

            if (source == lib) RemoveFlatLeftovers(pluginDirectory, lib);
            return loadFrom;
        }

        private static string ShadowCopy(string source, string shadowRoot)
        {
            var files = new DirectoryInfo(source).GetFiles("*.dll")
                .Where(f => !f.Name.Equals(OwnAssemblyFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0) return null;

            var target = Path.Combine(shadowRoot, Fingerprint(files));
            if (!IsCopyOf(target, files))
            {
                // Filled beside and renamed into place, so a copy cut short by a crash or a full
                // disk never looks finished to the next start.
                var staging = target + ".tmp";
                DeleteQuietly(staging);
                System.IO.Directory.CreateDirectory(staging);
                foreach (var file in files) file.CopyTo(Path.Combine(staging, file.Name), true);

                DeleteQuietly(target);
                System.IO.Directory.Move(staging, target);
            }

            // Copies made for other versions. The one the previous ACT loaded is still locked if
            // that process has not quite exited; it goes on a later start.
            foreach (var stale in new DirectoryInfo(shadowRoot).GetDirectories())
            {
                if (!stale.FullName.Equals(target, StringComparison.OrdinalIgnoreCase))
                    DeleteQuietly(stale.FullName);
            }

            return target;
        }

        // Changes whenever any shipped file does. Size and timestamp rather than content: hashing
        // ~40 MB on every ACT start buys nothing, since the updater rewrites every file it ships.
        private static string Fingerprint(FileInfo[] files)
        {
            var text = new StringBuilder();
            foreach (var file in files)
                text.Append(file.Name.ToLowerInvariant()).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');

            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool IsCopyOf(string target, FileInfo[] files)
        {
            if (!System.IO.Directory.Exists(target)) return false;

            foreach (var file in files)
            {
                var copy = new FileInfo(Path.Combine(target, file.Name));
                if (!copy.Exists || copy.Length != file.Length) return false;
            }
            return true;
        }

        // Updating from a release that shipped everything flat beside the dll leaves those files
        // behind, since the updater only writes what the new archive holds. Nothing loads them any
        // more; they are just 40 MB of confusion about which copy is the real one.
        private static void RemoveFlatLeftovers(string pluginDirectory, string lib)
        {
            try
            {
                foreach (var file in new DirectoryInfo(lib).GetFiles("*.dll"))
                {
                    if (file.Name.Equals(OwnAssemblyFile, StringComparison.OrdinalIgnoreCase)) continue;

                    var leftover = Path.Combine(pluginDirectory, file.Name);
                    try
                    {
                        if (File.Exists(leftover)) File.Delete(leftover);
                    }
                    catch (Exception)
                    {
                        // Still held by the ACT that is on its way out; the next start gets it.
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
            catch (Exception)
            {
            }
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
                // It locks the file, which is why the file is the shadow copy.
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
