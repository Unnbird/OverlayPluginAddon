using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.Updater;

namespace OverlayPluginAddon
{
    public class GcdOverlayPreset : IOverlayPreset
    {
        public string Name { get; set; }
        public string Type { get; set; } = "MiniParse";
        public string Url { get; set; }
        public int[] Size { get; set; } = new int[] { 600, 500 };
        public bool Locked { get; set; } = false;
        public List<string> Supports { get; set; } = new List<string> { "actws" };
    }

    /// <summary>
    /// The ACT plugin shell: finds OverlayPlugin, starts <see cref="GcdEventSource"/>, registers
    /// the export columns, the mopimopi preset that renders them, and the auto-updater.
    /// </summary>
    public class GcdOverlayAddon : IActPluginV1, IOverlayAddonV2
    {
        /// <summary>GitHub repository the auto-updater watches. Releases must be tagged v{VERSION}
        /// and carry OverlayPluginAddon-{VERSION}.zip.</summary>
        private const string UpdateRepo = "Unnbird/OverlayPluginAddon";

        public static string PluginPath { get; private set; } = string.Empty;

        private GcdEventSource eventSource;
        private bool isInitialized;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            pluginStatusText.Text = "OverlayPluginAddon (GCD uptime) Plugin Ready.";

            // No settings UI - the recast table is a file and everything else is diagnostics.
            if (pluginScreenSpace?.Parent is TabControl parentTab)
                parentTab.TabPages.Remove(pluginScreenSpace);

            foreach (var plugin in ActGlobals.oFormActMain.ActPlugins)
            {
                if (plugin.pluginObj == this)
                {
                    PluginPath = plugin.pluginFile.FullName;
                    break;
                }
            }

            // During ACT boot OverlayPlugin has not finished its second init phase yet, so
            // resolving services here would either throw or make TinyIoC build throwaway
            // duplicates. OverlayPlugin calls IOverlayAddonV2.Init() at the right moment instead.
            // If it is already up we were enabled by hand later on and LoadAddons() will not run
            // again, so we have to start ourselves.
            if (IsOverlayPluginReady())
                Init();
        }

        private static bool IsOverlayPluginReady()
        {
            try
            {
                var container = Registry.GetContainer();
                return container != null && container.CanResolve<Registry>();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Init()
        {
            if (isInitialized) return;

            try
            {
                var container = Registry.GetContainer();
                var registry = container?.Resolve<Registry>();
                if (registry == null) return;

                eventSource = new GcdEventSource(container);

                // Order matters: the export variables have to exist before MiniParse builds its
                // next CombatData payload, and StartEventSource is what begins feeding the tracker.
                eventSource.RegisterExportVariables();
                registry.StartEventSource(eventSource);

                RegisterPresets(registry);
                CheckForUpdates(container);

                isInitialized = true;
            }
            catch (Exception ex)
            {
                ActGlobals.oFormActMain?.WriteExceptionLog(ex, "OverlayPluginAddonInitError");
            }
        }

        // Where the dll and its data/ live. ACT gives us the real path in InitPlugin; the assembly
        // location is the fallback for the case where we never found ourselves in ActPlugins.
        public static string PluginDirectory()
        {
            try
            {
                if (!string.IsNullOrEmpty(PluginPath))
                    return Path.GetDirectoryName(PluginPath);

                return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// A file under data/. The packaged layout puts data/ beside the dll; the source tree has
        /// it one level up, which is where a Debug build run straight out of out/ finds it.
        /// </summary>
        public static string ResolveDataPath(string fileName)
        {
            var dir = PluginDirectory();
            if (dir == null) return fileName;

            var candidate = Path.Combine(dir, "data", fileName);
            if (File.Exists(candidate)) return candidate;

            return Path.GetFullPath(Path.Combine(dir, "..", "data", fileName));
        }

        // GitHub answers /releases/latest with a 404 *body* when a repository has no release yet -
        // and the same for one that does not exist, and for a spent unauthenticated rate limit.
        // HttpClientWrapper.Get returns that body instead of throwing, so Updater's own check
        // hands it to JObject.Parse, dereferences the tag_name that is not there, and dies on a
        // NullReferenceException - which it reports in a modal error box over the game, on every
        // single ACT start. Look first, and say nothing when there is nothing to update to.
        private static bool HasPublishedRelease(string repo)
        {
            try
            {
                var body = HttpClientWrapper.Get("https://api.github.com/repos/" + repo + "/releases/latest");
                var tag = JObject.Parse(body)["tag_name"];
                if (tag == null) return false;

                // Same shape RunAutoUpdater will parse a moment later: it does Substring(1) on the
                // tag and Version.Parse on the rest, so a tag that is not v1.2.3 throws there too.
                return Version.TryParse(tag.ToString().TrimStart('v'), out _);
            }
            catch (Exception)
            {
                // Offline, DNS down, a captive portal: none of that is worth interrupting anyone
                // over. The next ACT start asks again.
                return false;
            }
        }

        // Same auto-updater OverlayPlugin ships for its addons - an addon only has to say where its
        // releases are. RunAutoUpdater reads the latest tag off the GitHub API and, when it is
        // newer than this assembly, shows the release notes and asks before downloading. It is
        // fire-and-forget on purpose: Init() runs on OverlayPlugin's addon-loading thread and a
        // network round trip there would hold up the rest of ACT's startup.
        private static async void CheckForUpdates(TinyIoCContainer container)
        {
            try
            {
                var pluginDir = PluginDirectory();
                if (string.IsNullOrEmpty(pluginDir)) return;

                var options = new UpdaterOptions
                {
                    project = "OverlayPluginAddon",
                    pluginDirectory = pluginDir,
                    // Nothing persists lastCheck between ACT sessions, so the interval never
                    // suppresses anything; what limits this to one check per session is Init()'s
                    // isInitialized guard.
                    lastCheck = DateTime.MinValue,
                    checkInterval = TimeSpan.FromHours(1),
                    currentVersion = Assembly.GetExecutingAssembly().GetName().Version,
                    repo = UpdateRepo,
                    downloadUrl = "https://github.com/{REPO}/releases/download/v{VERSION}/OverlayPluginAddon-{VERSION}.zip",
                    // build.ps1 packs everything under a single OverlayPluginAddon/ folder, so one
                    // level comes off and OverlayPluginAddon.dll plus data/ land straight in pluginDir.
                    strippedDirs = 1,
                };

                // HttpClientWrapper refuses to run on the UI thread, so both calls go to the pool.
                if (!await Task.Run(() => HasPublishedRelease(options.repo))) return;

                // The container is already resolved here, so use the overload that takes it rather
                // than the one that goes hunting through ActPlugins for OverlayPlugin and throws
                // when it cannot find it.
                await Task.Run(() => Updater.RunAutoUpdater(options, container, false));
            }
            catch (Exception ex)
            {
                ActGlobals.oFormActMain?.WriteExceptionLog(ex, "OverlayPluginAddonCheckUpdateError");
            }
        }

        // The stock MopiMopi preset points at haeruhaeru's build, which has no idea what the GCD
        // columns are. This one is the fork that renders them. Presets registered from an addon's
        // Init() land before the New Overlay dialog builds its combo box, so it shows up there
        // alongside the ones OverlayPlugin ships in resources/presets.json.
        private static void RegisterPresets(Registry registry)
        {
            try
            {
                // Supports = ["actws"] alone is what makes OverlayPlugin turn on ActwsCompatibility
                // for the new overlay, which mopimopi needs - it talks the old ACTWebSocket
                // protocol, not the modern one. The HOST_PORT parameter is spelled out because
                // that is the placeholder OverlayPlugin substitutes the real socket address into;
                // left off, MiniParseOverlay.Navigate() appends this exact value itself.
                RegisterPreset(registry, "MopiMopiCustom", "https://unnbird.github.io/mopimopi/?HOST_PORT=ws://127.0.0.1/fake/");
            }
            catch (Exception ex)
            {
                ActGlobals.oFormActMain?.WriteExceptionLog(ex, "OverlayPluginAddonRegisterPresetsError");
            }
        }

        private static void RegisterPreset(Registry registry, string presetName, string url)
        {
            // The registry keeps every preset ever handed to it and the New Overlay dialog lists
            // them by name, so a second entry under a name OverlayPlugin's presets.json (or
            // another addon, RdpsOverlay included) already claimed would just show up twice.
            if (registry.OverlayPresets.Any(p => p.Name == presetName)) return;

            registry.RegisterOverlayPreset2(new GcdOverlayPreset
            {
                Name = presetName,
                Url = url,
            });
        }

        public void DeInitPlugin()
        {
            try
            {
                eventSource?.Stop();
            }
            catch (Exception ex)
            {
                ActGlobals.oFormActMain?.WriteExceptionLog(ex, "OverlayPluginAddonDeInitError");
            }
        }
    }
}
