using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewBridge;

public partial class ModEntry
{
    private void RegisterCommands()
    {
        var c = Helper.ConsoleCommands;
        c.Add("rd_send", "Send an item to RimWorld. rd_send <name or (O)id> <count>", (_, a) =>
        {
            if (!Ready() || a.Length < 1) return;
            int count = a.Length > 1 && int.TryParse(a[1], out var n) ? n : 1;
            Send(new JObject { ["type"] = "item_send", ["item"] = a[0], ["count"] = count });
            Monitor.Log($"Sent {count} x {a[0]} to RimWorld.", LogLevel.Info);
        });
        c.Add("rd_hire", "Hire a farmhand (100g/day). rd_hire <name>", (_, a) =>
        {
            if (!Ready() || a.Length < 1) return;
            if (!data.Hands.Contains(a[0])) data.Hands.Add(a[0]);
            Send(new JObject { ["type"] = "hire", ["name"] = a[0] });
            Monitor.Log($"Hired {a[0]}. Wages: {DailyWage}g/day.", LogLevel.Info);
        });
        c.Add("rd_fire", "Dismiss a farmhand. rd_fire <name>", (_, a) =>
        {
            if (!Ready() || a.Length < 1) return;
            data.Hands.Remove(a[0]);
            Send(new JObject { ["type"] = "fire", ["name"] = a[0] });
        });
        c.Add("rd_priority", "Set a hand's chore priority 0 (off) to 4. rd_priority <name> <water|harvest|animals> <0-4>", (_, a) =>
        {
            if (!Ready() || a.Length < 3 || !int.TryParse(a[2], out var v)) { Monitor.Log("Usage: rd_priority <name> <water|harvest|animals> <0-4>", LogLevel.Warn); return; }
            Send(new JObject { ["type"] = "priority", ["name"] = a[0], ["task"] = a[1], ["value"] = v });
        });
        c.Add("rd_tend", "Tend your injuries with a Muscle Remedy or Fiber.", (_, _) => { if (Ready()) TendCommand(); });
        c.Add("rd_research", "Show research, or pick a target. rd_research [id]", (_, a) =>
        {
            if (!Ready()) return;
            if (a.Length > 0)
            {
                data.ResearchTarget = a[0];
                Send(new JObject { ["type"] = "research_set", ["id"] = a[0] });
            }
            PrintResearch();
        });
        c.Add("rd_gate", "Hide unresearched machine recipes. rd_gate on|off", (_, a) =>
        {
            if (!Ready() || a.Length < 1) return;
            data.GateRecipes = a[0] == "on";
            ApplyRecipeGating();
            Monitor.Log($"Recipe gating {(data.GateRecipes ? "ON" : "off")}.", LogLevel.Info);
        });
        c.Add("rd_moods", "Show villager moods reported by RimWorld.", (_, _) =>
        {
            foreach (var (name, mood) in npcMood.OrderBy(x => x.Value))
                Monitor.Log($"  {name,-10} {mood:0.00}{(npcBroken.Contains(name) ? "  (BREAK)" : "")}", LogLevel.Info);
        });
        c.Add("rd_test_raid", "Spawn test monsters locally. rd_test_raid <slime|bat|brute|skeleton|grub> <count>", (_, a) =>
        {
            if (!Ready()) return;
            SpawnMonsters(a.Length > 1 && int.TryParse(a[1], out var n) ? n : 3, a.Length > 0 ? a[0] : "slime");
        });
    }

    private bool Ready()
    {
        if (Context.IsWorldReady) return true;
        Monitor.Log("Load a save first.", LogLevel.Warn);
        return false;
    }
}
