using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OverlayPluginAddon
{
    /// <summary>
    /// Tells a GCD (weaponskill or spell) apart from an oGCD (ability).
    ///
    /// The mapping comes out of FFXIV_ACT_Plugin.Resource's embedded ActionCategoryList, read from
    /// the assembly already loaded in the ACT process rather than from a file we ship. That keeps
    /// it in lockstep with whatever parser build the user is actually running - a new job or a
    /// reworked action shows up the moment they update the plugin - and avoids shipping a 33,000
    /// entry table that would go stale every patch.
    ///
    /// Category numbers are FFXIV's own, confirmed against known actions:
    ///   1 auto-attack, 2 spell (GCD), 3 weaponskill (GCD), 4 ability (oGCD).
    /// </summary>
    public class ActionCategories
    {
        private const string ResourceAssembly = "FFXIV_ACT_Plugin.Resource";

        private const string CategorySpell = "2";
        private const string CategoryWeaponskill = "3";

        private readonly HashSet<uint> gcdActions = new HashSet<uint>();
        private readonly HashSet<uint> spellActions = new HashSet<uint>();

        public int Count => gcdActions.Count;

        /// <summary>Null when the table loaded cleanly, otherwise why it did not.</summary>
        public string LoadError { get; private set; }

        public bool Available => LoadError == null && gcdActions.Count > 0;

        public bool IsGcd(uint actionId) => gcdActions.Contains(actionId);

        /// <summary>
        /// True for a spell, false for a weaponskill. Decides which speed stat governs an action
        /// that actions.json says nothing about - guessing skill speed for every unknown pools a
        /// caster's whole rotation into the wrong distribution.
        /// </summary>
        public bool IsSpell(uint actionId) => spellActions.Contains(actionId);

        /// <summary>
        /// Finds the parser's resource assembly, loading it if it has not been needed yet.
        ///
        /// FFXIV_ACT_Plugin embeds this assembly with Costura, which decompresses it lazily from an
        /// AssemblyResolve hook the first time something asks for it. Scanning the AppDomain alone
        /// therefore fails whenever this addon initialises before the parser has had cause to look
        /// up an action name - which is the normal case at ACT startup. Asking for it by name is
        /// what makes Costura hand it over.
        /// </summary>
        private static Assembly ResolveResourceAssembly(ActionCategories table)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == ResourceAssembly);
            if (loaded != null) return loaded;

            try
            {
                return Assembly.Load(new AssemblyName(ResourceAssembly));
            }
            catch (Exception ex)
            {
                table.LoadError = ResourceAssembly + " could not be loaded (" + ex.GetType().Name
                    + "). Is FFXIV_ACT_Plugin enabled and ordered before this addon?";
                return null;
            }
        }

        public static ActionCategories Load()
        {
            var table = new ActionCategories();

            try
            {
                var asm = ResolveResourceAssembly(table);
                if (asm == null) return table;

                // Be tolerant about the exact resource name: it has carried a "Generated." segment
                // for a while, but the addon should not stop working over a rename.
                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.IndexOf("ActionCategoryList", StringComparison.OrdinalIgnoreCase) >= 0);

                if (resourceName == null)
                {
                    table.LoadError = "no ActionCategoryList resource in " + ResourceAssembly;
                    return table;
                }

                using (var stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        table.LoadError = "resource " + resourceName + " could not be opened";
                        return table;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            // "<action id in hex>|<category>"
                            var bar = line.IndexOf('|');
                            if (bar <= 0 || bar == line.Length - 1) continue;

                            var category = line.Substring(bar + 1);
                            if (category != CategorySpell && category != CategoryWeaponskill) continue;

                            if (!uint.TryParse(line.Substring(0, bar), NumberStyles.HexNumber,
                                               CultureInfo.InvariantCulture, out var id)) continue;

                            table.gcdActions.Add(id);
                            if (category == CategorySpell) table.spellActions.Add(id);
                        }
                    }
                }

                if (table.gcdActions.Count == 0)
                    table.LoadError = "ActionCategoryList parsed but held no weaponskills or spells";
            }
            catch (Exception ex)
            {
                table.LoadError = ex.Message;
            }

            return table;
        }
    }
}
