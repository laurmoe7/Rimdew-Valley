using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>Food and temperature for farmhands. Hunger rises through the day; hands eat from farm chests (cooked meals are best,
/// raw crops are a bad mood). An electric stove turns raw crops into Simple Meals. Outdoor temperature follows season, weather,
/// time of day and storyteller heat waves / cold snaps; bunkhouses insulate, and powered heaters and coolers push hands'
/// surroundings toward a comfortable range. Both feed RimWorld mood thoughts through the existing mood-event channel.</summary>
public partial class ModEntry
{
    private const double HungerPerHour = 5, EatAt = 50, HungryAt = 70, StarvingAt = 95;
    private const float ClimateRadius = 7f;

    private double tempEventOffset;    // heat wave / cold snap, for the current day
    internal void SetTempEvent(double offset) => tempEventOffset = offset;

    // ---------- hunger ----------
    internal double HungerOf(string name) => data.Hunger.TryGetValue(name, out var h) ? h : 25;

    private readonly Dictionary<string, int> hungerStage = new();   // 0 fine, 1 told RimWorld "hungry", 2 told "starving"
    private readonly Dictionary<string, int> climateSent = new();

    private void TickFoodTemp(int ticks)
    {
        if (ticks % 30 != 0 || data.Hands.Count == 0) return;
        double hours = Game1.shouldTimePass() ? 30 * (1000.0 / 60) / 700.0 / 60.0 : 0;

        foreach (var name in data.Hands)
        {
            double h = Math.Min(100, HungerOf(name) + HungerPerHour * hours);
            data.Hunger[name] = h;
            hungerStage.TryGetValue(name, out int stage);
            if (h >= StarvingAt && stage < 2) { hungerStage[name] = 2; SendNpcEvent(name, "starving"); }
            else if (h >= HungryAt && stage < 1) { hungerStage[name] = 1; SendNpcEvent(name, "hungry"); }
            else if (h < EatAt && stage != 0) hungerStage[name] = 0;
        }

        if (ticks % 600 == 0) CheckClimate(ticks);
    }

    // ---------- food items ----------
    /// <summary>0 = not food, 1 = raw, 2 = snack/artisan, 3 = cooked meal.</summary>
    private static int FoodGrade(Item? i)
    {
        if (i is not SObject o || o.bigCraftable.Value) return 0;
        if (o.ItemId == PId("SimpleMeal")) return 3;
        if (o.Edibility <= 0) return 0;
        return o.Category switch
        {
            SObject.CookingCategory => 3,
            SObject.artisanGoodsCategory => 2,
            SObject.VegetableCategory or SObject.FruitsCategory or SObject.EggCategory or SObject.MilkCategory or SObject.FishCategory => 1,
            _ => 0
        };
    }

    private static bool IsRawFood(Item? i) => FoodGrade(i) == 1 && i is SObject o && o.Category != SObject.FishCategory;

    private (Chest chest, Item item)? FindBestFood(Vector2 from)
    {
        (Chest, Item)? best = null; int bestGrade = 0; float bestDist = float.MaxValue;
        foreach (var c in FarmChests())
            foreach (var it in c.Items)
            {
                int g = FoodGrade(it);
                if (g == 0) continue;
                float d = Vector2.DistanceSquared(c.TileLocation, from);
                if (g > bestGrade || (g == bestGrade && d < bestDist)) { best = (c, it); bestGrade = g; bestDist = d; }
            }
        return best;
    }

    private int CountFood(Func<Item?, bool> pred) => FarmChests().Sum(c => c.Items.Where(pred).Sum(i => i.Stack));

    /// <summary>A hungry hand eats the best food available from the nearest chest holding it.</summary>
    private void Eat(HandJob j)
    {
        var pick = FindBestFood(j.Tile);
        Monitor.Log($"[food] {j.Owner} tries to eat at {j.Tile}: {(pick == null ? "no food found; chests=" + FarmChests().Count() : pick.Value.item.Name)}", LogLevel.Debug);
        if (pick == null) return;
        var (chest, item) = pick.Value;
        int grade = FoodGrade(item);
        item.Stack--;
        if (item.Stack <= 0) chest.Items.Remove(item);
        double before = HungerOf(j.Owner);
        data.Hunger[j.Owner] = Math.Max(0, before - grade switch { 3 => 70, 2 => 35, _ => 30 });
        string kind = grade switch { 3 => "ate_meal", 2 => "ate_snack", _ => "ate_raw" };
        SendNpcEvent(j.Owner, kind);
        Defenses.Add($"{j.Owner} ate {(grade == 3 ? "a meal" : grade == 2 ? "a snack" : "raw food")}");
        if (Game1.currentLocation is StardewValley.Farm) Game1.playSound("eat");
    }

    // ---------- temperature ----------
    internal double OutdoorTemp()
    {
        double season = Game1.currentSeason switch { "spring" => 13, "summer" => 27, "fall" => 11, _ => -5 };
        double hour = Game1.timeOfDay / 100 + (Game1.timeOfDay % 100) / 60.0;
        double diurnal = 5 * Math.Sin((hour - 9) / 24.0 * 2 * Math.PI);   // warmest 15:00, coldest 03:00
        double weather = Game1.isGreenRain ? 0 : Game1.isLightning ? -4 : Game1.isRaining ? -3 : Game1.isSnowing ? -3 : 0;
        return season + diurnal + weather + tempEventOffset;
    }

    /// <summary>Temperature a hand feels where they are: outdoors, or inside their insulated bunkhouse; then heaters/coolers nearby.</summary>
    private double TempFor(HandActor a)
    {
        double outdoor = OutdoorTemp();
        double t = outdoor;
        Vector2 at = a.Pos / 64f;
        float radius = ClimateRadius;
        if (a.Inside && a.Bunk != null)
        {
            t = 18 + (outdoor - 18) * 0.35;                              // insulation
            at = new Vector2(a.Bunk.tileX.Value + a.Bunk.tilesWide.Value / 2f, a.Bunk.tileY.Value + a.Bunk.tilesHigh.Value / 2f);
            radius += 2;                                                  // a heater beside the building warms the inside
        }
        foreach (var n in pNodes.Values)
        {
            if (!n.Online || Vector2.Distance(n.Tile, at) > radius) continue;
            if (n.Kind == "Heater" && t < 21) t = Math.Min(21, t + 30);
            else if (n.Kind == "Cooler" && t > 24) t = Math.Max(24, t - 25);
        }
        return t;
    }

    internal (int meals, int snacks, int raw) FoodSummary() =>
        (CountFood(i => FoodGrade(i) == 3), CountFood(i => FoodGrade(i) == 2), CountFood(i => FoodGrade(i) == 1));

    internal (double temp, string status) HandClimate(string name) =>
        actors.TryGetValue(name, out var a) ? (TempFor(a), ClimateStatus(TempFor(a))) : (OutdoorTemp(), "");

    private static string ClimateStatus(double t) => t < -5 ? "freezing" : t < 5 ? "cold" : t > 38 ? "sweltering" : t > 32 ? "hot" : "";

    private void CheckClimate(int ticks)
    {
        foreach (var a in actors.Values)
        {
            string status = ClimateStatus(TempFor(a));
            if (status == "") continue;
            string key = $"{a.Name}|{status}|{Game1.stats.DaysPlayed}|{Game1.timeOfDay / 1000}";
            if (climateSent.ContainsKey(key)) continue;
            climateSent[key] = ticks;
            SendNpcEvent(a.Name, status);
            Defenses.Add($"{a.Name} is {status}");
        }
        if (climateSent.Count > 400) climateSent.Clear();
    }
}
