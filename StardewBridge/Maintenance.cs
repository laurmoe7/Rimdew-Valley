using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>Idle farmhands keep the defences running: they fetch wood or copper ore from a chest on the farm, then refuel
/// low generators and reload low turrets. Reloading earns Shooting XP.</summary>
public partial class ModEntry
{
    private const string WoodId = "(O)388", OreId = "(O)378";
    private const double RefuelBelowHours = 8, ReloadBelowAmmo = 40;

    private static bool IsMaintJob(HandJob j) => j.Kind is "refuel" or "reload";

    private IEnumerable<Chest> FarmChests() =>
        Game1.getFarm().objects.Values.OfType<Chest>().Where(c => c.playerChest.Value && c.SpecialChestType == Chest.SpecialChestTypes.None);

    private int ChestStock(string qid) => FarmChests().Sum(c => c.Items.Where(i => i?.QualifiedItemId == qid).Sum(i => i.Stack));

    private Chest? NearestChestWith(string qid, Vector2 tile) =>
        FarmChests().Where(c => c.Items.Any(i => i?.QualifiedItemId == qid)).OrderBy(c => Vector2.DistanceSquared(c.TileLocation, tile)).FirstOrDefault();

    /// <summary>Take up to <paramref name="want"/> of an item from the chest (stack by stack). Returns how many came out.</summary>
    private static int TakeFromChest(Chest chest, string qid, int want)
    {
        int got = 0;
        foreach (var item in chest.Items.Where(i => i?.QualifiedItemId == qid).ToList())
        {
            int take = Math.Min(want - got, item.Stack);
            item.Stack -= take;
            got += take;
            if (item.Stack <= 0) chest.Items.Remove(item);
            if (got >= want) break;
        }
        return got;
    }

    private bool Calm() => !Game1.getFarm().characters.OfType<StardewValley.Monsters.Monster>().Any(m => m.Health > 0);

    /// <summary>Every couple of seconds, give idle hands something to maintain.</summary>
    private void TickMaintenance()
    {
        if (Game1.ticks % 90 != 0 || actors.Count == 0 || !Calm()) return;
        if (Game1.timeOfDay < 600 || Game1.timeOfDay >= 2200) return;

        var reserved = new HashSet<Vector2>();
        foreach (var a in actors.Values)
        {
            if (a.Current is { } c && IsMaintJob(c)) reserved.Add(c.Tile);
            foreach (var j in a.Jobs.Where(IsMaintJob)) reserved.Add(j.Tile);
        }

        var idle = actors.Values.Where(a => a.Current == null && a.Jobs.Count == 0 && HandCanFight(a)).ToList();
        if (idle.Count == 0) return;

        int woodLeft = ChestStock(WoodId), oreLeft = ChestStock(OreId);
        foreach (var n in pNodes.Values)
        {
            if (idle.Count == 0) break;
            if (reserved.Contains(n.Tile)) continue;
            string? kind = null; int count = 0; string qid = "";
            if (n.Kind == "Generator" && GetD(n.Obj, FuelKey) <= RefuelBelowHours && woodLeft > 0 && GridWantsPower(n))
            { kind = "refuel"; qid = WoodId; count = (int)Math.Min(5, Math.Min(woodLeft, Math.Ceiling((MaxFuelHours - GetD(n.Obj, FuelKey)) / HoursPerWood))); woodLeft -= count; }
            else if (n.Kind == "Turret" && GetD(n.Obj, AmmoKey) <= ReloadBelowAmmo && oreLeft > 0)
            { kind = "reload"; qid = OreId; count = (int)Math.Min(10, Math.Min(oreLeft, Math.Floor((MaxAmmo - GetD(n.Obj, AmmoKey)) / AmmoPerOre))); oreLeft -= count; }
            if (kind == null || count <= 0) continue;

            var chest = NearestChestWith(qid, n.Tile);
            if (chest == null) continue;
            var hand = idle.OrderBy(a => Vector2.DistanceSquared(a.Pos, n.Tile * 64f)).First();
            idle.Remove(hand);
            hand.Jobs.Enqueue(new HandJob { Kind = kind, Owner = hand.Name, Tile = n.Tile, Via = chest.TileLocation, Item = qid, Count = count });
            Monitor.Log($"[maint] {hand.Name}: {kind} {n.Kind} at {n.Tile.X},{n.Tile.Y} ({count} x {qid}) from chest at {chest.TileLocation.X},{chest.TileLocation.Y}", LogLevel.Debug);
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
        j.Carry = TakeFromChest(chest, j.Item, j.Count);
        return j.Carry > 0;
    }

    /// <summary>At the machine: put the supplies in.</summary>
    private void DoMaintenance(HandJob j)
    {
        if (!j.Fetched && !FetchSupplies(j)) return;
        var farm = Game1.getFarm();
        if (!farm.objects.TryGetValue(j.Tile, out var o) || !IsPowerObject(o)) return;
        if (j.Kind == "refuel")
        {
            SetD(o, FuelKey, Math.Min(MaxFuelHours, GetD(o, FuelKey) + j.Carry * HoursPerWood));
            if (Game1.currentLocation is Farm) Game1.playSound("woodyStep");
            Defenses.Add($"{j.Owner} refueled");
        }
        else
        {
            SetD(o, AmmoKey, Math.Min(MaxAmmo, GetD(o, AmmoKey) + j.Carry * AmmoPerOre));
            if (actors.TryGetValue(j.Owner, out var a)) a.XpShooting++;
            if (Game1.currentLocation is Farm) Game1.playSound("clank");
            Defenses.Add($"{j.Owner} reloaded");
        }
        j.Carry = 0;
    }
}
