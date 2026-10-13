using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewBridge;

/// <summary>Feature 5: machines are RimWorld research projects. Earnings become research points; with
/// "rd_gate on", a machine's crafting recipe is hidden until its project is finished.</summary>
public partial class ModEntry
{
    // RimWorld project id -> Stardew crafting recipe name
    private static readonly Dictionary<string, string> RecipeFor = new()
    {
        ["Keg"] = "Keg",
        ["PreservesJar"] = "Preserves Jar",
        ["Loom"] = "Loom",
        ["CheesePress"] = "Cheese Press",
        ["MayonnaiseMachine"] = "Mayonnaise Machine",
        ["OilMaker"] = "Oil Maker",
        ["Furnace"] = "Furnace"
    };

    internal JArray lastProjects = new();

    private void OnResearchState(JObject msg)
    {
        lastProjects = (JArray)msg["projects"]!;
        foreach (var p in lastProjects)
            if ((bool)p["done"]!) data.Researched.Add((string)p["id"]!);
        ApplyRecipeGating();
    }

    private void OnResearchUnlocked(string id)
    {
        data.Researched.Add(id);
        Hud($"Research complete: {id}!", HUDMessage.achievement_type);
        ApplyRecipeGating();
    }

    /// <summary>Hide unresearched recipes (or restore everything we hid when gating is off).</summary>
    private void ApplyRecipeGating()
    {
        if (!Context.IsWorldReady) return;
        var recipes = Game1.player.craftingRecipes;
        foreach (var (id, recipe) in RecipeFor)
        {
            bool allowed = !data.GateRecipes || data.Researched.Contains(id);
            if (!allowed && recipes.ContainsKey(recipe))
            {
                recipes.Remove(recipe);
                data.GatedRemoved.Add(recipe);
            }
            else if (allowed && data.GatedRemoved.Remove(recipe))
                recipes.TryAdd(recipe, 0);
        }
    }

    private void PrintResearch()
    {
        if (lastProjects.Count == 0) { Monitor.Log("No research data yet (RimWorld connected? sleep a night).", LogLevel.Info); return; }
        foreach (var p in lastProjects)
            Monitor.Log($"  {((bool)p["done"]! ? "[done]" : "      ")} {(string)p["id"]!}: {(double)p["progress"]!:0}/{(double)p["cost"]!:0}", LogLevel.Info);
        Monitor.Log($"Current target: {(data.ResearchTarget == "" ? "(cheapest)" : data.ResearchTarget)}; gating {(data.GateRecipes ? "ON" : "off")}.", LogLevel.Info);
    }
}
