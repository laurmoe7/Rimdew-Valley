using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.BigCraftables;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>RimWorld-style electricity: wood generators, solar panels and batteries feed consumers (mini-turrets) over a grid.
/// Two power objects link when they are within <see cref="LinkRange"/> tiles of each other, or <see cref="PoleRange"/> tiles
/// when one of them is a pole. Each linked group is its own grid. Everything is stored in the placed objects' modData, so it
/// saves with the world.</summary>
public partial class ModEntry
{
    private const string PowerPrefix = "laurm.RimPower_";
    private const string PowerSheetAsset = "Mods/laurm.StardewBridge/PowerSprites";
    private const float LinkRange = 3.5f, PoleRange = 9f;
    private const double GeneratorWatts = 1000, SolarPeakWatts = 500, TurretWatts = 100, BatteryCapWh = 2000;
    private const double HoursPerWood = 4, MaxFuelHours = 24;
    private const double AmmoPerOre = 10, MaxAmmo = 100;
    private const string AmmoKey = "laurm.RimPower/ammo", TestKey = "laurm.RimPower/test";
    private const string FuelKey = "laurm.RimPower/fuel", ChargeKey = "laurm.RimPower/charge";

    private record PowerItem(string Id, string Name, string Description, string Recipe, string RecipeIngredients);

    private static readonly PowerItem[] PowerItems =
    {
        new("PowerPole",     "Power Pole",     "Links power objects up to 9 tiles apart.",                             "Power Pole",     "388 5 334 1"),
        new("WoodGenerator", "Wood Generator", "Burns wood for 1000 W. Right click with wood to refuel (4 h each).",  "Wood Generator", "335 5 390 20 334 2"),
        new("SolarPanel",    "Solar Panel",    "Up to 500 W in daylight; much less in rain.",                          "Solar Panel",    "335 3 334 5 338 3"),
        new("PowerBattery",  "Battery",        "Stores 2000 Wh. Smooths out day and night.",                           "Battery",        "335 2 334 5 388 10"),
        new("MiniTurret",    "Mini-Turret",    "Shoots monsters on its own. Needs 100 W and copper ore as ammo (10 shots each, holds 100).",                             "Mini-Turret",    "335 5 334 3 390 10"),
        new("ElectricStove", "Electric Stove", "Hands cook raw crops into Simple Meals here. Needs 150 W.",              "Electric Stove", "335 2 334 3 390 10"),
        new("Heater",        "Heater",         "Warms hands within 7 tiles (or a nearby bunkhouse) toward 21 C. Needs 250 W.",  "Heater",         "335 3 382 5 334 2"),
        new("Cooler",        "Cooler",         "Cools hands within 7 tiles (or a nearby bunkhouse) toward 24 C. Needs 250 W.",  "Cooler",         "335 3 334 3 338 2"),
    };

    private static readonly HashSet<string> PowerRecipeNames = PowerItems.Select(p => p.Recipe).Concat(DefItems.Select(d => d.Recipe)).ToHashSet();

    private static string PId(string shortId) => PowerPrefix + shortId;
    private static bool IsPowerObject(SObject o) => o.bigCraftable.Value && o.ItemId.StartsWith(PowerPrefix);
    private static bool IsRimObject(SObject o) => o.ItemId.StartsWith(PowerPrefix);

    private class PNode
    {
        public SObject Obj = null!;
        public Vector2 Tile;
        public string Kind = "";
        public int Grid;
        public double Supply, Demand;
        public bool Online;
        public float Angle = MathF.PI / 2;
        public int NextShot, LastShot = -100;
        public Monster? Target;
    }

    private class GridInfo
    {
        public int Nodes;
        public double Supply, Demand, Charge, Capacity, Satisfaction = 1;
    }

    private readonly Dictionary<Vector2, PNode> pNodes = new();
    private readonly List<GridInfo> grids = new();
    private readonly List<(PNode A, PNode B)> pLinks = new();
    private Texture2D? powerSheet;

    private static Gun? turretGun;
    private static Gun TurretGun => turretGun ??= Guns[3] with { MinDmg = 9, MaxDmg = 14, Cooldown = 13, Spread = 0.04f, Speed = 20f };
    private const float TurretRangeTiles = 9f;

    // ---------- content ----------
    private void PowerAssets(AssetRequestedEventArgs e)
    {
        DefenseAssets(e);
        if (e.NameWithoutLocale.IsEquivalentTo("Data/BigCraftables"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, BigCraftableData>().Data;
                for (int i = 0; i < PowerItems.Length; i++)
                {
                    var p = PowerItems[i];
                    data[PId(p.Id)] = new BigCraftableData
                    {
                        Name = PId(p.Id), DisplayName = p.Name, Description = p.Description, Price = 0, Fragility = 0,
                        CanBePlacedOutdoors = true, CanBePlacedIndoors = false, Texture = PowerSheetAsset, SpriteIndex = i
                    };
                }
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, string>().Data;
                foreach (var p in PowerItems) data[p.Recipe] = $"{p.RecipeIngredients}/Home/{PId(p.Id)}/true/default/{p.Name}";
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(PowerSheetAsset))
            e.LoadFrom(() => BuildPowerSheet(), AssetLoadPriority.Exclusive);
    }

    /// <summary>Learn the power recipes (unless recipe gating hides them).</summary>
    private void LearnPowerRecipes(bool gated)
    {
        foreach (var p in PowerItems)
            if (!gated) Game1.player.craftingRecipes.TryAdd(p.Recipe, 0);
    }

    internal void GivePowerKit()
    {
        foreach (var p in PowerItems)
            Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(BC)" + PId(p.Id), p.Id == "PowerPole" ? 6 : 1));
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)388", 50));
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)378", 50));
        Hud("Power kit: pole x6, generator, solar, battery, mini-turret, 50 wood, 50 copper ore.", HUDMessage.achievement_type);
    }

    // ---------- procedural sprites (16x32 cells in a row) ----------
    private Texture2D BuildPowerSheet()
    {
        if (powerSheet != null) return powerSheet;
        const int W = 16, H = 32;
        var px = new Color[W * H * PowerItems.Length];
        void Set(int cell, int x, int y, Color c) { if (x >= 0 && x < W && y >= 0 && y < H) px[y * W * PowerItems.Length + cell * W + x] = c; }
        void Rect(int cell, int x, int y, int w, int h, Color c) { for (int j = y; j < y + h; j++) for (int i = x; i < x + w; i++) Set(cell, i, j, c); }
        var dark = new Color(28, 24, 22);

        // 0 pole
        var wood = new Color(120, 82, 46); var woodD = new Color(86, 56, 30);
        Rect(0, 7, 4, 2, 28, wood); Rect(0, 8, 4, 1, 28, woodD);
        Rect(0, 3, 7, 10, 2, wood); Rect(0, 3, 8, 10, 1, woodD);
        Set(0, 3, 5, new Color(150, 200, 230)); Set(0, 12, 5, new Color(150, 200, 230)); Set(0, 7, 3, new Color(150, 200, 230));
        Rect(0, 5, 29, 6, 3, new Color(120, 120, 124));

        // 1 generator
        var steel = new Color(96, 102, 114); var steelL = new Color(132, 140, 154); var steelD = new Color(66, 70, 80);
        Rect(1, 1, 15, 14, 15, steel); Rect(1, 1, 15, 14, 2, steelL); Rect(1, 1, 27, 14, 3, steelD);
        Rect(1, 11, 5, 3, 10, steelD); Rect(1, 11, 5, 3, 1, steelL);
        for (int k = 0; k < 4; k++) Rect(1, 3, 19 + k * 2, 6, 1, new Color(224, 128, 40));
        Rect(1, 10, 19, 3, 6, new Color(230, 200, 60)); Set(1, 11, 20, steelD); Set(1, 12, 22, steelD); Set(1, 11, 24, steelD);
        Rect(1, 2, 29, 3, 2, dark); Rect(1, 11, 29, 3, 2, dark);

        // 2 solar panel
        Rect(2, 7, 22, 2, 9, new Color(120, 120, 128));
        Rect(2, 1, 7, 14, 14, new Color(190, 192, 200));
        Rect(2, 2, 8, 12, 12, new Color(30, 52, 118));
        for (int k = 0; k < 4; k++) { Rect(2, 2, 8 + k * 3, 12, 1, new Color(70, 108, 178)); Rect(2, 2 + k * 3 + 2, 8, 1, 12, new Color(70, 108, 178)); }
        Rect(2, 3, 9, 3, 2, new Color(130, 170, 230));
        Rect(2, 4, 29, 8, 3, new Color(110, 110, 118));

        // 3 battery
        Rect(3, 3, 12, 10, 18, new Color(52, 58, 68)); Rect(3, 3, 12, 10, 2, new Color(86, 94, 108));
        Rect(3, 5, 9, 2, 3, new Color(214, 178, 60)); Rect(3, 9, 9, 2, 3, new Color(214, 178, 60));
        for (int k = 0; k < 4; k++) Rect(3, 5, 15 + k * 4, 6, 3, new Color(84, 204, 96));
        Rect(3, 3, 28, 10, 2, new Color(34, 38, 46));

        // electric stove (cell 5)
        var enamel = new Color(214, 218, 224); var enamelD = new Color(150, 156, 168);
        Rect(5, 1, 14, 14, 16, enamel); Rect(5, 1, 14, 14, 2, new Color(240, 242, 246)); Rect(5, 1, 28, 14, 2, enamelD);
        Rect(5, 3, 17, 4, 3, new Color(50, 50, 56)); Rect(5, 9, 17, 4, 3, new Color(50, 50, 56));
        Rect(5, 4, 18, 2, 1, new Color(230, 90, 40)); Rect(5, 10, 18, 2, 1, new Color(230, 90, 40));
        Rect(5, 3, 22, 10, 6, enamelD); Rect(5, 4, 23, 8, 4, new Color(70, 76, 90));
        for (int k = 0; k < 3; k++) Set(5, 4 + k * 3, 21, new Color(60, 60, 66));
        // heater (cell 6)
        var hd = new Color(76, 60, 56);
        Rect(6, 2, 14, 12, 16, hd); Rect(6, 2, 14, 12, 2, new Color(110, 88, 80));
        for (int k = 0; k < 5; k++) Rect(6, 4, 18 + k * 2, 8, 1, k % 2 == 0 ? new Color(255, 150, 50) : new Color(220, 70, 30));
        Rect(6, 3, 28, 3, 2, dark); Rect(6, 10, 28, 3, 2, dark);
        // cooler (cell 7)
        var cw = new Color(196, 214, 232); var cwD = new Color(120, 150, 184);
        Rect(7, 2, 13, 12, 17, cw); Rect(7, 2, 13, 12, 2, new Color(232, 242, 252)); Rect(7, 2, 28, 12, 2, cwD);
        Rect(7, 4, 17, 8, 8, cwD); Rect(7, 5, 18, 6, 6, new Color(60, 100, 160));
        Rect(7, 7, 20, 2, 2, new Color(210, 230, 250)); Rect(7, 5, 21, 6, 1, new Color(100, 140, 200)); Rect(7, 7, 18, 1, 6, new Color(100, 140, 200));
        // 4 turret (barrel is drawn on top at runtime)
        var olive = new Color(104, 118, 100); var oliveL = new Color(144, 160, 136); var oliveD = new Color(70, 80, 68);
        Rect(4, 3, 27, 10, 4, oliveD); Rect(4, 5, 25, 6, 2, olive);
        Rect(4, 6, 17, 4, 8, steelD);
        Rect(4, 3, 8, 10, 9, olive); Rect(4, 4, 7, 8, 1, olive); Rect(4, 4, 17, 8, 1, olive);
        Rect(4, 4, 8, 6, 2, oliveL); Rect(4, 3, 14, 10, 3, oliveD);

        // 1px dark outline around each sprite
        for (int cell = 0; cell < PowerItems.Length; cell++)
        {
            var copy = (Color[])px.Clone();
            bool Solid(int x, int y) => x >= 0 && x < W && y >= 0 && y < H && copy[y * W * PowerItems.Length + cell * W + x].A > 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (!Solid(x, y) && (Solid(x - 1, y) || Solid(x + 1, y) || Solid(x, y - 1) || Solid(x, y + 1))) Set(cell, x, y, dark);
        }

        var tex = new Texture2D(Game1.graphics.GraphicsDevice, W * PowerItems.Length, H);
        tex.SetData(px);
        powerSheet = tex;
        return tex;
    }

    // ---------- the grid ----------
    private static double Daylight()
    {
        double h = Game1.timeOfDay / 100 + (Game1.timeOfDay % 100) / 60.0;
        if (h < 6 || h > 20) return 0;
        double d = Math.Sin((h - 6) / 14 * Math.PI);
        if (Game1.isRaining || Game1.isLightning) d *= 0.25;
        return Math.Max(0, d);
    }

    private static string KindOf(SObject o) => o.ItemId[PowerPrefix.Length..] switch
    {
        "PowerPole" => "Pole", "WoodGenerator" => "Generator", "SolarPanel" => "Solar", "PowerBattery" => "Battery", "MiniTurret" => "Turret",
        "ElectricStove" => "Stove", "Heater" => "Heater", "Cooler" => "Cooler", _ => ""
    };

    private static double GetD(SObject o, string key, double def = 0) => o.modData.TryGetValue(key, out var s) && double.TryParse(s, out var v) ? v : def;
    private static void SetD(SObject o, string key, double v) => o.modData[key] = v.ToString("0.###");

    /// <summary>Rescan the farm for power objects and re-link them.</summary>
    private void RescanPower()
    {
        var farm = Game1.getFarm();
        var seen = new HashSet<Vector2>();
        foreach (var (tile, o) in farm.objects.Pairs)
        {
            if (!IsPowerObject(o)) continue;
            seen.Add(tile);
            if (!pNodes.TryGetValue(tile, out var n) || n.Obj != o) pNodes[tile] = n = new PNode { Obj = o, Tile = tile, Kind = KindOf(o) };
        }
        foreach (var k in pNodes.Keys.Where(k => !seen.Contains(k)).ToList()) pNodes.Remove(k);

        // union-find over linked nodes
        var list = pNodes.Values.ToList();
        var parent = list.Select((_, i) => i).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        pLinks.Clear();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                float range = list[i].Kind == "Pole" || list[j].Kind == "Pole" ? PoleRange : LinkRange;
                if (Vector2.Distance(list[i].Tile, list[j].Tile) > range) continue;
                parent[Find(i)] = Find(j);
                pLinks.Add((list[i], list[j]));
            }
        grids.Clear();
        var ids = new Dictionary<int, int>();
        for (int i = 0; i < list.Count; i++)
        {
            int root = Find(i);
            if (!ids.TryGetValue(root, out var gid)) { ids[root] = gid = grids.Count; grids.Add(new GridInfo()); }
            list[i].Grid = gid;
            grids[gid].Nodes++;
        }
    }

    /// <summary>Advance the grid simulation by <paramref name="hours"/> game hours.</summary>
    private void SimulatePower(double hours)
    {
        double sun = Daylight();
        foreach (var g in grids) { g.Supply = g.Demand = g.Charge = g.Capacity = 0; }
        foreach (var n in pNodes.Values) { n.Supply = n.Demand = 0; }

        // fixed supply and demand
        foreach (var n in pNodes.Values)
        {
            var g = grids[n.Grid];
            switch (n.Kind)
            {
                case "Solar": n.Supply = SolarPeakWatts * sun; g.Supply += n.Supply; break;
                case "Turret": n.Demand = TurretWatts; g.Demand += n.Demand; break;
                case "Stove": n.Demand = 150; g.Demand += n.Demand; break;
                case "Heater": case "Cooler": n.Demand = 250; g.Demand += n.Demand; break;
                case "Battery": g.Charge += GetD(n.Obj, ChargeKey); g.Capacity += BatteryCapWh; break;
            }
        }

        // generators run when the grid needs them (unmet demand, or batteries with room to fill)
        for (int gi = 0; gi < grids.Count; gi++)
        {
            var g = grids[gi];
            bool needs = g.Demand > g.Supply || g.Charge < g.Capacity - 1;
            foreach (var n in pNodes.Values.Where(n => n.Grid == gi && n.Kind == "Generator"))
            {
                double fuel = GetD(n.Obj, FuelKey);
                if (fuel <= 0) { n.Online = false; continue; }
                n.Online = needs;
                if (!needs) continue;
                n.Supply = GeneratorWatts;
                g.Supply += n.Supply;
                SetD(n.Obj, FuelKey, Math.Max(0, fuel - hours));
            }
        }

        // batteries cover shortages or soak up surplus; satisfaction is what consumers actually get
        foreach (var g in grids)
        {
            double surplus = g.Supply - g.Demand;   // watts
            g.Satisfaction = g.Demand <= 0 ? 1 : Math.Min(1, g.Supply / g.Demand);
            if (surplus < 0 && g.Charge > 0)
            {
                double drawWh = Math.Min(g.Charge, -surplus * hours);
                g.Charge -= drawWh;
                double covered = hours > 0 ? drawWh / hours : -surplus;
                g.Satisfaction = Math.Min(1, (g.Supply + covered) / g.Demand);
            }
            else if (surplus > 0 && g.Capacity > 0) g.Charge = Math.Min(g.Capacity, g.Charge + surplus * hours);
        }

        // write charge back, split evenly across the grid's batteries
        for (int gi = 0; gi < grids.Count; gi++)
        {
            var bats = pNodes.Values.Where(n => n.Grid == gi && n.Kind == "Battery").ToList();
            foreach (var b in bats) SetD(b.Obj, ChargeKey, grids[gi].Charge / bats.Count);
        }
        foreach (var n in pNodes.Values)
            if (n.Kind is "Turret" or "Battery" or "Solar" or "Pole" or "Stove" or "Heater" or "Cooler") n.Online = grids[n.Grid].Satisfaction >= 0.5 && (n.Kind != "Solar" || n.Supply > 1);
    }

    private int lastPowerScan = -999;

    private void TickPower(UpdateTickedEventArgs e)
    {
        if (!Game1.hasLoadedGame || Game1.currentLocation == null) return;
        if (e.IsMultipleOf(30))
        {
            RescanPower();
            // 30 ticks = 0.5 s real = ~0.71 game minutes (700 ms per minute)
            SimulatePower(Game1.shouldTimePass() ? 30 * (1000.0 / 60) / 700.0 / 60.0 : 0);
            lastPowerScan = (int)e.Ticks;
        }
        TickTurrets((int)e.Ticks);
        TickDefense((int)e.Ticks);
    }

    // ---------- turrets ----------
    private void TickTurrets(int ticks)
    {
        var farm = Game1.getFarm();
        if (Game1.currentLocation != farm) return;   // projectiles only fly while the player is in the same location
        var monsters = farm.characters.OfType<Monster>().Where(m => m.Health > 0 && !m.IsInvisible).ToList();
        float range = TurretRangeTiles * 64f;
        foreach (var n in pNodes.Values)
        {
            if (n.Kind != "Turret") continue;
            var head = TurretHead(n);
            n.Target = null;
            if (!n.Online || monsters.Count == 0 || GetD(n.Obj, AmmoKey) < 1) continue;

            Monster? best = null; float bestD = range;
            foreach (var m in monsters)
            {
                float d = Vector2.Distance(head, m.GetBoundingBox().Center.ToVector2());
                if (d < bestD) { bestD = d; best = m; }
            }
            if (best == null) continue;
            n.Target = best;

            // lead the target: aim where it will be when the bullet arrives (Stardew's yVelocity points up, hence the minus)
            var center = best.GetBoundingBox().Center.ToVector2();
            float flight = Vector2.Distance(head, center) / TurretGun.Speed;
            var to = center + new Vector2(best.xVelocity, -best.yVelocity) * flight - head;
            float want = MathF.Atan2(to.Y, to.X);
            float diff = MathHelper.WrapAngle(want - n.Angle);
            n.Angle += Math.Clamp(diff, -0.35f, 0.35f);
            if (Math.Abs(diff) > 0.25f || ticks < n.NextShot) continue;

            n.NextShot = ticks + TurretGun.Cooldown;
            n.LastShot = ticks;
            float a = n.Angle + (float)((Game1.random.NextDouble() - 0.5) * 2 * TurretGun.Spread);
            // start the bullet clear of the turret's own tile, or it collides with the turret object it came from
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var own = new Rectangle((int)n.Tile.X * 64 - 36, (int)n.Tile.Y * 64 - 36, 136, 136);   // bullet box is ~64px wide, so clear by half of that
            var muzzle = head + dir * 36f;
            for (int k = 0; k < 16 && own.Contains(muzzle.ToPoint()); k++) muzzle += dir * 8f;
            SpawnBullet(farm, TurretGun, muzzle, a, Game1.random.Next(TurretGun.MinDmg, TurretGun.MaxDmg + 1), false);
            PlayGunShot(TurretGun, head);
            SetD(n.Obj, AmmoKey, GetD(n.Obj, AmmoKey) - 1);
            turretShots++;
        }
    }

    private int turretShots;

    private static Vector2 TurretHead(PNode n) => new(n.Tile.X * 64 + 32, n.Tile.Y * 64 - 14);

    // ---------- interaction ----------
    private void PowerButton(ButtonPressedEventArgs e)
    {
        if (!Context.IsPlayerFree || !e.Button.IsActionButton()) return;
        var tile = e.Cursor.GrabTile;
        bool pickup = Helper.Input.IsDown(SButton.LeftShift) || Helper.Input.IsDown(SButton.RightShift);
        if (!Game1.currentLocation.objects.TryGetValue(tile, out var o) || !IsRimObject(o) || (IsTrap(o) && !pickup)) return;
        if (Vector2.Distance(Game1.player.Tile, tile) > (pickup ? 10 : 4)) return;
        Helper.Input.Suppress(e.Button);

        if (pickup)
        {
            Game1.currentLocation.objects.Remove(tile);
            var item = o.getOne();
            if (!Game1.player.addItemToInventoryBool(item)) Game1.createItemDebris(item, Game1.player.getStandingPosition(), 2);
            Game1.playSound("pickUpItem");
            return;
        }

        string kind = o.bigCraftable.Value ? KindOf(o) : "";
        var held = Game1.player.CurrentItem?.QualifiedItemId;
        if (kind == "Generator" && held == "(O)388")
        {
            double fuel = GetD(o, FuelKey);
            if (fuel + HoursPerWood > MaxFuelHours) { Hud("The generator is full.", HUDMessage.error_type); return; }
            SetD(o, FuelKey, fuel + HoursPerWood);
            Game1.player.reduceActiveItemByOne();
            Game1.playSound("woodyStep");
            Hud($"Generator fuel: {fuel + HoursPerWood:0.#} h", HUDMessage.newQuest_type);
            return;
        }
        if (kind == "Turret" && held == "(O)378")
        {
            double ammo = GetD(o, AmmoKey);
            int fit = (int)Math.Floor((MaxAmmo - ammo) / AmmoPerOre);
            int use = Math.Min(fit, Game1.player.CurrentItem!.Stack);
            if (use <= 0) { Hud("The turret is full.", HUDMessage.error_type); return; }
            SetD(o, AmmoKey, ammo + use * AmmoPerOre);
            Game1.player.Items.ReduceId("(O)378", use);
            Game1.playSound("clank");
            Hud($"Turret ammo: {ammo + use * AmmoPerOre:0}/{MaxAmmo:0}", HUDMessage.newQuest_type);
            return;
        }
        Hud(kind == "" ? DescribeDefense(o) : Describe(o, kind), HUDMessage.newQuest_type);
    }

    private string Describe(SObject o, string kind)
    {
        pNodes.TryGetValue(o.TileLocation, out var n);
        var g = n != null && n.Grid < grids.Count ? grids[n.Grid] : null;
        string grid = g == null ? "" : $" Grid: {g.Supply:0}/{g.Demand:0} W, battery {g.Charge:0}/{g.Capacity:0} Wh.";
        return kind switch
        {
            "Generator" => $"Generator: fuel {GetD(o, FuelKey):0.#} h ({(n?.Online == true ? "running" : "idle")}).{grid}",
            "Battery" => $"Battery: {GetD(o, ChargeKey):0} / {BatteryCapWh:0} Wh.{grid}",
            "Solar" => $"Solar panel: {(n?.Supply ?? 0):0} W now.{grid}",
            "Turret" => $"Mini-turret: {(n?.Online == true ? "online" : "NO POWER")}, ammo {GetD(o, AmmoKey):0}/{MaxAmmo:0}.{grid}",
            "Stove" => $"Electric stove: {(n?.Online == true ? "powered" : "NO POWER")}.{grid}",
            "Heater" => $"Heater: {(n?.Online == true ? "warming" : "NO POWER")}.{grid}",
            "Cooler" => $"Cooler: {(n?.Online == true ? "cooling" : "NO POWER")}.{grid}",
            _ => $"Power pole.{grid}"
        };
    }

    // ---------- drawing ----------
    private void DrawPower(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation != Game1.getFarm() || pNodes.Count == 0) return;
        var b = e.SpriteBatch;
        bool carrying = Game1.player.CurrentItem is { } ci && ci.ItemId?.StartsWith(PowerPrefix) == true;
        var vp = new Rectangle(Game1.viewport.X - 200, Game1.viewport.Y - 200, Game1.viewport.Width + 400, Game1.viewport.Height + 400);

        // wires
        foreach (var (a, c) in pLinks)
        {
            var p1 = new Vector2(a.Tile.X * 64 + 32, a.Tile.Y * 64 - 24); var p2 = new Vector2(c.Tile.X * 64 + 32, c.Tile.Y * 64 - 24);
            if (!vp.Contains(p1.ToPoint()) && !vp.Contains(p2.ToPoint())) continue;
            var s1 = Game1.GlobalToLocal(Game1.viewport, p1); var s2 = Game1.GlobalToLocal(Game1.viewport, p2);
            bool live = grids[a.Grid].Satisfaction >= 0.5;
            var col = (live ? new Color(255, 214, 90) : new Color(200, 80, 70)) * (carrying ? 0.9f : 0.45f);
            Utility.drawLineWithScreenCoordinates((int)s1.X, (int)s1.Y, (int)s2.X, (int)s2.Y, b, col, 1f, 2);
        }

        foreach (var n in pNodes.Values)
        {
            var world = new Vector2(n.Tile.X * 64, n.Tile.Y * 64);
            if (!vp.Contains(world.ToPoint())) continue;
            var screen = Game1.GlobalToLocal(Game1.viewport, world);

            if (n.Kind == "Turret")
            {
                var head = Game1.GlobalToLocal(Game1.viewport, TurretHead(n));
                var tex = LoadGunSprite(TurretGun);
                if (tex != null)
                {
                    // reuse the hand-held gun drawing so recoil and left/right flipping match the farmers' guns
                    DrawGunSprite(b, TurretGun, TurretHead(n), n.Angle, 2, n.Tile.Y * 64 + 64, n.LastShot);
                }
                else
                    b.Draw(Game1.fadeToBlackRect, head, null, Color.DimGray, n.Angle, new Vector2(0, 0.5f), new Vector2(30, 6), SpriteEffects.None, 1f);
                if (Game1.ticks - n.LastShot < 3)   // muzzle flash
                {
                    var tip = head + new Vector2(MathF.Cos(n.Angle), MathF.Sin(n.Angle)) * 40f;
                    b.Draw(Game1.fadeToBlackRect, new Rectangle((int)tip.X - 5, (int)tip.Y - 5, 10, 10), new Color(255, 230, 140));
                }
            }
            if (n.Kind == "Generator" && n.Online && n.Supply > 0)   // glowing furnace mouth
                b.Draw(Game1.fadeToBlackRect, new Rectangle((int)screen.X + 14, (int)screen.Y - 30, 18, 14),
                    new Color(255, 170 + (int)(Math.Sin(Game1.ticks / 5.0) * 40), 60) * 0.85f);

            // status lamp: green powered, red starved
            bool ok = n.Kind switch { "Generator" => GetD(n.Obj, FuelKey) > 0, "Solar" => n.Supply > 1 || Daylight() > 0, "Turret" => n.Online && GetD(n.Obj, AmmoKey) >= 1, _ => n.Online };
            if (n.Kind == "Pole") ok = grids[n.Grid].Satisfaction >= 0.5;
            // lamp sits just above the sprite (sprites are 2 tiles tall, bottom-aligned to the tile)
            var lamp = Game1.GlobalToLocal(Game1.viewport, new Vector2(n.Tile.X * 64 + 27, n.Tile.Y * 64 - (n.Kind == "Pole" ? 64 : n.Kind == "Turret" ? 52 : 78)));
            b.Draw(Game1.fadeToBlackRect, new Rectangle((int)lamp.X, (int)lamp.Y, 10, 10), ok ? new Color(90, 230, 110) : new Color(230, 70, 60));
        }
    }

    private void DrawPowerHud(object? sender, RenderedHudEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation != Game1.getFarm() || grids.Count == 0 || Game1.activeClickableMenu != null) return;
        bool carrying = Game1.player.CurrentItem is { } ci && ci.ItemId?.StartsWith(PowerPrefix) == true;
        PNode? near = pNodes.Values.OrderBy(n => Vector2.Distance(n.Tile, Game1.player.Tile)).FirstOrDefault();
        if (near == null || (!carrying && Vector2.Distance(near.Tile, Game1.player.Tile) > 8)) return;
        var g = grids[near.Grid];
        string line1 = $"Power grid: {g.Supply:0} W in / {g.Demand:0} W used";
        string line2 = g.Capacity > 0 ? $"Battery {g.Charge:0}/{g.Capacity:0} Wh" : "No battery";
        string line3 = g.Demand > 0 && g.Satisfaction < 0.99 ? $"SHORTAGE: {g.Satisfaction * 100:0}% satisfied" : $"{g.Nodes} devices";
        var b = e.SpriteBatch;
        var font = Game1.smallFont;
        int w = (int)Math.Max(font.MeasureString(line1).X, Math.Max(font.MeasureString(line2).X, font.MeasureString(line3).X)) + 24, h = 96;
        int x = 16, y = 90;
        b.Draw(Game1.fadeToBlackRect, new Rectangle(x, y, w, h), Color.Black * 0.6f);
        b.DrawString(font, line1, new Vector2(x + 12, y + 8), new Color(255, 214, 90));
        b.DrawString(font, line2, new Vector2(x + 12, y + 34), Color.White);
        b.DrawString(font, line3, new Vector2(x + 12, y + 60), g.Satisfaction < 0.99 && g.Demand > 0 ? new Color(255, 110, 100) : Color.LightGray);
    }

    private void InitPower()
    {
        Helper.Events.Display.RenderedWorld += DrawPower;
        Helper.Events.Display.RenderedHud += DrawPowerHud;
        Helper.Events.Input.ButtonPressed += (_, e) => PowerButton(e);
    }

    /// <summary>Remove leftovers from the dev power test (tagged objects, or the exact untagged test layout from an older build) and test hands.</summary>
    private void CleanTestObjects()
    {
        var farm = Game1.getFarm();
        var doomed = farm.objects.Pairs.Where(kv => kv.Value.modData.ContainsKey(TestKey)).Select(kv => kv.Key).ToList();
        var d = farm.GetMainFarmHouseEntry();
        var layout = new[] { (0, 0, "WoodGenerator"), (1, 0, "PowerBattery"), (2, 0, "SolarPanel"), (3, 0, "PowerPole"), (9, 0, "MiniTurret"), (9, 12, "MiniTurret") }
            .Select(l => (tile: new Vector2(d.X + 6 + l.Item1, d.Y - 2 + l.Item2), id: PId(l.Item3))).ToList();
        if (layout.All(l => farm.objects.TryGetValue(l.tile, out var o) && o.ItemId == l.id)) doomed.AddRange(layout.Select(l => l.tile));
        foreach (var t in doomed.Distinct()) farm.objects.Remove(t);
        int hands = data.Hands.RemoveAll(n => n.StartsWith("Test"));
        if (doomed.Count > 0 || hands > 0) { Monitor.Log($"Removed {doomed.Distinct().Count()} leftover test objects and {hands} test hands from the save.", LogLevel.Info); SyncHands(true); }
    }

    internal void ReclaimPower()
    {
        var loc = Game1.currentLocation;
        int n = 0;
        foreach (var (tile, o) in loc.objects.Pairs.Where(kv => IsRimObject(kv.Value)).ToList())
        {
            loc.objects.Remove(tile);
            var item = o.getOne();
            if (!Game1.player.addItemToInventoryBool(item)) Game1.createItemDebris(item, Game1.player.getStandingPosition(), 2);
            n++;
        }
        Hud($"Picked up {n} power/defense objects.", HUDMessage.achievement_type);
    }

    private void RegisterPowerCommands()
    {
        Helper.ConsoleCommands.Add("rd_defense", "Give sandbags, spike traps and explosive traps.", (_, _) => { if (Ready()) GiveDefenseKit(); });
        Helper.ConsoleCommands.Add("rd_power_reclaim", "Pick up every power/defense object in the current location (also: Shift+right-click one).", (_, _) => { if (Ready()) ReclaimPower(); });
        Helper.ConsoleCommands.Add("rd_power", "Give the power kit (poles, generator, solar, battery, turret).", (_, _) => { if (Ready()) GivePowerKit(); });
    }
}
