using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>Idle farmhands look after the valley between chores: eat when hungry, fetch wood or copper ore from a farm chest
/// and refuel generators / reload turrets, cook raw crops at a powered stove, and patch up damaged sandbags.
/// Reloading earns Shooting XP.</summary>
public partial class ModEntry
{
    private const string WoodId = "(O)388", OreId = "(O)378", FiberId = "(O)771";
    private const double RefuelBelowHours = 8, ReloadBelowAmmo = 40;
    private const int RepairBelowHp = 60;

    private static bool IsMaintJob(HandJob j) => j.Kind is "refuel" or "reload" or "repair" or "cook" or "eat" or "stock" or "heat";

    private IEnumerable<Chest> FarmChests() =>
        Game1.getFarm().objects.Values.OfType<Chest>().Where(c => c.playerChest.Value && c.SpecialChestType == Chest.SpecialChestTypes.None);

    /// <summary>What a job's <c>Item</c> key means: a qualified item id, or "raw" for any raw crop.</summary>
    private static Func<Item?, bool> Matcher(string key) => key == "raw" ? IsRawFood : i => i?.QualifiedItemId == key;

    private int ChestStock(string key) => CountFood(Matcher(key));

    private Chest? NearestChestWith(string key, Vector2 tile) =>
        FarmChests().Where(c => c.Items.Any(Matcher(key))).OrderBy(c => Vector2.DistanceSquared(c.TileLocation, tile)).FirstOrDefault();

    /// <summary>Take up to <paramref name="want"/> matching items out of the chest. Returns what came out (stacks are split).</summary>
    private static List<Item> TakeFromChest(Chest chest, Func<Item?, bool> pred, int want)
    {
        var taken = new List<Item>();
        int got = 0;
        foreach (var item in chest.Items.Where(pred).ToList())
        {
            int take = Math.Min(want - got, item.Stack);
            var piece = item.getOne(); piece.Stack = take;
            taken.Add(piece);
            item.Stack -= take;
            got += take;
            if (item.Stack <= 0) chest.Items.Remove(item);
            if (got >= want) break;
        }
        return taken;
    }

    private bool Calm() => !Game1.getFarm().characters.OfType<StardewValley.Monsters.Monster>().Any(IsTarget);

    /// <summary>Every couple of seconds, give idle hands something to do.</summary>
    private void TickMaintenance()
    {
        if (Game1.ticks % 90 != 0 || actors.Count == 0 || !Calm()) return;

        var reserved = new HashSet<Vector2>();
        foreach (var a in actors.Values)
        {
            if (a.Current is { } c && IsMaintJob(c)) reserved.Add(c.Tile);
            foreach (var j in a.Jobs.Where(IsMaintJob)) reserved.Add(j.Tile);
        }
        var idle = actors.Values.Where(a => a.Current == null && a.Jobs.Count == 0 && HandCanFight(a) && !HasMeal(a)).ToList();
        if (idle.Count == 0) return;

        // 1. hungry hands eat first (day or night)
        foreach (var hand in idle.ToList())
        {
            if (HungerOf(hand.Name) < EatAt) continue;
            var food = FindBestFood(hand.Pos / 64f);
            int chestRank = food == null ? 0 : FoodRank(FoodGrade(food.Value.item));
            // a stocked, powered nutrient paste dispenser beats raw crops (but not snacks or cooked meals)
            var disp = pNodes.Values.Where(n => n.Kind == "Dispenser" && n.Online && GetD(n.Obj, PasteKey) >= 1)
                                    .OrderBy(n => Vector2.DistanceSquared(n.Pos * 64f, hand.Pos)).FirstOrDefault();
            var micro = pNodes.Values.Where(n => n.Kind == "Microwave" && n.Online).OrderBy(n => Vector2.DistanceSquared(n.Pos * 64f, hand.Pos)).FirstOrDefault();
            bool frozenPick = food != null && IsFreezer(food.Value.chest) && !MeantFrozen(food.Value.item);
            if (disp != null && chestRank < 2)
            {
                idle.Remove(hand);
                hand.Jobs.Enqueue(new HandJob { Kind = "eat", Owner = hand.Name, Tile = disp.Tile, Item = "paste" });
                Monitor.Log($"[maint] {hand.Name} is hungry ({HungerOf(hand.Name):0}) and goes to the nutrient dispenser at {disp.Pos.X},{disp.Pos.Y}", LogLevel.Debug);
            }
            else if (food != null && frozenPick && micro != null)
            {
                idle.Remove(hand);
                var f = food.Value;
                hand.Jobs.Enqueue(new HandJob { Kind = "heat", Owner = hand.Name, Tile = micro.Tile, Via = f.chest.TileLocation, Item = f.item.QualifiedItemId, Count = 1 });
                Monitor.Log($"[maint] {hand.Name} is hungry ({HungerOf(hand.Name):0}): thaw {f.item.Name} from the freezer at {f.chest.TileLocation.X},{f.chest.TileLocation.Y} in the microwave at {micro.Pos.X},{micro.Pos.Y}", LogLevel.Debug);
            }
            else if (food != null)
            {
                idle.Remove(hand);
                hand.Jobs.Enqueue(new HandJob { Kind = "eat", Owner = hand.Name, Tile = food.Value.chest.TileLocation });
                Monitor.Log($"[maint] {hand.Name} is hungry ({HungerOf(hand.Name):0}) and goes to eat from the chest at {food.Value.chest.TileLocation.X},{food.Value.chest.TileLocation.Y}", LogLevel.Debug);
            }
        }

        // 2. upkeep, in daylight hours only
        if (idle.Count == 0 || Game1.timeOfDay < 600 || Game1.timeOfDay >= 2200) return;

        int woodLeft = ChestStock(WoodId), oreLeft = ChestStock(OreId), fiberLeft = ChestStock(FiberId), rawLeft = ChestStock("raw");
        int meals = CountFood(i => FoodGrade(i) == 3);
        bool wantMeals = meals < Math.Max(2, data.Hands.Count * 2);

        var tasks = new List<(Vector2 tile, Vector2 pos, string kind, string item, int count)>();
        foreach (var n in pNodes.Values)
        {
            if (reserved.Contains(n.Tile)) continue;
            if (n.Kind == "Generator" && GetD(n.Obj, FuelKey) <= RefuelBelowHours && woodLeft > 0 && GridWantsPower(n))
            { int c = (int)Math.Min(5, Math.Min(woodLeft, Math.Ceiling((MaxFuelHours - GetD(n.Obj, FuelKey)) / HoursPerWood))); woodLeft -= c; tasks.Add((n.Tile, n.Pos, "refuel", WoodId, c)); }
            else if (n.Kind == "Turret" && GetD(n.Obj, AmmoKey) <= ReloadBelowAmmo && oreLeft > 0)
            { int c = (int)Math.Min(10, Math.Min(oreLeft, Math.Floor((MaxAmmo - GetD(n.Obj, AmmoKey)) / AmmoPerOre))); oreLeft -= c; tasks.Add((n.Tile, n.Pos, "reload", OreId, c)); }
            else if (n.Kind == "Dispenser" && n.Online && GetD(n.Obj, PasteKey) < 15 && rawLeft >= 1)
            { int c = (int)Math.Min(10, Math.Min(rawLeft, 30 - GetD(n.Obj, PasteKey))); rawLeft -= c; tasks.Add((n.Tile, n.Pos, "stock", "raw", c)); }
            else if (n.Kind == "Stove" && n.Online && wantMeals && rawLeft >= 2)
            { rawLeft -= 2; wantMeals = false; tasks.Add((n.Tile, n.Pos, "cook", "raw", 2)); }
        }
        foreach (var (tile, o) in Game1.getFarm().objects.Pairs)
            if (IsSandbag(o) && !reserved.Contains(tile) && fiberLeft > 0 && (int)GetD(o, HpKey, SandbagHp) <= RepairBelowHp)
            { fiberLeft--; tasks.Add((tile, tile, "repair", FiberId, 1)); }

        foreach (var (tile, pos, kind, item, count) in tasks)
        {
            if (idle.Count == 0) break;
            if (count <= 0) continue;
            var chest = NearestChestWith(item, pos);
            if (chest == null) continue;
            var hand = idle.OrderBy(a => Vector2.DistanceSquared(a.Pos, pos * 64f)).First();
            idle.Remove(hand);
            hand.Jobs.Enqueue(new HandJob { Kind = kind, Owner = hand.Name, Tile = tile, Via = chest.TileLocation, Item = item, Count = count });
            Monitor.Log($"[maint] {hand.Name}: {kind} at {tile.X},{tile.Y} ({count} x {item}) from chest at {chest.TileLocation.X},{chest.TileLocation.Y}", LogLevel.Debug);
        }

        // 3. harvest crops the moment they are ready (hands whose Harvest priority is off stay out of the fields)
        if (idle.Count == 0) return;
        var claimed = new HashSet<Vector2>();
        foreach (var a in actors.Values)
        {
            if (a.Current is { Kind: "harvest" } cur) claimed.Add(cur.Tile);
            foreach (var j in a.Jobs.Where(j => j.Kind == "harvest")) claimed.Add(j.Tile);
        }
        var ready = FarmCrops(Game1.getFarm()).Where(c => c.dirt.readyForHarvest() && !claimed.Contains(c.tile)).ToList();
        foreach (var hand in idle.OrderBy(a => a.Name))
        {
            if (ready.Count == 0) break;
            if (data.Priorities.TryGetValue(hand.Name + "|harvest", out int prio) && prio == 0) continue;
            foreach (var c in PickNearest(ready, 8, hand.Pos, x => x.tile * 64f))
                hand.Jobs.Enqueue(new HandJob { Kind = "harvest", Tile = c.tile, Owner = hand.Name });
        }
    }

    /// <summary>A generator is only worth feeding if its grid has demand or a battery with room.</summary>
    private bool GridWantsPower(PNode gen) => gen.Grid < grids.Count && (grids[gen.Grid].Demand > 0 || grids[gen.Grid].Charge < grids[gen.Grid].Capacity - 1);

    /// <summary>At the chest: take the supplies. False if the chest ran dry (the job is dropped).</summary>
    private bool FetchSupplies(HandJob j)
    {
        j.Fetched = true;
        var chest = NearestChestWith(j.Item, j.Via ?? j.Tile);
        if (chest == null) return false;
        j.Held = TakeFromChest(chest, Matcher(j.Item), j.Count);
        j.Carry = j.Held.Sum(i => i.Stack);
        j.Via = chest.TileLocation;
        return j.Carry > 0;
    }

    private void ReturnHeld(HandJob j)
    {
        if (j.Held.Count == 0) return;
        var chest = j.Via.HasValue && Game1.getFarm().objects.TryGetValue(j.Via.Value, out var o) && o is Chest c ? c : FarmChests().FirstOrDefault();
        foreach (var i in j.Held) chest?.addItem(i);
        j.Held.Clear();
    }

    /// <summary>At the machine (or the chest, for eating): do the work.</summary>
    private void DoMaintenance(HandJob j)
    {
        if (j.Kind == "eat") { Eat(j); return; }
        if (!j.Fetched && !FetchSupplies(j)) return;
        var farm = Game1.getFarm();
        SObject? o = null;
        if (j.Kind == "repair") farm.objects.TryGetValue(j.Tile, out o);
        else if (pNodes.TryGetValue(j.Tile, out var tnode)) o = tnode.Obj;   // appliances, on the farm or inside a building
        if (o == null) { ReturnHeld(j); return; }
        bool farmNow = Game1.currentLocation is StardewValley.Farm;
        switch (j.Kind)
        {
            case "refuel" when IsPowerObject(o):
                SetD(o, FuelKey, Math.Min(MaxFuelHours, GetD(o, FuelKey) + j.Carry * HoursPerWood));
                if (farmNow) Game1.playSound("woodyStep");
                Defenses.Add($"{j.Owner} refueled");
                break;
            case "reload" when IsPowerObject(o):
                SetD(o, AmmoKey, Math.Min(MaxAmmo, GetD(o, AmmoKey) + j.Carry * AmmoPerOre));
                if (actors.TryGetValue(j.Owner, out var a)) a.XpShooting++;
                if (farmNow) Game1.playSound("clank");
                Defenses.Add($"{j.Owner} reloaded");
                break;
            case "repair" when IsSandbag(o):
                o.modData[HpKey] = SandbagHp.ToString();
                if (farmNow) Game1.playSound("axchop");
                Defenses.Add($"{j.Owner} repaired sandbags");
                break;
            case "heat" when IsPowerObject(o) && pNodes.TryGetValue(j.Tile, out var mnode) && mnode.Online:
                foreach (var thawed in j.Held) ApplyMeal(j.Owner, thawed, false);
                if (j.Held.Count > 0) Defenses.Add($"{j.Owner} thawed it in the microwave");
                j.Held.Clear();
                if (farmNow) Game1.playSound("furnace");
                break;
            case "stock" when IsPowerObject(o):
                SetD(o, PasteKey, Math.Min(40, GetD(o, PasteKey) + j.Carry));
                j.Held.Clear();
                if (farmNow) Game1.playSound("dwop");
                Defenses.Add($"{j.Owner} stocked the dispenser");
                break;
            case "cook" when IsPowerObject(o) && pNodes.TryGetValue(j.Tile, out var stove) && stove.Online:
                var meal = ItemRegistry.Create("(O)" + PId("SimpleMeal"), 1);
                var chest = j.Via.HasValue && farm.objects.TryGetValue(j.Via.Value, out var co) && co is Chest cc ? cc : FarmChests().FirstOrDefault();
                if (chest != null && chest.addItem(meal) == null) { j.Held.Clear(); if (farmNow) Game1.playSound("dirtyHit"); Defenses.Add($"{j.Owner} cooked a meal"); }
                break;
            default:
                ReturnHeld(j);   // the machine is gone or unpowered: put the ingredients back
                break;
        }
        j.Carry = 0;
    }
}
