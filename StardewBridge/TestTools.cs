using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewBridge;

/// <summary>Developer conveniences, exposed in the menu's Test tab. Nothing here is saved until you sleep.</summary>
public partial class ModEntry
{
    private string? forcedHurtKind;

    /// <summary>Remove weeds, twigs and small stones from the farm (not boulders, stumps, logs, trees or grass).</summary>
    internal void ClearDebris()
    {
        var farm = Game1.getFarm();
        var keys = farm.objects.Pairs
            .Where(kv => kv.Value.IsWeeds() || kv.Value.IsTwig() || kv.Value.IsBreakableStone())
            .Select(kv => kv.Key).ToList();
        foreach (var k in keys) farm.objects.Remove(k);
        Hud($"Cleared {keys.Count} weeds, twigs and stones.", HUDMessage.achievement_type);
    }

    /// <summary>Till and plant a 6x4 parsnip patch just south-east of the farmhouse door.</summary>
    internal void PlantTestPatch()
    {
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        int planted = 0;
        for (int dx = 2; dx < 8; dx++)
            for (int dy = 2; dy < 6; dy++)
            {
                var tile = new Vector2(door.X + dx, door.Y + dy);
                farm.objects.Remove(tile);
                if (farm.terrainFeatures.ContainsKey(tile)) farm.terrainFeatures.Remove(tile);
                if (!farm.isTilePassable(tile) && !farm.CanItemBePlacedHere(tile)) continue;
                var dirt = new HoeDirt(HoeDirt.dry);
                farm.terrainFeatures.Add(tile, dirt);
                if (dirt.plant("472", Game1.player, false)) planted++;
            }
        Hud($"Planted {planted} parsnips by the farmhouse.", HUDMessage.achievement_type);
    }

    /// <summary>Open the same placement screen Robin uses, offering a free, instant Bunkhouse.</summary>
    internal void PlaceBunkhouse()
    {
        Game1.activeClickableMenu = new StardewValley.Menus.CarpenterMenu(TestBuilder);
    }

    internal void GrowAllCrops()
    {
        int n = 0;
        foreach (var (_, dirt) in FarmCrops(Game1.getFarm()))
        {
            dirt.crop.growCompletely();
            n++;
        }
        Hud($"{n} crops are ready to harvest.", HUDMessage.achievement_type);
    }

    internal void GiveTestKit()
    {
        Game1.player.Money += 2000;
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)472", 20)); // parsnip seeds
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)351", 3));  // muscle remedy
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)771", 5));  // fiber
        Hud("Test kit: 2000g, 20 parsnip seeds, 3 Muscle Remedy, 5 Fiber.", HUDMessage.achievement_type);
    }

    /// <summary>Vanilla Stardew has no guns; the Galaxy Slingshot is the strongest ranged weapon. Plus ammo and a sword.</summary>
    internal void GiveWeaponsKit()
    {
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(W)34"));          // Galaxy Slingshot
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)390", 200));    // stone (basic ammo)
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)380", 100));    // iron ore (stronger ammo)
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(W)4"));           // Galaxy Sword
        Hud("Weapons kit: Galaxy Slingshot, 200 stone, 100 iron ore, Galaxy Sword.", HUDMessage.achievement_type);
    }

    /// <summary>Lose HP so the hit is reported to RimWorld as the given damage type.</summary>
    internal void HurtMe(string kind, int hp)
    {
        forcedHurtKind = kind;
        Game1.player.health = Math.Max(1, Game1.player.health - hp);
    }

    internal void SendMoodEventToAll(params string[] kinds)
    {
        foreach (var npc in Utility.getAllVillagers().Where(n => n.CanSocialize))
            foreach (var kind in kinds)
                SendNpcEvent(npc.Name, kind);
        Hud("Mood events sent to every villager. Press 'Refresh data', then open the Villagers tab.", HUDMessage.achievement_type);
    }

    internal void FireRimWorldIncident(string def) => Send(new JObject { ["type"] = "dev_incident", ["def"] = def });
    internal void AddResearchPoints(int n) => Send(new JObject { ["type"] = "research_points", ["amount"] = n });
    internal void RefreshRimWorld() => Send(new JObject { ["type"] = "report" });
    internal void RunNightNow()
    {
        SendDayEnd();
        Hud("Running the RimWorld night. Results arrive in a few seconds.", HUDMessage.achievement_type);
    }
}
