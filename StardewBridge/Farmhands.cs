using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;

namespace StardewBridge;

/// <summary>Feature 4: hired farmhands are RimWorld pawns with skills, traits and moods. Each night Stardew sends
/// the farm's chores; RimWorld says how many each hand got through, and we apply that to the farm.</summary>
public partial class ModEntry
{
    private const int DailyWage = 100;

    private bool PayHands()
    {
        if (data.Hands.Count == 0) return true;
        int total = DailyWage * data.Hands.Count;
        if (Game1.player.Money < total) return false;
        Game1.player.Money -= total;
        return true;
    }

    private IEnumerable<(Vector2 tile, HoeDirt dirt)> FarmCrops(Farm farm)
    {
        foreach (var (tile, feature) in farm.terrainFeatures.Pairs)
            if (feature is HoeDirt { crop: not null } dirt && !dirt.crop.dead.Value)
                yield return (tile, dirt);
    }

    private HashSet<Vector2> SprinklerTiles(Farm farm)
    {
        var set = new HashSet<Vector2>();
        foreach (var obj in farm.objects.Values)
            if (obj.IsSprinkler())
                foreach (var t in obj.GetSprinklerTiles()) set.Add(t);
        return set;
    }

    private JObject BuildBacklog(Farm farm)
    {
        var sprinklered = SprinklerTiles(farm);
        int water = FarmCrops(farm).Count(c => !sprinklered.Contains(c.tile));
        int harvest = FarmCrops(farm).Count(c => c.dirt.readyForHarvest());
        int animals = farm.getAllFarmAnimals().Count(a => a.fullness.Value < 200);
        return new JObject { ["water"] = water, ["harvest"] = harvest, ["animals"] = animals };
    }

    private void OnWorkDone(JObject msg)
    {
        if (data.Hands.Count == 0) return;
        var farm = Game1.getFarm();
        int wantWater = (int)msg["water"]!, wantHarvest = (int)msg["harvest"]!, wantAnimals = (int)msg["animals"]!;

        var sprinklered = SprinklerTiles(farm);
        int watered = 0;
        foreach (var (tile, dirt) in FarmCrops(farm))
        {
            if (watered >= wantWater) break;
            if (sprinklered.Contains(tile) || dirt.state.Value == HoeDirt.watered) continue;
            dirt.state.Value = HoeDirt.watered;
            watered++;
        }

        int harvested = 0;
        var bin = farm.getShippingBin(Game1.player);
        foreach (var (_, dirt) in FarmCrops(farm).ToList())
        {
            if (harvested >= wantHarvest) break;
            if (!dirt.readyForHarvest()) continue;
            var crop = dirt.crop;
            var produce = ItemRegistry.Create(crop.indexOfHarvest.Value, 1, allowNull: true);
            if (produce == null) continue;
            bin.Add(produce); // hands drop the harvest straight into the shipping bin
            var cropData = crop.GetData();
            if (cropData != null && cropData.RegrowDays > 0)
            {
                crop.fullyGrown.Value = true;
                crop.dayOfCurrentPhase.Value = cropData.RegrowDays;
            }
            else dirt.destroyCrop(false);
            harvested++;
        }

        int fed = 0;
        foreach (var animal in farm.getAllFarmAnimals())
        {
            if (fed >= wantAnimals) break;
            if (animal.fullness.Value >= 200) continue;
            animal.fullness.Value = 255;
            fed++;
        }

        if (watered + harvested + fed > 0)
            Hud($"Farmhands: watered {watered}, harvested {harvested}, fed {fed}.", HUDMessage.achievement_type);
        foreach (var h in (JArray)msg["hands"]!)
            Monitor.Log($"  {(string)h["name"]!}: {(string)h["status"]!}, mood {(double)h["mood"]!:0.00}, " +
                        $"Plants {(int)h["plants"]!}, Animals {(int)h["animals"]!}, worked {(double)h["hours"]!}h", LogLevel.Info);
    }
}
