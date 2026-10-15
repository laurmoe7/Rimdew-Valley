using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimDewBridge
{
    /// <summary>
    /// Mental breaks and inspirations: RimWorld decides when a pawn breaks or gets inspired; Stardew acts it out
    /// (a binging hand raids the food chest, an inspired farmer crafts iridium). Both last until the night sim ends them.
    /// </summary>
    public partial class BridgeComponent
    {
        private Pawn Who(string who) { return who == "farmer" ? Get("farmer") : Get("hand:" + who); }

        /// <summary>Report fields: the pawn's current mental break and inspiration (defName + label), empty when none.</summary>
        private static void AddMind(Dictionary<string, object> d, Pawn p)
        {
            d["break"] = p.InMentalState ? p.MentalStateDef.defName : "";
            d["break_label"] = p.InMentalState ? p.MentalStateDef.label : "";
            d["inspiration"] = p.Inspired ? p.InspirationDef.defName : "";
            d["inspiration_label"] = p.Inspired ? p.InspirationDef.label : "";
        }

        /// <summary>Stardew used the inspiration up (e.g. the farmer crafted the iridium item).</summary>
        private void UseInspiration(string who)
        {
            var p = Who(who);
            if (p != null && p.Inspired) p.mindState.inspirationHandler.EndInspiration(p.Inspiration);
        }

        /// <summary>Hands go back to standing drafted once a break is over, so they don't roam the hidden map at night.</summary>
        private static void Redraft(Pawn p)
        {
            if (!p.Dead && !p.InMentalState && p.drafter != null && !p.drafter.Drafted) p.drafter.Drafted = true;
        }

        // ---- test tools ----
        private void DevInspire(string who, string defName)
        {
            var p = Who(who);
            var def = DefDatabase<InspirationDef>.GetNamedSilentFail(defName);
            if (p == null || def == null) return;
            if (p.Inspired) p.mindState.inspirationHandler.EndInspiration(p.Inspiration);
            if (!p.mindState.inspirationHandler.TryStartInspiration(def, "Stardew test", false))
                Log.Warning("[RimDew] " + p.Name.ToStringShort + " cannot get " + defName + " (incapable of the work?)");
            ReportMind(who);
        }

        private void DevBreak(string who, string defName)
        {
            var p = Who(who);
            if (p == null) return;
            if (p.InMentalState) p.MentalState.RecoverFromState();   // an empty def just calms them down
            var def = DefDatabase<MentalStateDef>.GetNamedSilentFail(defName);
            if (def == null) { Redraft(p); ReportMind(who); return; }
            p.mindState.mentalStateHandler.TryStartMentalState(def, "Stardew test", true, true, false, null, true);
            ReportMind(who);
        }

        private void ReportMind(string who)
        {
            if (who == "farmer") SendHealth(); else SendHandsInfo();
        }
    }
}
