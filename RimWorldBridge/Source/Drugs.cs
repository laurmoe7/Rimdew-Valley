using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimDewBridge
{
    /// <summary>
    /// Drinks: Stardew beer, wine and coffee are RimWorld drugs taken by the farmer pawn. RimWorld decides the high, tolerance,
    /// addiction chance, hangover and withdrawal; the results reach Stardew through the farmer's capacities, mood and health report.
    /// </summary>
    public partial class BridgeComponent
    {
        private static HashSet<HediffDef> drugHediffs;

        /// <summary>Every hediff a drug can cause: highs and tolerances from the drug defs, addictions, hangover and overdose.</summary>
        private static HashSet<HediffDef> DrugHediffs()
        {
            if (drugHediffs != null) return drugHediffs;
            drugHediffs = new HashSet<HediffDef>();
            foreach (var d in DefDatabase<ThingDef>.AllDefs)
            {
                var drug = d.GetCompProperties<CompProperties_Drug>();
                if (drug == null || d.ingestible == null) continue;
                if (d.ingestible.outcomeDoers != null)
                    foreach (var o in d.ingestible.outcomeDoers.OfType<IngestionOutcomeDoer_GiveHediff>())
                        if (o.hediffDef != null) drugHediffs.Add(o.hediffDef);
                if (drug.chemical != null && drug.chemical.addictionHediff != null) drugHediffs.Add(drug.chemical.addictionHediff);
            }
            foreach (var n in new[] { "Hangover", "DrugOverdose" })
            {
                var h = DefDatabase<HediffDef>.GetNamedSilentFail(n);
                if (h != null) drugHediffs.Add(h);
            }
            return drugHediffs;
        }

        private static bool IsDrug(Hediff h) { return DrugHediffs().Contains(h.def); }

        private void Ingest(string defName, int count)
        {
            var p = Get("farmer");
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (p == null || p.Dead || def == null || def.ingestible == null)
            {
                Log.Warning("[RimDew] Cannot ingest " + defName);
                return;
            }
            for (int i = 0; i < Math.Max(1, count); i++)
            {
                var t = ThingMaker.MakeThing(def);
                t.Ingested(p, p.needs != null && p.needs.food != null ? p.needs.food.NutritionWanted : 0f);
            }
            SendHealth();
        }

        private static readonly System.Reflection.PropertyInfo ChemicalFall =
            typeof(Need_Chemical).GetProperty("ChemicalFallPerTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        /// <summary>The colony stays paused through the Stardew day, so drug effects keep their own clock (a Stardew hour is a RimWorld
        /// hour): highs wear off, tolerance and addiction fade, chemical needs fall toward withdrawal. Mirrors vanilla, which moves
        /// severities in fixed steps and gives a hangover to anyone still hammered.</summary>
        private void DrugClock(int minutes)
        {
            var p = Get("farmer");
            if (p == null || p.Dead || minutes <= 0) return;
            int ticks = minutes * GenDate.TicksPerHour / 60;
            float days = ticks / (float)GenDate.TicksPerDay;
            bool any = false;

            var alcohol = p.health.hediffSet.GetFirstHediffOfDef(DefDatabase<HediffDef>.GetNamedSilentFail("AlcoholHigh"));
            bool hammered = alcohol != null && alcohol.CurStageIndex >= 3;

            foreach (var h in p.health.hediffSet.hediffs.Where(IsDrug).ToList())
            {
                any = true;
                var withComps = h as HediffWithComps;
                if (withComps != null && withComps.comps != null)
                {
                    foreach (var c in withComps.comps)
                    {
                        if (c is HediffComp_SeverityModifierBase sev) h.Severity += sev.SeverityChangePerDay() * days;
                        else if (c is HediffComp_Disappears gone) gone.ticksToDisappear -= ticks;
                    }
                }
                if (h.ShouldRemove) p.health.RemoveHediff(h);
            }

            if (hammered)
            {
                var hangDef = DefDatabase<HediffDef>.GetNamedSilentFail("Hangover");
                var hang = p.health.hediffSet.GetFirstHediffOfDef(hangDef);
                if (hang == null) { hang = HediffMaker.MakeHediff(hangDef, p); p.health.AddHediff(hang); }
                hang.Severity = 1f;
            }

            if (p.needs != null)
            {
                foreach (var need in p.needs.AllNeeds.OfType<Need_Chemical>())
                {
                    any = true;
                    float perTick = ChemicalFall != null ? (float)ChemicalFall.GetValue(need, null) : need.def.fallPerDay / GenDate.TicksPerDay;
                    need.CurLevel -= perTick * ticks;
                }
                p.needs.AddOrRemoveNeedsAsAppropriate();
            }
            if (any) SendHealth();
        }

        /// <summary>For the health report: what the farmer is on, hooked on, and craving (addictions and cravings by chemical, e.g. "alcohol").</summary>
        private static Dictionary<string, object> DrugReport(Pawn p)
        {
            var on = new List<object>();
            var addicted = new List<object>();
            var tolerance = new List<object>();
            bool active = false;
            foreach (var h in p.health.hediffSet.hediffs)
            {
                if (!IsDrug(h)) continue;
                active = true;                       // includes a hangover still hidden behind the drunkenness
                if (!h.Visible) continue;
                var addiction = h as Hediff_Addiction;
                // Stage only, no "hours left", so the label changes only when the state really does.
                string label = h.CurStage != null && !h.CurStage.label.NullOrEmpty() ? h.def.label + " (" + h.CurStage.label + ")" : h.def.label;
                if (addiction != null) addicted.Add(addiction.Chemical != null ? addiction.Chemical.label : h.def.label);
                else if (h.def.defName.EndsWith("Tolerance")) tolerance.Add(label);
                else on.Add(label);
            }
            var withdrawal = new List<object>();
            if (p.needs != null)
                foreach (var need in p.needs.AllNeeds.OfType<Need_Chemical>())
                    if (need.CurCategory == DrugDesireCategory.Withdrawal && need.AddictionHediff != null && need.AddictionHediff.Chemical != null)
                        withdrawal.Add(need.AddictionHediff.Chemical.label);
            if (p.needs != null && p.needs.AllNeeds.OfType<Need_Chemical>().Any()) active = true;
            return new Dictionary<string, object>
            {
                { "on", on }, { "addicted", addicted }, { "withdrawal", withdrawal }, { "tolerance", tolerance }, { "active", active }
            };
        }

        // ---- test tools ----
        /// <summary>Hook the farmer on a drug right away, already craving it (Stardew F8 menu).</summary>
        private void DevAddict(string drugDef)
        {
            var p = Get("farmer");
            var drug = DefDatabase<ThingDef>.GetNamedSilentFail(drugDef)?.GetCompProperties<CompProperties_Drug>();
            if (p == null || drug == null || drug.chemical == null || drug.chemical.addictionHediff == null) return;
            if (p.health.hediffSet.GetFirstHediffOfDef(drug.chemical.addictionHediff) == null)
                p.health.AddHediff(HediffMaker.MakeHediff(drug.chemical.addictionHediff, p));
            p.needs.AddOrRemoveNeedsAsAppropriate();
            var need = p.needs.AllNeeds.OfType<Need_Chemical>().FirstOrDefault(n => n.AddictionHediff != null && n.AddictionHediff.def == drug.chemical.addictionHediff);
            if (need != null) need.CurLevel = 0f;
            SendHealth();
        }

        private void DevSoberUp()
        {
            var p = Get("farmer");
            if (p == null) return;
            foreach (var h in p.health.hediffSet.hediffs.Where(IsDrug).ToList()) p.health.RemoveHediff(h);
            p.needs.AddOrRemoveNeedsAsAppropriate();
            SendHealth();
        }
    }
}
