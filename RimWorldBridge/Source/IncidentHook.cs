using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimDewBridge
{
    /// <summary>
    /// Intercepts the real storyteller's threats. Instead of attacking the hidden colony, the incident is
    /// sent to Stardew (raids -> monsters, infestation -> grubs, weather events -> weather).
    /// No Harmony: we swap each incident's cached IncidentWorker for a wrapper.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class IncidentHook
    {
        private static readonly HashSet<string> Extra = new HashSet<string>
        {
            "ToxicFallout", "Eclipse", "ColdSnap", "HeatWave", "VolcanicWinter", "Flashstorm"
        };

        static IncidentHook()
        {
            var field = typeof(IncidentDef).GetField("workerInt", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) { Log.Error("[RimDew] IncidentDef.workerInt not found; threats will hit the hidden colony."); return; }
            int n = 0;
            foreach (var d in DefDatabase<IncidentDef>.AllDefs)
            {
                if (d.workerClass == null) continue;
                bool threat = d.category == IncidentCategoryDefOf.ThreatBig || d.category == IncidentCategoryDefOf.ThreatSmall;
                if (!threat && !Extra.Contains(d.defName)) continue;
                var wrapper = new BridgeIncidentWorker(d.Worker) { def = d };
                field.SetValue(d, wrapper);
                n++;
            }
            Log.Message("[RimDew] Redirected " + n + " incident types to Stardew.");
        }
    }

    public class BridgeIncidentWorker : IncidentWorker
    {
        private readonly IncidentWorker inner;
        public BridgeIncidentWorker(IncidentWorker inner) { this.inner = inner; }

        protected override bool CanFireNowSub(IncidentParms parms) { return inner.CanFireNow(parms); }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var bridge = BridgeComponent.Instance;
            if (bridge == null) return false;
            float points = parms.points * bridge.ThreatScale();
            bridge.Send(new Dictionary<string, object>
            {
                { "type", "incident" },
                { "def", def.defName },
                { "label", def.label },
                { "category", def.category.defName },
                { "points", (int)points }
            });
            bridge.MoodHit("raided");
            Messages.Message("Incident sent to Stardew: " + def.label + " (" + (int)points + " pts)", MessageTypeDefOf.NeutralEvent, false);
            return true;
        }
    }
}
