using HarmonyLib;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>RimWorld mental breaks and inspirations, acted out in the valley. A hand on a food binge raids the food chests,
/// one in a tantrum knocks over fences and sprinklers, a sad wanderer drifts around the farm instead of working.
/// An inspired farmer crafts or cooks one iridium-quality item (creativity) or trades at better prices (trade).</summary>
public partial class ModEntry
{
    private const int BingeLimit = 8, SmashLimit = 3;   // per hand per day
    private readonly Dictionary<string, int> breakActs = new();       // "name|kind|day" -> acts done today
    private readonly Dictionary<string, int> nextBreakAct = new();    // name -> tick
    private readonly Dictionary<string, string> lastHandMind = new(); // name -> "break|inspiration" last seen
    internal string FarmerInspiration = "", FarmerInspirationLabel = "";
    internal const double TradeBonus = 0.2;

    private JObject? HandInfo(string name) => LastHands.FirstOrDefault(h => (string?)h["name"] == name) as JObject;

    /// <summary>binge / tantrum / wander / sulk (any other break), or null when the hand is fine.</summary>
    internal string? BreakKind(string name)
    {
        string def = (string?)HandInfo(name)?["break"] ?? "";
        if (def == "") return null;
        if (def.StartsWith("Binging")) return "binge";
        if (def.Contains("Tantrum") || def is "Berserk" or "MurderousRage" or "FireStartingSpree" or "SadisticRage") return "tantrum";
        if (def.StartsWith("Wander") || def is "GiveUpExit" or "RunWild" or "Roaming" or "PanicFlee") return "wander";
        return "sulk";
    }

    /// <summary>HUD notes when a hand's break or inspiration starts or ends (called after every hands report).</summary>
    private void NoteHandMinds()
    {
        foreach (var h in LastHands.OfType<JObject>())
        {
            string name = (string)h["name"]!;
            string brk = (string?)h["break_label"] ?? "", insp = (string?)h["inspiration_label"] ?? "";
            string now = brk + "|" + insp;
            lastHandMind.TryGetValue(name, out var before);
            lastHandMind[name] = now;
            if (before == now) continue;
            var (oldBrk, oldInsp) = before == null ? ("", "") : (before.Split('|')[0], before.Split('|')[1]);
            if (brk != "" && brk != oldBrk) Hud($"{name} is having a mental break: {brk}.", HUDMessage.error_type);
            else if (brk == "" && oldBrk != "") Hud($"{name} has calmed down.", HUDMessage.achievement_type);
            if (insp != "" && insp != oldInsp) Hud($"{name} is inspired: {insp}!", HUDMessage.achievement_type);
        }
    }

    // ---------- hands: acting out a break ----------
    private int ActsToday(string name, string kind) => breakActs.TryGetValue($"{name}|{kind}|{Game1.Date.TotalDays}", out var n) ? n : 0;
    private void CountAct(string name, string kind) => breakActs[$"{name}|{kind}|{Game1.Date.TotalDays}"] = ActsToday(name, kind) + 1;

    /// <summary>An idle hand in a break gets its next break "job" (called from TickHands before chores).</summary>
    private void PlanBreakAct(HandActor a)
    {
        string? kind = BreakKind(a.Name);
        if (kind == null || kind == "sulk" || a.Drafted || !Calm()) return;
        if (Game1.timeOfDay < 600 || Game1.timeOfDay >= 2200) return;
        if (nextBreakAct.TryGetValue(a.Name, out int next) && Game1.ticks < next) return;
        var farm = Game1.getFarm();
        var at = (a.Inside ? a.Door ?? a.Home : a.Pos) / 64f;

        switch (kind)
        {
            case "binge" when ActsToday(a.Name, "binge") < BingeLimit:
                var food = FindBestFood(at);
                if (food == null) break;
                a.Jobs.Enqueue(new HandJob { Kind = "binge", Owner = a.Name, Tile = food.Value.chest.TileLocation });
                nextBreakAct[a.Name] = (int)Game1.ticks + 600;
                return;
            case "tantrum" when ActsToday(a.Name, "smash") < SmashLimit:
                var target = farm.objects.Pairs.Where(p => Smashable(p.Value) && Vector2.Distance(p.Key, at) < 20)
                    .OrderBy(p => Vector2.DistanceSquared(p.Key, at)).Select(p => (Vector2?)p.Key).FirstOrDefault();
                if (target == null) break;
                a.Jobs.Enqueue(new HandJob { Kind = "smash", Owner = a.Name, Tile = target.Value });
                nextBreakAct[a.Name] = (int)Game1.ticks + 900;
                return;
            case "wander":
                var rng = Game1.random;
                for (int i = 0; i < 12; i++)
                {
                    var t = new Point((int)at.X + rng.Next(-8, 9), (int)at.Y + rng.Next(-8, 9));
                    if (!Walkable(t)) continue;
                    a.Jobs.Enqueue(new HandJob { Kind = "wander", Owner = a.Name, Tile = new Vector2(t.X, t.Y - 1) });
                    break;
                }
                nextBreakAct[a.Name] = (int)Game1.ticks + 240 + rng.Next(240);
                return;
        }
        nextBreakAct[a.Name] = (int)Game1.ticks + 600;   // nothing to binge on or smash: sulk a while
    }

    /// <summary>Things a tantrum knocks over: fences, sprinklers, scarecrows (they drop as items you can put back).</summary>
    private static bool Smashable(SObject o) => o is Fence { isGate.Value: false } || o.IsSprinkler() || o.IsScarecrow();

    /// <summary>At the food chest: wolf down two items.</summary>
    private void Binge(HandJob j)
    {
        int ate = 0;
        for (int i = 0; i < 2; i++)
        {
            var pick = FindBestFood(j.Tile);
            if (pick == null) break;
            var (chest, item) = pick.Value;
            var eaten = item.getOne();
            item.Stack--;
            if (item.Stack <= 0) chest.Items.Remove(item);
            ApplyMeal(j.Owner, eaten, false);
            ate++;
        }
        if (ate == 0) return;
        if (ActsToday(j.Owner, "binge") == 0) Hud($"{j.Owner} is binging on the food in your chests!", HUDMessage.error_type);
        CountAct(j.Owner, "binge");
        Defenses.Add($"{j.Owner} binged on food");
    }

    /// <summary>At the target: knock it over. It drops as an item, so the player can put it back.</summary>
    private void Smash(HandJob j)
    {
        var farm = Game1.getFarm();
        if (!farm.objects.TryGetValue(j.Tile, out var o) || !Smashable(o)) return;
        farm.objects.Remove(j.Tile);
        var drop = ItemRegistry.Create(o.QualifiedItemId, 1, allowNull: true);
        if (drop != null) Game1.createItemDebris(drop, j.Tile * 64f + new Vector2(32, 32), -1, farm);
        if (Game1.currentLocation is Farm) Game1.playSound("axchop");
        Hud($"{j.Owner} knocked over your {o.DisplayName} in a tantrum!", HUDMessage.error_type);
        CountAct(j.Owner, "smash");
        Defenses.Add($"{j.Owner} knocked over a {o.DisplayName}");
    }

    // ---------- farmer: inspirations ----------
    private void OnFarmerMind(JObject msg)
    {
        string insp = (string?)msg["inspiration"] ?? "";
        string label = (string?)msg["inspiration_label"] ?? "";
        if (insp != "" && insp != FarmerInspiration)
            Hud(insp switch
            {
                "Inspired_Creativity" => "Inspired creativity! The next thing you cook or craft will be iridium quality.",
                "Inspired_Trade" => $"Inspired trade! Shops sell to you {TradeBonus:P0} cheaper and pay {TradeBonus:P0} more.",
                _ => $"You feel {label}!"
            }, HUDMessage.achievement_type);
        FarmerInspiration = insp;
        FarmerInspirationLabel = label;
    }

    internal bool TradeInspired => FarmerInspiration == "Inspired_Trade";

    /// <summary>A freshly crafted or cooked item: with inspired creativity it comes out iridium, and the inspiration is spent.</summary>
    internal void OnCrafted(Item item, bool cooking)
    {
        if (FarmerInspiration != "Inspired_Creativity" || item is not SObject o || o.bigCraftable.Value) return;
        if (!cooking && o.Edibility <= -300 && o.Category != SObject.artisanGoodsCategory) return;   // tools of the trade (sprinklers, fences) have no quality
        o.Quality = SObject.bestQuality;
        FarmerInspiration = "";
        Send(new JObject { ["type"] = "use_inspiration", ["who"] = "farmer" });
        Hud($"Inspired: iridium-quality {o.DisplayName}!", HUDMessage.achievement_type);
    }

    /// <summary>Inspired trade: every shop's prices drop when it opens.</summary>
    private void DiscountShop(ShopMenu shop)
    {
        if (!TradeInspired) return;
        foreach (var stock in shop.itemPriceAndStock.Values)
            if (stock.Price > 0) stock.Price = Math.Max(1, (int)Math.Round(stock.Price * (1 - TradeBonus)));
    }

    // ---------- test tools ----------
    internal void DevInspire(string who, string def) => Send(new JObject { ["type"] = "dev_inspire", ["who"] = who, ["def"] = def });
    internal void DevBreak(string who, string def) => Send(new JObject { ["type"] = "dev_break", ["who"] = who, ["def"] = def });
    internal string? FirstHand => data.Hands.FirstOrDefault();

    /// <summary>Status shown for a hand: the break by name, plus any inspiration.</summary>
    internal static string StatusText(JObject info)
    {
        string brk = (string?)info["break_label"] ?? "", insp = (string?)info["inspiration_label"] ?? "";
        string s = brk != "" ? brk : (string)info["status"]!;
        return insp != "" ? $"{s}, {insp}" : s;
    }
}

/// <summary>Crafting or cooking a recipe: the new item lands in the page's held item when the hand was empty.</summary>
[HarmonyPatch(typeof(CraftingPage), "clickCraftingRecipe")]
internal static class CraftPatch
{
    private static void Prefix(CraftingPage __instance, out Item? __state) => __state = Traverse.Create(__instance).Field<Item>("heldItem").Value;

    private static void Postfix(CraftingPage __instance, Item? __state)
    {
        try
        {
            var held = Traverse.Create(__instance).Field<Item>("heldItem").Value;
            if (__state == null && held != null) ModEntry.Instance?.OnCrafted(held, __instance.cooking);
        }
        catch (Exception e) { ModEntry.Instance?.LogPatchError(e); }
    }
}

/// <summary>Inspired trade: shops and the shipping bin pay more.</summary>
[HarmonyPatch(typeof(SObject), nameof(SObject.sellToStorePrice))]
internal static class SellPricePatch
{
    private static void Postfix(ref int __result)
    {
        if (ModEntry.Instance?.TradeInspired == true && __result > 0) __result = (int)Math.Round(__result * (1 + ModEntry.TradeBonus));
    }
}
