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
    internal const int DailyWage = 100;

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
        int water = FarmCrops(farm).Count(c => !sprinklered.Contains(c.tile) && !c.dirt.readyForHarvest());   // a fully grown crop needs no water
        int harvest = FarmCrops(farm).Count(c => c.dirt.readyForHarvest());
        int animals = farm.getAllFarmAnimals().Count(a => a.fullness.Value < 200);
        return new JObject { ["water"] = water, ["harvest"] = harvest, ["animals"] = animals };
    }
}
