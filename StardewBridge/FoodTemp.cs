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

    internal static bool IsFreezer(Chest c) => c.ItemId == FreezerId;

    /// <summary>Ice cream is meant to be frozen; everything else tastes bad straight from the freezer.</summary>
    private static bool MeantFrozen(Item i) => i.ItemId == "233";

    private bool MicrowaveReady() => pNodes.Values.Any(n => n.Kind == "Microwave" && n.Online);

    /// <summary>Preference order when hungry: cooked meal 4, snack 3, nutrient paste 2, raw 1.</summary>
    private static int FoodRank(int grade) => grade switch { 3 => 4, 2 => 3, 1 => 1, _ => 0 };

    private static bool IsRawFood(Item? i) => FoodGrade(i) == 1 && i is SObject o && o.Category != SObject.FishCategory;

    private (Chest chest, Item item)? FindBestFood(Vector2 from)
    {
        bool canHeat = MicrowaveReady();
        (Chest, Item)? best = null; int bestScore = 0; float bestDist = float.MaxValue;
        foreach (var c in FarmChests())
        {
            bool cold = IsFreezer(c);
            foreach (var it in c.Items)
            {
                int g = FoodGrade(it);
                if (g == 0) continue;
                // food that is frozen and cannot be thawed is a last resort; with a working microwave it is as good as anything
                bool bad = cold && !MeantFrozen(it) && !canHeat;
                int score = FoodRank(g) * 2 + (bad ? 0 : 1);
                float d = Vector2.DistanceSquared(c.TileLocation, from);
                if (score > bestScore || (score == bestScore && d < bestDist)) { best = (c, it); bestScore = score; bestDist = d; }
            }
        }
        return best;
    }

    private int CountFood(Func<Item?, bool> pred) => FarmChests().Sum(c => c.Items.Where(pred).Sum(i => i.Stack));

    /// <summary>A hungry hand eats the best food available from the nearest chest holding it.</summary>
    private void Eat(HandJob j)
    {
        if (j.Item == "paste")
        {
            if (!pNodes.TryGetValue(j.Tile, out var dn) || !dn.Online || GetD(dn.Obj, PasteKey) < 1) return;
            var disp = dn.Obj;
            SetD(disp, PasteKey, GetD(disp, PasteKey) - 1);
            if (actors.TryGetValue(j.Owner, out var pa) && FindDiningSeat(pa) != null) { pa.MealPaste = true; Defenses.Add($"{j.Owner} takes paste to the table"); return; }
            data.Hunger[j.Owner] = Math.Max(0, HungerOf(j.Owner) - 45);
            SendNpcEvent(j.Owner, "ate_paste");
            Defenses.Add($"{j.Owner} ate nutrient paste");
            return;
        }
        var pick = FindBestFood(j.Tile);
        if (pick == null) return;
        var (chest, item) = pick.Value;
        bool frozen = IsFreezer(chest) && !MeantFrozen(item);
        var eaten = item.getOne();
        item.Stack--;
        if (item.Stack <= 0) chest.Items.Remove(item);
        if (actors.TryGetValue(j.Owner, out var ha) && FindDiningSeat(ha) != null) { ha.Meal = eaten; ha.MealFrozen = frozen; Defenses.Add($"{j.Owner} takes a meal to the table"); return; }
        ApplyMeal(j.Owner, eaten, frozen);
    }

    /// <summary>Hunger goes down by the food's grade; the mood event says how it tasted. Frozen food that was not thawed is miserable.</summary>
    private void ApplyMeal(string who, Item food, bool unthawed)
    {
        int grade = FoodGrade(food);
        data.Hunger[who] = Math.Max(0, HungerOf(who) - grade switch { 3 => 70, 2 => 35, _ => 30 });
        string kind = unthawed ? "ate_frozen" : grade switch { 3 => "ate_meal", 2 => "ate_snack", _ => "ate_raw" };
        SendNpcEvent(who, kind);
        Defenses.Add($"{who} ate {(unthawed ? "frozen food" : grade == 3 ? "a meal" : grade == 2 ? "a snack" : "raw food")}");
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
            if (!n.Online) continue;
            if (n.Loc != null) { if (!(a.Inside && a.Bunk?.GetIndoors() == n.Loc)) continue; }   // an appliance indoors only serves that room
            else if (Vector2.Distance(n.Tile, at) > radius) continue;
            if (n.Kind == "Heater" && t < 21) t = Math.Min(21, t + 30);
            else if (n.Kind == "Cooler" && t > 24) t = Math.Max(24, t - 25);
        }
        return t;
    }

    private static bool Perishable(Item? i) => FoodGrade(i) is 1 or 3;

    /// <summary>Overnight, about a fifth of the perishable food (raw crops, eggs, milk, fish, cooked meals) in ordinary chests spoils.
    /// Chests within 3 tiles of a powered freezer keep everything.</summary>
    internal (int lost, int saved) SpoilFood(double chance = 0.2)
    {
        int lost = 0, saved = 0;
        foreach (var chest in FarmChests().ToList())
        {
            bool frozen = IsFreezer(chest);
            foreach (var item in chest.Items.Where(Perishable).ToList())
            {
                int rot = 0;
                for (int u = 0; u < item.Stack; u++) if (Game1.random.NextDouble() < chance) rot++;
                if (rot == 0) continue;
                if (frozen) { saved += rot; continue; }
                item.Stack -= rot;
                lost += rot;
                if (item.Stack <= 0) chest.Items.Remove(item);
            }
        }
        return (lost, saved);
    }

    private void SpoilOvernight()
    {
        var (lost, saved) = SpoilFood();
        if (lost > 0) Hud($"{lost} food item{(lost == 1 ? "" : "s")} spoiled overnight.{(saved > 0 ? $" Freezers saved {saved}." : " Keep food in a freezer to stop it spoiling.")}", HUDMessage.error_type);
        else if (saved > 0) Hud($"Freezers kept {saved} food item{(saved == 1 ? "" : "s")} from spoiling.", HUDMessage.achievement_type);
    }

    internal (int paste, int freezers, int microwaves) StorageSummary() =>
        ((int)pNodes.Values.Where(n => n.Kind == "Dispenser").Sum(n => GetD(n.Obj, PasteKey)),
         FarmChests().Count(IsFreezer), pNodes.Values.Count(n => n.Kind == "Microwave" && n.Online));

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
