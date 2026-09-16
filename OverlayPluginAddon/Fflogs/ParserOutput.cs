using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.ClearScript;

namespace OverlayPluginAddon.Fflogs
{
    /// <summary>One actor's hit summary, as the parser books it.</summary>
    public sealed class HitDetails
    {
        public long HitCount;
        public long CriticalCount;
        public long DirectHitCount;
        public long CriticalDirectHitCount;
        public double MaxHit;
        public double MinHit;

        public void Merge(HitDetails other)
        {
            HitCount += other.HitCount;
            CriticalCount += other.CriticalCount;
            DirectHitCount += other.DirectHitCount;
            CriticalDirectHitCount += other.CriticalDirectHitCount;
            MaxHit = Math.Max(MaxHit, other.MaxHit);
            if (other.MinHit > 0) MinHit = MinHit > 0 ? Math.Min(MinHit, other.MinHit) : other.MinHit;
        }
    }

    /// <summary>One actor's own figures - no pets folded in.</summary>
    public sealed class ActorFigures
    {
        public long Id;
        public string Name = string.Empty;
        public string FullType = string.Empty;
        public double Amount;
        public double AmountTaken;
        public double SingleTargetAmountTaken;
        public double AmountGiven;
        public double Over;
        public HitDetails Hits = new HitDetails();

        /// <summary>The ability that dealt <see cref="HitDetails.MaxHit"/>; empty when none matched.</summary>
        public string MaxHitAbility = string.Empty;
    }

    /// <summary>
    /// One player: their own figures plus every pet the parser kept apart, and the totals of both.
    /// </summary>
    public sealed class ActorRow
    {
        public long Id;
        public string Name = string.Empty;
        public string FullType = string.Empty;
        public double Amount;
        public double AmountTaken;
        public double SingleTargetAmountTaken;
        public double AmountGiven;
        public double Over;
        public HitDetails Hits = new HitDetails();

        /// <summary>The player without their kept-apart pets. Never null once reading is done.</summary>
        public ActorFigures Own = new ActorFigures();

        /// <summary>The pets the parser booked separately, one entry each.</summary>
        public readonly List<ActorFigures> Pets = new List<ActorFigures>();

        public ActorFigures PetNamed(string baseName)
        {
            foreach (var pet in Pets)
                if (string.Equals(pet.Name, baseName, StringComparison.Ordinal)) return pet;
            return null;
        }
    }

    /// <summary>One fight, converted out of the parser and safe to keep.</summary>
    public sealed class FightFigures
    {
        public long Id;
        public string State = string.Empty;
        public double StartTimeMs;
        public double EndTimeMs;
        public string Zone = string.Empty;

        /// <summary>Seconds of the fight when nothing could be hit, as the parser's own handler counts them.</summary>
        public double DowntimeSeconds;

        public readonly List<ActorRow> Damage = new List<ActorRow>();
        public readonly List<ActorRow> Healing = new List<ActorRow>();
        public readonly Dictionary<string, int> Deaths = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Whether the parser filled these tables for this fight at all. An empty healing table
        /// means it produced no healing (seen on real logs), not that nobody healed - the caller
        /// keeps ACT's figures in that case rather than printing zeroes.
        /// </summary>
        public bool HasHealing;
        public bool HasDeaths;

        public double DurationSeconds => Math.Max(0, (EndTimeMs - StartTimeMs) / 1000.0);
        public bool InProgress => string.Equals(State, "inprogress", StringComparison.Ordinal);

        public ActorRow DamageOf(string name)
        {
            foreach (var row in Damage)
                if (string.Equals(row.Name, name, StringComparison.Ordinal)) return row;
            return null;
        }

        public ActorRow HealingOf(string name)
        {
            foreach (var row in Healing)
                if (string.Equals(row.Name, name, StringComparison.Ordinal)) return row;
            return null;
        }
    }

    /// <summary>
    /// Reads the parser's live output into plain .NET objects.
    ///
    /// A port of mopimopi's js/fflogs/host-logic.js, which is where the rules this implements are
    /// argued: which fight to report, how pets fold into their owners, where a death's name comes
    /// from. Everything is converted on the collect that produced it, because the parser hands a
    /// finished fight over exactly once - the collect that returns it also drops it from the list.
    /// </summary>
    public static class ParserOutput
    {
        // ------------------------------------------------------------------ reading script values

        internal static double Num(object v)
        {
            if (v == null || v is Undefined) return 0;
            if (v is double d) return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is float f) return f;
            if (v is decimal m) return (double)m;
            if (v is bool b) return b ? 1 : 0;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        internal static string Str(object v)
        {
            if (v == null || v is Undefined) return string.Empty;
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        internal static ScriptObject Obj(object v) => v as ScriptObject;

        internal static ScriptObject Get(ScriptObject o, string name) => o == null ? null : Obj(o.GetProperty(name));

        internal static object Raw(ScriptObject o, string name) => o?.GetProperty(name);

        /// <summary>
        /// Every own property of a script object, keyed as a string.
        ///
        /// Both halves are needed: V8 files integer-like keys away as array indices, and the
        /// parser's actor tables are keyed by actor id, so PropertyNames alone comes back empty
        /// on exactly the objects that matter.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, object>> Properties(ScriptObject o)
        {
            if (o == null) yield break;
            foreach (var index in o.PropertyIndices)
                yield return new KeyValuePair<string, object>(index.ToString(CultureInfo.InvariantCulture), o[index]);
            foreach (var name in o.PropertyNames)
                yield return new KeyValuePair<string, object>(name, o.GetProperty(name));
        }

        /// <summary>Length of a script array, 0 for anything else.</summary>
        internal static int Length(ScriptObject o) => o == null ? 0 : (int)Num(o.GetProperty("length"));

        /// <summary>One value out of a JavaScript Map. Null when there is no such key or no map.</summary>
        internal static object MapGet(ScriptObject map, object key)
        {
            if (map == null) return null;
            try
            {
                var found = map.InvokeMethod("get", key);
                return found is Undefined ? null : found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ the pet tables

        /// <summary>
        /// The parser's pet bookkeeping: which actor is whose pet, and how to name an actor that
        /// never dealt damage. Null before the parser has any output.
        /// </summary>
        public sealed class PetTables
        {
            private readonly ScriptObject petsIdTable;   // Map: pet actor id -> 1-based index into petsTable
            private readonly ScriptObject petsTable;     // Array: { pet, owner, summon }
            private readonly ScriptObject output;

            public PetTables(ScriptObject output, ScriptObject petsIdTable, ScriptObject petsTable)
            {
                this.output = output;
                this.petsIdTable = petsIdTable;
                this.petsTable = petsTable;
            }

            public static PetTables From(ScriptObject parserOutput)
            {
                var ids = Get(parserOutput, "petsIdTable");
                var table = Get(parserOutput, "petsTable");
                return ids == null || table == null ? null : new PetTables(parserOutput, ids, table);
            }

            /// <summary>The owner's actor id, or 0 when this actor is nobody's pet.</summary>
            public long OwnerOf(long actorId)
            {
                var index = (long)Num(MapGet(petsIdTable, (double)actorId));
                if (index <= 0) return 0;
                var entry = Obj(petsTable[(int)(index - 1)]);
                var owner = (long)Num(Raw(entry, "owner"));
                return owner > 0 ? owner : 0;
            }

            /// <summary>The actor's name from the parser's own table, for one that never dealt damage.</summary>
            public string NameOf(long actorId)
            {
                try
                {
                    var actor = Obj(output?.InvokeMethod("getActor", (double)actorId));
                    return Str(Raw(actor, "unitName"));
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        // ------------------------------------------------------------------ actors

        private static HitDetails ReadHits(ScriptObject h)
        {
            var out_ = new HitDetails();
            if (h == null) return out_;
            out_.HitCount = (long)Num(Raw(h, "hitCount"));
            out_.CriticalCount = (long)Num(Raw(h, "criticalCount"));
            out_.DirectHitCount = (long)Num(Raw(h, "directHitCount"));
            out_.CriticalDirectHitCount = (long)Num(Raw(h, "criticalDirectHitCount"));
            out_.MaxHit = Num(Raw(h, "maxHit"));
            out_.MinHit = Num(Raw(h, "minHit"));
            return out_;
        }

        /// <summary>
        /// The biggest single hit's ability name: the ability whose own maxHit equals the actor's.
        /// Resolved here rather than kept, so no script object outlives the collect.
        /// </summary>
        private static string MaxHitAbility(ScriptObject actor, double maxHit)
        {
            if (maxHit <= 0) return string.Empty;
            var abilities = Get(actor, "abilities");
            if (abilities == null) return string.Empty;
            foreach (var pair in Properties(abilities))
            {
                var ability = Obj(pair.Value);
                if (ability == null) continue;
                if (Math.Abs(Num(Raw(Get(ability, "hitDetails"), "maxHit")) - maxHit) > 0.0001) continue;
                return Str(Raw(ability, "name"));
            }
            return string.Empty;
        }

        private static ActorFigures ReadActor(ScriptObject actor, long fallbackId)
        {
            var figures = new ActorFigures
            {
                Id = (long)Num(Raw(actor, "id")),
                Name = Str(Raw(actor, "name")),
                FullType = Str(Raw(actor, "fullType")),
                Amount = Num(Raw(actor, "amount")),
                AmountTaken = Num(Raw(actor, "amountTaken")),
                SingleTargetAmountTaken = Num(Raw(actor, "singleTargetAmountTaken")),
                AmountGiven = Num(Raw(actor, "amountGiven")),
                Over = Num(Raw(actor, "over")),
                Hits = ReadHits(Get(actor, "hitDetails")),
            };
            if (figures.Id == 0) figures.Id = fallbackId;
            figures.MaxHitAbility = MaxHitAbility(actor, figures.Hits.MaxHit);
            return figures;
        }

        /// <summary>
        /// Flattens an actors table into one row per player.
        ///
        /// Pets whose buffs mirror their owner's are already booked under the owner by the parser.
        /// The ones that are not - Demi-Bahamut, Phoenix, Solar Bahamut - arrive as actors of their
        /// own and are folded in here, while still being listed under <see cref="ActorRow.Pets"/> so
        /// a consumer that keeps pets as separate lines can hand each its share and still add up.
        /// Limit Break stays a row of its own: nobody owns it and it neither takes nor gives buffs.
        /// </summary>
        public static List<ActorRow> FoldActors(ScriptObject actors, PetTables pets)
        {
            var rows = new Dictionary<long, ActorRow>();
            if (actors == null) return new List<ActorRow>();

            // Two passes: names first, so a pet folded in before its owner was read does not end up
            // naming the row after itself.
            var read = new List<KeyValuePair<long, ActorFigures>>();
            var byId = new Dictionary<long, ActorFigures>();
            foreach (var pair in Properties(actors))
            {
                var actor = Obj(pair.Value);
                if (actor == null) continue;
                long.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key);
                var figures = ReadActor(actor, key);
                read.Add(new KeyValuePair<long, ActorFigures>(key, figures));
                byId[figures.Id] = figures;
            }

            ActorRow RowFor(long id)
            {
                if (!rows.TryGetValue(id, out var row))
                {
                    row = new ActorRow { Id = id };
                    rows[id] = row;
                }
                return row;
            }

            void AddTotals(ActorRow row, ActorFigures source)
            {
                row.Amount += source.Amount;
                row.AmountTaken += source.AmountTaken;
                row.SingleTargetAmountTaken += source.SingleTargetAmountTaken;
                row.AmountGiven += source.AmountGiven;
                row.Over += source.Over;
                row.Hits.Merge(source.Hits);
            }

            foreach (var entry in read)
            {
                var figures = entry.Value;
                var owner = pets?.OwnerOf(figures.Id) ?? 0;

                if (owner != 0 && owner != figures.Id)
                {
                    var row = RowFor(owner);
                    if (row.Name.Length == 0)
                    {
                        byId.TryGetValue(owner, out var ownerFigures);
                        row.Name = ownerFigures != null && ownerFigures.Name.Length > 0
                            ? ownerFigures.Name
                            : (pets?.NameOf(owner) ?? string.Empty);
                        row.FullType = ownerFigures?.FullType ?? string.Empty;
                    }
                    AddTotals(row, figures);
                    row.Pets.Add(figures);
                }
                else
                {
                    var row = RowFor(figures.Id);
                    if (figures.Name.Length > 0) row.Name = figures.Name;
                    if (figures.FullType.Length > 0) row.FullType = figures.FullType;
                    row.Own = figures;
                    AddTotals(row, figures);
                }
            }

            // An owner every one of whose figures came from pets has no entry of its own; give it an
            // empty one so callers can always read Own.
            foreach (var row in rows.Values)
            {
                if (row.Own.Id != 0) continue;
                row.Own = new ActorFigures { Id = row.Id, Name = row.Name, FullType = row.FullType };
            }

            return rows.Values
                .Where(r => r.Name.Length > 0)
                .OrderByDescending(r => r.Amount)
                .ToList();
        }

        // ------------------------------------------------------------------ one collect

        /// <summary>
        /// The fight to report out of one collectMeters() result: the one with the highest id.
        ///
        /// Ids are assigned when a fight books its first actor and only ever grow. A collect that
        /// returns nothing is the parser between pulls, not the end of the fight - the caller keeps
        /// what it last converted.
        /// </summary>
        public static FightFigures Read(ScriptObject meters, ScriptObject parserOutput, FightFigures previous)
        {
            var fights = Get(meters, "fights");
            if (fights == null) return previous;

            ScriptObject best = null;
            var bestId = long.MinValue;
            for (var i = 0; i < Length(fights); i++)
            {
                var fight = Obj(fights[i]);
                if (fight == null) continue;
                var id = (long)Num(Raw(fight, "id"));
                if (best != null && id < bestId) continue;
                best = fight;
                bestId = id;
            }

            if (best == null) return previous;
            if (previous != null && bestId < previous.Id) return previous;

            return ReadFight(best, PetTables.From(parserOutput));
        }

        public static FightFigures ReadFight(ScriptObject fight, PetTables pets)
        {
            var figures = new FightFigures
            {
                Id = (long)Num(Raw(fight, "id")),
                State = Str(Raw(fight, "state")),
                StartTimeMs = Num(Raw(fight, "startTime")),
                EndTimeMs = Num(Raw(fight, "endTime")),
                Zone = Str(Raw(Get(fight, "zone"), "name")),
                // The parser recomputes this on every collectMeters() from the zone handler's own
                // accounting. It is what the damage clock has taken off it; see FightMatch.
                DowntimeSeconds = Math.Max(0, Num(Raw(fight, "downtime")) / 1000.0),
            };

            figures.Damage.AddRange(FoldActors(Get(Get(fight, "friendlyDamage"), "actors"), pets));
            figures.Healing.AddRange(FoldActors(Get(Get(fight, "friendlyHealing"), "actors"), pets));
            figures.HasHealing = figures.Healing.Count > 0;

            var deaths = Get(Get(fight, "deaths"), "actors");
            figures.HasDeaths = deaths != null;
            ReadDeaths(deaths, Get(Get(fight, "friendlyDamage"), "actors"), pets, figures.Deaths);

            return figures;
        }

        /// <summary>
        /// Deaths per actor name. The table is keyed by actor id; the name comes from the damage
        /// table when the actor dealt damage, and from the parser's actor table otherwise.
        /// </summary>
        private static void ReadDeaths(ScriptObject deaths, ScriptObject damageActors, PetTables pets,
            IDictionary<string, int> into)
        {
            if (deaths == null) return;
            foreach (var pair in Properties(deaths))
            {
                var entry = Obj(pair.Value);
                var count = Length(Get(entry, "deaths"));
                if (count == 0) continue;

                long.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
                var name = Str(Raw(Obj(damageActors?.GetProperty(pair.Key)), "name"));
                if (name.Length == 0) name = pets?.NameOf(id) ?? string.Empty;
                if (name.Length == 0) continue;

                into.TryGetValue(name, out var already);
                into[name] = already + count;
            }
        }
    }
}
