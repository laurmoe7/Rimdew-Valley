using HarmonyLib;
using Newtonsoft.Json.Linq;
using StardewValley;

namespace StardewBridge;

/// <summary>Drinks are RimWorld drugs for the farmer's pawn: alcohol makes you tipsy, then drunk, then hungover, coffee is psychite tea,
/// and a triple shot is go-juice's milder cousin, wake-up. RimWorld rolls tolerance and addiction; withdrawal follows if you stop.</summary>
public partial class ModEntry
{
    /// <summary>Stardew item -> RimWorld drug and doses.</summary>
    private static readonly Dictionary<string, (string def, int doses)> DrinkMap = new()
    {
        ["(O)346"] = ("Beer", 1),          // Beer
        ["(O)303"] = ("Beer", 1),          // Pale Ale
        ["(O)459"] = ("Beer", 1),          // Mead
        ["(O)873"] = ("Beer", 1),          // Piña Colada
        ["(O)348"] = ("Beer", 2),          // Wine
        ["(O)395"] = ("PsychiteTea", 1),   // Coffee
        ["(O)253"] = ("WakeUp", 1),        // Triple Shot Espresso
    };

    private bool drugActive;                                    // anything drug-related on the farmer, so the clock must run
    private HashSet<string> lastDrugsOn = new(), lastAddicted = new(), lastWithdrawal = new();
    internal List<string> LastTolerance = new();

    internal void OnFarmerAte(StardewValley.Object o)
    {
        if (!DrinkMap.TryGetValue(o.QualifiedItemId, out var d)) return;
        Drink(d.def, d.doses);
    }

    internal void Drink(string def, int doses)
    {
        Send(new JObject { ["type"] = "ingest", ["item"] = def, ["count"] = doses });
        drugActive = true;
    }

    /// <summary>Every 10 in-game minutes while anything is in the system or the farmer is hooked.</summary>
    private void OnTimeChangedDrugs()
    {
        if (drugActive) Send(new JObject { ["type"] = "drug_clock", ["minutes"] = 10 });
    }

    internal void AdvanceDrugClock(int minutes) => Send(new JObject { ["type"] = "drug_clock", ["minutes"] = minutes });

    /// <summary>Reads the "drugs" part of a health report: HUD notes when you get a high, a hangover, an addiction or withdrawal.</summary>
    private void OnDrugState(JObject? drugs)
    {
        if (drugs == null) return;
        var on = Strings(drugs["on"]);
        var addicted = Strings(drugs["addicted"]);
        var withdrawal = Strings(drugs["withdrawal"]);
        drugActive = (bool?)drugs["active"] ?? (on.Count > 0 || addicted.Count > 0);
        LastTolerance = Strings(drugs["tolerance"]).ToList();

        foreach (var s in on.Except(lastDrugsOn)) Hud($"{char.ToUpper(s[0])}{s[1..]}.", HUDMessage.newQuest_type);
        foreach (var s in addicted.Except(lastAddicted)) Hud($"You've developed {Article(s)} {s} addiction.", HUDMessage.error_type);
        foreach (var s in withdrawal.Except(lastWithdrawal)) Hud($"Withdrawal: you're craving {s}.", HUDMessage.error_type);
        foreach (var s in lastAddicted.Except(addicted)) Hud($"You've beaten your {s} addiction.", HUDMessage.achievement_type);
        lastDrugsOn = on; lastAddicted = addicted; lastWithdrawal = withdrawal;
    }

    private static HashSet<string> Strings(JToken? t) => t is JArray a ? a.Select(x => (string)x!).ToHashSet() : new HashSet<string>();

    private static string Article(string s) => "aeiou".Contains(char.ToLower(s[0])) ? "an" : "a";

    /// <summary>What the farmer is on, for buff text and the Health tab.</summary>
    internal List<string> DrugLines()
    {
        var lines = lastDrugsOn.ToList();
        lines.AddRange(lastAddicted.Select(a => $"{a} addiction{(lastWithdrawal.Contains(a) ? " (withdrawal)" : "")}"));
        return lines;
    }
}

/// <summary>The farmer finished choosing to eat or drink something.</summary>
[HarmonyPatch(typeof(Farmer), nameof(Farmer.eatObject))]
internal static class EatObjectPatch
{
    private static void Postfix(Farmer __instance, StardewValley.Object o)
    {
        try { if (__instance.IsLocalPlayer) ModEntry.Instance?.OnFarmerAte(o); }
        catch (Exception e) { ModEntry.Instance?.LogPatchError(e); }
    }
}
