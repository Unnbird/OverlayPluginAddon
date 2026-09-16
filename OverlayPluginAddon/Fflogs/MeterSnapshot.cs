using System;
using System.Collections.Generic;

namespace OverlayPluginAddon.Fflogs
{
    /// <summary>
    /// One table row's worth of FFLogs figures, already divided where they are rates.
    ///
    /// The split between a player's own figures and their pets' is deliberate and matches what
    /// mopimopi does with the rows afterwards: it keeps pets as lines of their own and sums them
    /// into the owner. So the owner's damage here is the owner *without* the pets the parser kept
    /// apart, each pet carries its own, and the two add back up to the parser's total. The rDPS
    /// family is the exception - it is the folded total on the owner's row and zero on every pet,
    /// because it is a rate, not something to be summed.
    /// </summary>
    public sealed class PlayerFigures
    {
        public double Damage;
        public HitDetails Hits = new HitDetails();
        public string MaxHitAbility = string.Empty;

        public double Healed;        // FFLogs splits healing and overheal; ACT's "healed" is both
        public double OverHeal;
        public HitDetails HealHits = new HitDetails();
        public string MaxHealAbility = string.Empty;

        public int Deaths;

        /// <summary>
        /// This row's damage and healing per second, divided here rather than by the overlay.
        ///
        /// Own, not folded, unlike the rDPS family below: a pet the parser kept apart carries its
        /// own rate on its own row, so an overlay that sums pet rows into the owner arrives at the
        /// folded rate rDPS is measured on. Rates add the way the amounts behind them do, because
        /// every row on this table is divided by the same clock.
        ///
        /// Dividing here is what makes a solo pull read the same in both columns. An overlay
        /// dividing for itself gets the clock as a string and the damage as a rounded whole, and
        /// neither survives the trip intact - which is the whole difference between rDPS and DPS
        /// on a pull where, nobody being there to give or take a buff, they are one number.
        /// </summary>
        public double Dps;
        public double Hps;

        public double Rdps;
        public double Adps;
        public double Ndps;
        public double Cdps;
        public double RdpsDelta;
        public double RdpsPct;

        /// <summary>A pet the parser already folded into its owner, or one it never saw: every
        /// figure is zero so mopimopi's merge() cannot add it on top of the owner's total.</summary>
        public static readonly PlayerFigures Zero = new PlayerFigures();
    }

    /// <summary>
    /// Everything the export formatters read, built once per collect and then never changed.
    ///
    /// The formatters run on OverlayPlugin's Parallel.ForEach over allies while the parser thread
    /// is still working, so publication is a single reference swap and readers never see a half-
    /// built table.
    ///
    /// This is the .NET half of what mopimopi's js/fflogs/apply.js used to do in the page: match the
    /// parser's rows to ACT's combatants by name, give pets their own share, and leave the rows
    /// FFLogs does not know to ACT.
    /// </summary>
    public sealed class MeterSnapshot
    {
        /// <summary>The snapshot handed out before the parser has anything to say.</summary>
        public static readonly MeterSnapshot Empty = new MeterSnapshot();

        private readonly Dictionary<string, ActorRow> damageByName = new Dictionary<string, ActorRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, ActorRow> healingByName = new Dictionary<string, ActorRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, PlayerFigures> rows = new Dictionary<string, PlayerFigures>(StringComparer.Ordinal);

        /// <summary>Whether the parser's fight is the pull ACT is reporting. False means every
        /// column stays ACT's.</summary>
        public bool Applied { get; private set; }

        public string Reason { get; private set; } = "no fight yet";
        public long FightId { get; private set; }
        public string FightState { get; private set; } = string.Empty;
        public FightClocks Clocks { get; private set; }
        public bool HasHealing { get; private set; }
        public bool HasDeaths { get; private set; }

        /// <summary>The whole table's damage, healing and rDPS, for the encounter columns.</summary>
        public double EncounterDamage { get; private set; }
        public double EncounterHealed { get; private set; }
        public double EncounterRdpsAmount { get; private set; }

        /// <summary>Every row FFLogs knows about, by the name it knows them as. For diagnostics and
        /// replays; the export formatters look rows up by name instead.</summary>
        public IEnumerable<string> Names => rows.Keys;

        /// <summary>The downtime windows this fight currently has, merged. Handed to the GCD tracker.</summary>
        public IReadOnlyList<DowntimeWindow> Downtime { get; private set; } = Array.Empty<DowntimeWindow>();

        private MeterSnapshot() { }

        /// <summary>
        /// Builds the table from one fight.
        ///
        /// <paramref name="applied"/> is the caller's decision about whether this fight and ACT's
        /// encounter are the same pull (see <see cref="FightMatch"/>). A snapshot that is not
        /// applied still carries the downtime windows - the GCD columns are measured against the
        /// player's own presses and do not care which encounter ACT thinks is running.
        /// </summary>
        public static MeterSnapshot Build(FightFigures fight, bool applied, string reason,
            IReadOnlyList<DowntimeWindow> downtime)
        {
            var snapshot = new MeterSnapshot
            {
                Applied = applied,
                Reason = reason ?? string.Empty,
                Downtime = downtime ?? (IReadOnlyList<DowntimeWindow>)Array.Empty<DowntimeWindow>(),
            };
            if (fight == null) return snapshot;

            snapshot.FightId = fight.Id;
            snapshot.FightState = fight.State;
            snapshot.HasHealing = fight.HasHealing;
            snapshot.HasDeaths = fight.HasDeaths;
            snapshot.Clocks = FightMatch.Clocks(fight);

            foreach (var row in fight.Damage) snapshot.damageByName[row.Name] = row;
            foreach (var row in fight.Healing) snapshot.healingByName[row.Name] = row;

            // The raid's totals, over the same table every row's share is a share of. Rows ACT knows
            // and FFLogs does not contribute nothing here, so rdpsPct across this table is exactly
            // 100% - which is what it is a share of.
            foreach (var row in fight.Damage)
            {
                snapshot.EncounterDamage += row.Amount;
                snapshot.EncounterRdpsAmount += row.Amount - row.AmountTaken + row.AmountGiven;
            }
            foreach (var row in fight.Healing) snapshot.EncounterHealed += row.Amount + row.Over;

            var divisor = snapshot.Clocks.Active > 0 ? snapshot.Clocks.Active : 1;
            var healDivisor = snapshot.Clocks.Seconds > 0 ? snapshot.Clocks.Seconds : 1;
            var rdpsTotal = snapshot.EncounterRdpsAmount;

            foreach (var row in fight.Damage)
            {
                var healing = snapshot.healingByName.TryGetValue(row.Name, out var h) ? h : null;
                fight.Deaths.TryGetValue(row.Name, out var deaths);

                // The owner's own figures, without the pets the parser kept apart: mopimopi adds
                // those back from the pet rows below.
                var figures = new PlayerFigures
                {
                    Damage = row.Own.Amount,
                    Hits = row.Own.Hits,
                    MaxHitAbility = row.Own.MaxHitAbility,
                    Deaths = deaths,

                    // Own, matching the damage above it: the pet rows carry the rest.
                    Dps = row.Own.Amount / divisor,

                    // The rDPS family is the folded total - own plus every pet - because it is a
                    // rate on the player, not a quantity to be summed across their lines.
                    Rdps = (row.Amount - row.AmountTaken + row.AmountGiven) / divisor,
                    Adps = (row.Amount - row.SingleTargetAmountTaken) / divisor,
                    Ndps = (row.Amount - row.AmountTaken) / divisor,
                    Cdps = (row.Amount - row.SingleTargetAmountTaken + row.AmountGiven) / divisor,
                    RdpsDelta = (row.AmountGiven - row.AmountTaken) / divisor,
                    RdpsPct = rdpsTotal > 0 ? (row.Amount - row.AmountTaken + row.AmountGiven) / rdpsTotal * 100 : 0,
                };

                if (healing != null)
                {
                    figures.Healed = healing.Own.Amount + healing.Own.Over;
                    figures.OverHeal = healing.Own.Over;
                    figures.HealHits = healing.Own.Hits;
                    figures.MaxHealAbility = healing.Own.MaxHitAbility;
                    figures.Hps = figures.Healed / healDivisor;
                }

                snapshot.rows[row.Name] = figures;
            }

            // A healer who dealt no damage at all still has a row to fill.
            foreach (var row in fight.Healing)
            {
                if (snapshot.rows.ContainsKey(row.Name)) continue;
                fight.Deaths.TryGetValue(row.Name, out var deaths);
                snapshot.rows[row.Name] = new PlayerFigures
                {
                    Healed = row.Own.Amount + row.Own.Over,
                    OverHeal = row.Own.Over,
                    HealHits = row.Own.Hits,
                    MaxHealAbility = row.Own.MaxHitAbility,
                    Deaths = deaths,
                    Hps = (row.Own.Amount + row.Own.Over) / healDivisor,
                };
            }

            return snapshot;
        }

        /// <summary>
        /// The figures for one row of ACT's table, by the name ACT calls it - already resolved, so
        /// "YOU" has become the logging player's real name.
        ///
        /// Null means FFLogs has nothing for this row (an NPC, a name the two sides spell
        /// differently) and ACT's own figures should stand. That is not the same as
        /// <see cref="PlayerFigures.Zero"/>, which is a pet the parser has already counted under its
        /// owner and which must therefore contribute nothing.
        /// </summary>
        public PlayerFigures Lookup(string actName)
        {
            if (!Applied || string.IsNullOrEmpty(actName)) return null;

            var pet = SplitPetName(actName);
            if (pet == null) return rows.TryGetValue(actName, out var own) ? own : null;

            // A pet row: the parser either kept this pet apart (Demi-Bahamut and friends, found
            // under the owner's pets) or folded it into the owner already (Carbuncle, Eos...).
            // Either way the owner's row carries the total, so this row gets the pet's own share or
            // nothing - never ACT's figure, which mopimopi's merge() would add on top.
            var hasOwner = damageByName.TryGetValue(pet.Value.Owner, out var ownerDamage);
            var hasOwnerHealing = healingByName.TryGetValue(pet.Value.Owner, out var ownerHealing);
            if (!hasOwner && !hasOwnerHealing) return null;

            var damagePet = ownerDamage?.PetNamed(pet.Value.Base);
            var healingPet = ownerHealing?.PetNamed(pet.Value.Base);
            if (damagePet == null && healingPet == null) return PlayerFigures.Zero;

            var figures = new PlayerFigures();
            if (damagePet != null)
            {
                figures.Damage = damagePet.Amount;
                figures.Hits = damagePet.Hits;
                figures.MaxHitAbility = damagePet.MaxHitAbility;
            }

            if (healingPet != null)
            {
                figures.Healed = healingPet.Amount + healingPet.Over;
                figures.OverHeal = healingPet.Over;
                figures.HealHits = healingPet.Hits;
                figures.MaxHealAbility = healingPet.MaxHitAbility;
            }

            // The pet's own share of the rates, against the same two clocks its owner's row used.
            // Summed back into the owner they come to the folded rate, which is what rDPS is.
            figures.Dps = figures.Damage / (Clocks.Active > 0 ? Clocks.Active : 1);
            figures.Hps = figures.Healed / (Clocks.Seconds > 0 ? Clocks.Seconds : 1);

            return figures;
        }

        private struct PetName
        {
            public string Base;
            public string Owner;
        }

        /// <summary>"Eos (Owner Name)" -> Eos + Owner Name; null for a plain name.</summary>
        private static PetName? SplitPetName(string name)
        {
            if (string.IsNullOrEmpty(name) || name[name.Length - 1] != ')') return null;
            var open = name.IndexOf(" (", StringComparison.Ordinal);
            if (open <= 0) return null;
            var owner = name.Substring(open + 2, name.Length - open - 3);
            return owner.Length == 0 ? (PetName?)null : new PetName { Base = name.Substring(0, open), Owner = owner };
        }
    }
}
