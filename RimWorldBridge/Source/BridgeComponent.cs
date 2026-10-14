using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimDewBridge
{
    /// <summary>
    /// RimWorld side of the bridge. Stardew is the visible game; this colony is a hidden simulation.
    /// It stays paused and only advances one RimWorld day each time Stardew ends a day.
    ///   "npc:Name"  = Stardew villager (mood sim)
    ///   "farmer"    = the player (injury sim)
    ///   "hand:Name" = hired farmhand (skills/mood decide work output)
    /// </summary>
    public class BridgeComponent : GameComponent
    {
        public static BridgeComponent Instance;
        private const int TicksPerDay = 20000; // a third of a RimWorld day keeps the nightly sim to a few seconds
        private const int TicksPerFrame = 3000;

        private HubClient hub;
        private Dictionary<string, Pawn> pawns = new Dictionary<string, Pawn>();
        private List<string> pk;
        private List<Pawn> pv;
        private string researchTarget = "";
        private double wealth;

        private int ticksLeft;
        private Dictionary<string, object> currentDay;
        private readonly Dictionary<string, int> handPriority = new Dictionary<string, int>(); // "name|task" -> 0..4

        public BridgeComponent(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Value, LookMode.Reference, ref pk, ref pv);
            Scribe_Values.Look(ref researchTarget, "researchTarget", "");
            Scribe_Values.Look(ref wealth, "wealth");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pawns == null) pawns = new Dictionary<string, Pawn>();
        }

        public override void FinalizeInit()
        {
            Instance = this;
            Application.runInBackground = true;
            Prefs.RunInBackground = true;
            // Background-sim mode: Stardew is the game being played, so keep RimWorld silent and light.
            // Stardew's OpenAL crashes when RimWorld's audio is live at the same time, so stop RimWorld's audio engine.
            AudioListener.pause = true;
            AudioListener.volume = 0f;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 20;
            hub = hub ?? new HubClient();
        }

        // ---- main loop (runs every frame, even while paused) ----
        public override void GameComponentUpdate()
        {
            if (hub == null) return;
            if (!AudioListener.pause) AudioListener.pause = true; // the game un-pauses audio on window focus changes
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            Dictionary<string, object> m;
            while (hub.Inbox.TryDequeue(out m))
            {
                try { Handle(m); }
                catch (Exception e) { Log.Error("[RimDew] handling " + m.Str("type") + ": " + e); }
            }

            if (ticksLeft > 0)
            {
                // Tick for up to ~60 ms per frame so the (hidden) window never locks up.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = 0;
                while (n < ticksLeft && n < TicksPerFrame && sw.ElapsedMilliseconds < 60) { Find.TickManager.DoSingleTick(); n++; }
                ticksLeft -= n;
                if (ticksLeft == 0) FinishDay();
            }
        }

        public void Send(Dictionary<string, object> m) { if (hub != null) hub.Send(m); }

        private void Handle(Dictionary<string, object> m)
        {
            switch (m.Str("type"))
            {
                case "peer_status":
                    Messages.Message(m.Bool("online") ? "Stardew Valley connected" : "Stardew Valley disconnected",
                        m.Bool("online") ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
                    break;
                case "item_send": DropItems(m.Str("item"), (int)m.Num("count", 1)); break;
                case "npc_list":
                    foreach (var n in m.List("npcs")) Ensure("npc:" + n, Convert.ToString(n));
                    Ensure("farmer", "Farmer");
                    break;
                case "npc_event": NpcEvent(m.Str("npc"), m.Str("kind")); break;
                case "player_hurt": Hurt(m.Num("amount"), m.Str("kind")); break;
                case "tend": Tend((float)m.Num("quality", 0.4)); break;
                case "hire": Ensure("hand:" + m.Str("name"), m.Str("name")); SendHandsInfo(); break;
                case "fire": Fire(m.Str("name")); break;
                case "priority": handPriority[m.Str("name") + "|" + m.Str("task")] = (int)m.Num("value"); break;
                case "research_set": researchTarget = m.Str("id"); break;
                case "day_end": BeginDay(m); break;
                case "hand_xp": HandXp(m); break;
                // --- test tools (Stardew menu > Test tab) ---
                case "report":
                    SendHandsInfo();
                    Send(new Dictionary<string, object> { { "type", "npc_state" }, { "apply", false }, { "npcs", NpcReport() } });
                    SendHealth();
                    SendResearch();
                    break;
                case "research_points": ApplyResearch(m.Num("amount")); SendResearch(); break;
                case "dev_incident": FireIncident(m.Str("def")); break;
            }
        }

        // ---- pawns ----
        private Pawn Ensure(string key, string name)
        {
            Pawn p;
            if (pawns.TryGetValue(key, out p) && p != null && !p.Destroyed)
            {
                if (p.Dead) ResurrectionUtility.TryResurrect(p);
                return p;
            }
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return null;
            p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                PawnGenerationContext.NonPlayer, -1, forceGenerateNewPawn: true, canGeneratePawnRelations: false));
            p.Name = new NameTriple(name, name, "");
            GainThought(p, "valley_life"); // baseline mood from day one, not just after the first night
            GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(map.Center, map, 15), map);
            if (p.drafter != null) p.drafter.Drafted = true; // stand still; no jobs, no wandering
            pawns[key] = p;
            return p;
        }

        private Pawn Get(string key)
        {
            Pawn p;
            return pawns.TryGetValue(key, out p) && p != null && !p.Destroyed ? p : null;
        }

        private void Fire(string name)
        {
            var p = Get("hand:" + name);
            if (p != null) p.Destroy();
            pawns.Remove("hand:" + name);
        }

        // ---- villager mood ----
        /// <summary>Instant mood target (baseline + all current thoughts). The visible bar eases toward this over game time,
        /// which would look stale while the colony is paused between nights.</summary>
        private static float Mood(Pawn p) { return p.needs.mood.CurInstantLevelPercentage; }

        private static string Pascal(string kind)
        {
            return string.Concat(kind.Split('_').Select(s => s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1)));
        }

        private void NpcEvent(string npc, string kind)
        {
            // The farmer feels good about every chat and festival too.
            if (kind == "talked" || kind == "festival") { var farmer = Get("farmer"); if (farmer != null) GainThought(farmer, kind); }
            var p = Get("npc:" + npc) ?? Get("hand:" + npc);
            if (p == null || p.needs == null) return;
            GainThought(p, kind);
            if (p.needs.joy != null)
            {
                float joy = kind == "festival" ? 0.5f : kind == "gift_loved" ? 0.2f : kind == "talked" || kind == "gift_liked" ? 0.1f : 0f;
                p.needs.joy.CurLevel = Mathf.Clamp01(p.needs.joy.CurLevel + joy);
            }
        }

        private void GainThought(Pawn p, string kind)
        {
            var def = DefDatabase<ThoughtDef>.GetNamedSilentFail("SD_" + Pascal(kind));
            if (def != null && p.needs != null && p.needs.mood != null)
                p.needs.mood.thoughts.memories.TryGainMemory(def);
        }

        private List<object> NpcReport()
        {
            var list = new List<object>();
            foreach (var kv in pawns)
            {
                var p = kv.Value;
                if (!kv.Key.StartsWith("npc:") || p == null || p.Dead || p.needs == null || p.needs.mood == null) continue;
                float mood = Mood(p);
                bool brk = p.InMentalState;
                list.Add(new Dictionary<string, object>
                {
                    { "name", kv.Key.Substring(4) },
                    { "mood", Math.Round(mood, 2) },
                    { "break", brk },
                    { "label", brk ? p.MentalState.def.label : mood < 0.3f ? "unhappy" : mood > 0.7f ? "happy" : "okay" }
                });
            }
            return list;
        }

        // ---- farmer health ----
        private void Hurt(double amount, string kind)
        {
            var p = Get("farmer");
            if (p == null || p.Dead || amount <= 0) return;
            DamageDef d = kind == "blunt" ? DamageDefOf.Blunt : kind == "bite" ? DamageDefOf.Bite
                        : kind == "burn" ? DamageDefOf.Flame : DamageDefOf.Cut;
            p.TakeDamage(new DamageInfo(d, (float)(amount * 0.6), 0f, -1f));
            SendHealth();
        }

        private void Tend(float quality)
        {
            var p = Get("farmer");
            if (p == null || p.Dead) return;
            foreach (var h in p.health.hediffSet.hediffs.ToList())
                if (h.TendableNow()) h.Tended(quality, quality, 0);
            SendHealth(false, true);
        }

        /// <summary>A night's rest heals wounds faster than the (shortened) sim day would: tended wounds heal twice as much.</summary>
        private void HealOvernight()
        {
            var p = Get("farmer");
            if (p == null || p.Dead) return;
            foreach (var h in p.health.hediffSet.hediffs.OfType<Hediff_Injury>().ToList())
                if (!h.IsPermanent()) h.Heal(h.IsTended() ? 4f : 2f);
        }

        private void SendHealth(bool day = false, bool tend = false)
        {
            var p = Get("farmer");
            if (p == null) return;
            var cap = p.health.capacities;
            var injuries = new List<object>();
            foreach (var h in p.health.hediffSet.hediffs)
            {
                if (!h.Visible || injuries.Count >= 8) continue;
                injuries.Add(h.LabelCap + (h.Part != null ? " (" + h.Part.Label + ")" : "") + (h.IsTended() ? " [tended]" : ""));
            }
            Send(new Dictionary<string, object>
            {
                { "type", "health_state" }, { "mood", Math.Round(p.needs != null && p.needs.mood != null ? Mood(p) : 0.5f, 2) }, { "phase", day ? "day" : tend ? "tend" : "event" },
                { "dead", p.Dead },
                { "downed", p.Downed },
                { "health_pct", Math.Round(p.health.summaryHealth.SummaryHealthPercent, 2) },
                { "bleed", Math.Round(p.health.hediffSet.BleedRateTotal, 2) },
                { "pain", Math.Round(p.health.hediffSet.PainTotal, 2) },
                { "moving", Math.Round(cap.GetLevel(PawnCapacityDefOf.Moving), 2) },
                { "manipulation", Math.Round(cap.GetLevel(PawnCapacityDefOf.Manipulation), 2) },
                { "consciousness", Math.Round(cap.GetLevel(PawnCapacityDefOf.Consciousness), 2) },
                { "injuries", injuries }
            });
            if (p.Dead) ResurrectionUtility.TryResurrect(p);
        }

        // ---- day cycle ----
        private void BeginDay(Dictionary<string, object> m)
        {
            currentDay = m;
            wealth = m.Num("wealth", wealth);
            string weather = m.Str("weather");
            bool bad = weather == "rain" || weather == "storm" || weather == "snow" || weather == "green_rain";
            bool paid = m.Bool("wages_paid", true);

            foreach (var kv in pawns)
            {
                var p = kv.Value;
                if (p == null || p.Dead || p.needs == null) continue;
                if (p.needs.food != null) p.needs.food.CurLevel = Mathf.Max(p.needs.food.CurLevel, 0.8f);
                if (p.needs.rest != null) p.needs.rest.CurLevel = Mathf.Max(p.needs.rest.CurLevel, 0.9f);
                GainThought(p, "valley_life"); // offsets the gloom of standing on a bare map; see Thoughts_Stardew.xml
                if ((kv.Key.StartsWith("npc:") || kv.Key == "farmer") && bad) GainThought(p, "bad_weather");
                if (kv.Key.StartsWith("hand:"))
                {
                    GainThought(p, paid ? "paid" : "unpaid");
                    bool housed = m.ContainsKey("housed_names")
                        ? m.List("housed_names").Any(n => Convert.ToString(n) == kv.Key.Substring(5))
                        : m.Bool("housed", true);
                    GainThought(p, housed ? "housed" : "unhoused");
                }
            }
            ApplyResearch(m.Num("research_points"));
            ticksLeft = TicksPerDay;
        }

        private void FinishDay()
        {
            var m = currentDay ?? new Dictionary<string, object>();
            HealOvernight();
            Send(new Dictionary<string, object> { { "type", "npc_state" }, { "npcs", NpcReport() } });
            SendHealth(true);
            Send(WorkReport(m.Obj_("backlog")));
            SendResearch();
            currentDay = null;
        }

        // ---- farmhands ----
        private int Prio(string name, string task, int def)
        {
            int v;
            return handPriority.TryGetValue(name + "|" + task, out v) ? v : def;
        }

        /// <summary>Every thought currently affecting the pawn's mood, biggest first, so Stardew can show why they feel as they do.</summary>
        private static List<object> MoodThoughts(Pawn p)
        {
            var result = new List<object>();
            if (p.needs == null || p.needs.mood == null) return result;
            var all = new List<Thought>();
            p.needs.mood.thoughts.GetAllMoodThoughts(all);
            foreach (var t in all.OrderByDescending(t => Math.Abs(t.MoodOffset())).Take(14))
            {
                float off = t.MoodOffset();
                if (Math.Abs(off) < 0.5f) continue;
                result.Add(new Dictionary<string, object> { { "label", t.LabelCap }, { "offset", Math.Round(off, 1) } });
            }
            return result;
        }

        private static Dictionary<string, object> NeedLevels(Pawn p)
        {
            return new Dictionary<string, object>
            {
                { "food", p.needs.food != null ? Math.Round(p.needs.food.CurLevelPercentage, 2) : 0.0 },
                { "rest", p.needs.rest != null ? Math.Round(p.needs.rest.CurLevelPercentage, 2) : 0.0 },
                { "joy", p.needs.joy != null ? Math.Round(p.needs.joy.CurLevelPercentage, 2) : 0.0 }
            };
        }

        /// <summary>Hand status without running a night: mood, skills and whether they could work right now.</summary>
        /// <summary>Skill XP earned by hands from Stardew work: Plants/Animals from chores, Shooting from raids.</summary>
        private void HandXp(Dictionary<string, object> m)
        {
            foreach (var o in m.List("hands"))
            {
                var h = o.Obj();
                var p = Get("hand:" + h.Str("name"));
                if (p == null || p.skills == null) continue;
                GrantXp(p, SkillDefOf.Plants, h.Num("plants") * 8f);
                GrantXp(p, SkillDefOf.Animals, h.Num("animals") * 8f);
                GrantXp(p, SkillDefOf.Shooting, h.Num("shooting") * 4f);
            }
        }

        private void GrantXp(Pawn p, SkillDef def, double xp)
        {
            if (xp <= 0) return;
            var rec = p.skills.GetSkill(def);
            int before = rec.Level;
            rec.Learn((float)xp, true);
            if (rec.Level > before)
                Send(new Dictionary<string, object>
                {
                    { "type", "hand_levelup" }, { "name", p.Name.ToStringShort }, { "skill", def.label }, { "level", rec.Level }
                });
        }

        private void SendHandsInfo()
        {
            var hands = new List<object>();
            foreach (var kv in pawns)
            {
                var p = kv.Value;
                if (!kv.Key.StartsWith("hand:") || p == null || p.Dead || p.needs == null || p.needs.mood == null) continue;
                float mood = Mood(p);
                bool able = !p.Downed && !p.InMentalState;
                hands.Add(new Dictionary<string, object>
                {
                    { "name", kv.Key.Substring(5) }, { "mood", Math.Round(mood, 2) },
                    { "plants", p.skills.GetSkill(SkillDefOf.Plants).Level }, { "animals", p.skills.GetSkill(SkillDefOf.Animals).Level },
                    { "shooting", p.skills.GetSkill(SkillDefOf.Shooting).Level },
                    { "thoughts", MoodThoughts(p) }, { "needs", NeedLevels(p) }, { "traits", TraitList(p) },
                    { "status", !able ? (p.InMentalState ? "mental break" : "downed") : mood < 0.3f ? "unhappy" : "ok" }
                });
            }
            Send(new Dictionary<string, object> { { "type", "hands_info" }, { "hands", hands } });
        }

        // ---- traits ----
        private static List<object> TraitList(Pawn p)
        {
            var list = new List<object>();
            if (p.story == null) return list;
            foreach (var t in p.story.traits.allTraits)
                list.Add(new Dictionary<string, object> { { "id", t.def.defName }, { "degree", t.Degree }, { "label", t.LabelCap } });
            return list;
        }

        private static int TraitDegree(Pawn p, string defName, int none = 0)
        {
            if (p.story == null) return none;
            foreach (var t in p.story.traits.allTraits) if (t.def.defName == defName) return t.Degree;
            return none;
        }

        private static bool HasTrait(Pawn p, string defName)
        {
            return p.story != null && p.story.traits.allTraits.Any(t => t.def.defName == defName);
        }

        /// <summary>Hard workers put in longer hours, the lazy shorter ones.</summary>
        private static double WorkHoursFactor(Pawn p)
        {
            switch (TraitDegree(p, "Industriousness"))
            {
                case 2: return 1.35;
                case 1: return 1.2;
                case -1: return 0.8;
                case -2: return 0.6;
                default: return 1.0;
            }
        }

        private Dictionary<string, object> WorkReport(Dictionary<string, object> backlog)
        {
            var remaining = new Dictionary<string, double>
            {
                { "water", backlog.Num("water") }, { "harvest", backlog.Num("harvest") }, { "animals", backlog.Num("animals") }
            };
            var done = new Dictionary<string, double> { { "water", 0 }, { "harvest", 0 }, { "animals", 0 } };
            var hands = new List<object>();

            foreach (var kv in pawns)
            {
                if (!kv.Key.StartsWith("hand:")) continue;
                var p = kv.Value;
                string name = kv.Key.Substring(5);
                if (p == null || p.Dead) continue;
                float mood = Mood(p);
                int plants = p.skills.GetSkill(SkillDefOf.Plants).Level;
                int animals = p.skills.GetSkill(SkillDefOf.Animals).Level;
                bool able = !p.Downed && !p.InMentalState;
                double hours = able ? 10.0 * (0.5 + mood) * Math.Max(0.1, p.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation)) * WorkHoursFactor(p) : 0;
                double green = HasTrait(p, "SD_GreenThumb") ? 1.3 : 1.0;
                var rate = new Dictionary<string, double>
                {
                    { "water", (6 + plants * 1.2) * green }, { "harvest", (4 + plants * 0.8) * green }, { "animals", 5 + animals * 1.0 }
                };
                var order = new[] { "water", "harvest", "animals" }
                    .Select((t, i) => new { t, pr = Prio(name, t, i + 1) })
                    .Where(x => x.pr > 0).OrderBy(x => x.pr).Select(x => x.t);
                double workedHours = 0;
                var mine = new Dictionary<string, double> { { "water", 0 }, { "harvest", 0 }, { "animals", 0 } };
                foreach (var task in order)
                {
                    if (hours <= 0 || remaining[task] <= 0) continue;
                    double h = Math.Min(hours, remaining[task] / rate[task]);
                    double n = Math.Min(remaining[task], h * rate[task]);
                    done[task] += n; mine[task] += n; remaining[task] -= n; hours -= h; workedHours += h;
                }
                hands.Add(new Dictionary<string, object>
                {
                    { "name", name }, { "mood", Math.Round(mood, 2) }, { "plants", plants }, { "animals", animals },
                    { "shooting", p.skills.GetSkill(SkillDefOf.Shooting).Level },
                    { "thoughts", MoodThoughts(p) }, { "needs", NeedLevels(p) }, { "traits", TraitList(p) },
                    { "hours", Math.Round(workedHours, 1) },
                    { "did_water", (int)mine["water"] }, { "did_harvest", (int)mine["harvest"] }, { "did_feed", (int)mine["animals"] },
                    { "status", !able ? (p.InMentalState ? "mental break" : "downed") : mood < 0.3f ? "unhappy" : "ok" }
                });
            }
            return new Dictionary<string, object>
            {
                { "type", "work_done" },
                { "water", (int)done["water"] }, { "harvest", (int)done["harvest"] }, { "animals", (int)done["animals"] },
                { "hands", hands }
            };
        }

        // ---- research ----
        private static IEnumerable<ResearchProjectDef> StardewProjects()
        {
            return DefDatabase<ResearchProjectDef>.AllDefs.Where(d => d.defName.StartsWith("SD_"));
        }

        private void ApplyResearch(double points)
        {
            if (points <= 0) return;
            var proj = StardewProjects().FirstOrDefault(d => d.defName == "SD_" + researchTarget && !d.IsFinished)
                    ?? StardewProjects().Where(d => !d.IsFinished).OrderBy(d => d.baseCost).FirstOrDefault();
            if (proj == null) return;
            bool was = proj.IsFinished;
            Find.ResearchManager.AddProgress(proj, (float)points);
            if (!was && proj.IsFinished)
            {
                Send(new Dictionary<string, object> { { "type", "research_unlocked" }, { "id", proj.defName.Substring(3) } });
                Messages.Message("Stardew research complete: " + proj.label, MessageTypeDefOf.PositiveEvent, false);
            }
        }

        private void SendResearch()
        {
            var list = StardewProjects().Select(d => (object)new Dictionary<string, object>
            {
                { "id", d.defName.Substring(3) }, { "label", d.label },
                { "progress", Math.Round(d.ProgressReal, 0) }, { "cost", d.baseCost }, { "done", d.IsFinished }
            }).ToList();
            Send(new Dictionary<string, object> { { "type", "research_state" }, { "projects", list }, { "target", researchTarget } });
        }

        private void FireIncident(string defName)
        {
            var def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
            var map = Find.AnyPlayerHomeMap;
            if (def == null || map == null) { Log.Warning("[RimDew] Cannot fire incident " + defName); return; }
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.forced = true;
            def.Worker.TryExecute(parms); // our wrapper forwards it to Stardew
        }

        // ---- storyteller threat scale (wealth from Stardew) ----
        public float ThreatScale() { return Mathf.Clamp((float)(0.5 + wealth / 50000.0), 0.5f, 3f); }

        public void MoodHit(string kind)
        {
            foreach (var kv in pawns)
                if (kv.Value != null && !kv.Value.Dead && (kv.Key.StartsWith("npc:") || kv.Key == "farmer")) GainThought(kv.Value, kind);
        }

        // ---- items (from the original item bridge) ----
        private void DropItems(string defName, int count)
        {
            var map = Find.AnyPlayerHomeMap;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (map == null || def == null || count <= 0)
            {
                Log.Warning("[RimDew] Cannot deliver " + count + " x " + defName);
                return;
            }
            var things = new List<Thing>();
            while (count > 0)
            {
                var t = ThingMaker.MakeThing(def);
                t.stackCount = Math.Min(count, def.stackLimit);
                count -= t.stackCount;
                things.Add(t);
            }
            DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, things);
            Messages.Message("Stardew shipment arrived: " + things[0].LabelNoCount, MessageTypeDefOf.PositiveEvent, false);
        }
    }

    internal static class JExtra
    {
        public static Dictionary<string, object> Obj_(this Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) ? v.Obj() : new Dictionary<string, object>();
        }
    }
}
