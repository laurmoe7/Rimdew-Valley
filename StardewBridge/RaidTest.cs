using Newtonsoft.Json.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>Dev scenario (dev_raidtest.flag): checks each raid flavour spawns what it should and that sappers beat sandbags and traps.</summary>
public partial class ModEntry
{
    private int rtStart = -1;
    private Monster? aimBrute;

    private void Rt(string m) => Monitor.Log("[raidtest] " + m, LogLevel.Alert);

    private string Describe(IEnumerable<Monster> ms, Vector2 player)
    {
        var l = ms.ToList();
        if (l.Count == 0) return "none";
        var tiles = l.Select(m => new Vector2(m.Tile.X, m.Tile.Y)).ToList();
        float minD = tiles.Min(t => Vector2.Distance(t, player)), maxD = tiles.Max(t => Vector2.Distance(t, player));
        var c = new Vector2(tiles.Average(t => t.X), tiles.Average(t => t.Y));
        float spread = tiles.Max(t => Vector2.Distance(t, c));
        return $"{l.Count}x {string.Join("/", l.Select(m => m.GetType().Name).Distinct())} hp={l.Average(m => m.MaxHealth):0} speed={l.Average(m => m.speed):0.0} sappers={l.Count(m => m.modData.ContainsKey(SapperKey))} distFromPlayer={minD:0}-{maxD:0} spread={spread:0.0}";
    }

    private string WhatIsAt(GameLocation farm, string m)
    {
        var parts = m.Substring(m.IndexOf("at ") + 3).Split(',');
        float fx = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), fy = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var found = new List<string>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var t = new Vector2((int)fx + dx, (int)fy + dy);
                var pt = new Point((int)t.X, (int)t.Y);
                string s = $"({t.X},{t.Y}) walk={Walkable(pt)}";
                if (farm.objects.TryGetValue(t, out var o)) s += " obj=" + o.Name;
                if (farm.terrainFeatures.TryGetValue(t, out var tf)) s += " tf=" + tf.GetType().Name;
                var rc = farm.resourceClumps.FirstOrDefault(r => r.occupiesTile((int)t.X, (int)t.Y)); if (rc != null) s += " clump=" + rc.parentSheetIndex.Value;
                var lt = farm.getLargeTerrainFeatureAt((int)t.X, (int)t.Y); if (lt != null) s += " large=" + lt.GetType().Name;
                if (farm.buildings.Any(b => b.occupiesTile(t))) s += " building";
                if (dx == 0 && dy == 0 || !s.EndsWith("walk=True")) found.Add(s);
            }
        return string.Join(" | ", found);
    }

    private bool colDone;
    private int colStart = -1;

    /// <summary>RIMDEW_COLTEST=1: show the Colony tab with a few captives and save a picture of the menu.</summary>
    private void TickColonyTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_COLTEST") != "1" || colDone || !Context.IsWorldReady) return;
        if (colStart < 0) colStart = (int)e.Ticks;
        int t = (int)e.Ticks - colStart;
        if (t == 150)
        {
            data.Captives.Clear();
            foreach (var c in new[] { "Vex|Marlon", "Rook|Gus", "Mara|Pam" }) data.Captives.Add(c);
            Game1.activeClickableMenu = new RimDewMenu(this, 2);
        }
        if (t == 200) { MenuShot("colony_captives"); }
        if (t == 230)
        {
            Game1.exitActiveMenu();
            // recruit Vex (retrying: it is a 55% roll and one try a day), release Rook, execute Mara
            int tries = 0;
            while (data.Captives.Any(c => c.StartsWith("Vex")) && tries++ < 40)
            {
                int vi = data.Captives.FindIndex(c => c.StartsWith("Vex"));
                data.Captives[vi] = "Vex|Marlon";
                RecruitCaptive(vi);
            }
            Rt($"PRISON recruit took {tries} tries; hands now [{string.Join(", ", data.Hands)}]");
            ReleaseCaptive(data.Captives.FindIndex(c => c.StartsWith("Rook")));
            ExecuteCaptive(data.Captives.FindIndex(c => c.StartsWith("Mara")));
            Rt($"PRISON captives left [{string.Join(", ", data.Captives)}]");
        }
        if (t == 800) RefreshRimWorld();
        if (t == 1000)
        {
            foreach (var h in LastHands)
                Rt($"PRISON hand {(string?)h["name"]} mood={(double)h["mood"]!:0.00} traits=[{TraitSummary((string)h["name"]!)}] thoughts=[{string.Join("; ", ((JArray)h["thoughts"]!).Select(x => (string)x["label"]! + " " + (double)x["offset"]!))}]");
            data.Hands.Remove("Vex"); Send(new JObject { ["type"] = "fire", ["name"] = "Vex" }); SyncHands(false);
            Rt("COLONY done"); colDone = true;
        }
    }

    private bool cabinDone;
    private int cabStart = -1;

    /// <summary>RIMDEW_CABINTEST=1: build three free bunkhouses; each should switch to a cabin look.</summary>
    private void TickCabinTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_CABINTEST") != "1" || cabinDone || !Context.IsWorldReady) return;
        if (cabStart < 0) { cabStart = (int)e.Ticks; Rt("CABIN world ready"); }
        int t = (int)e.Ticks - cabStart - 90;
        if (t == -80) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t < 0) return;
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        try
        {
            if (t == 30)
            {
                int built = 0;
                for (int ty = 6; ty < 70 && built < 3; ty += 2)
                    for (int tx = 6; tx < 72 && built < 3; tx += 2)
                    {
                        if (farm.buildings.Any(o => Math.Abs(o.tileX.Value - tx) < 9 && Math.Abs(o.tileY.Value - ty) < 6)) continue;
                        if (farm.buildStructure(BunkFreeId, new Vector2(tx, ty), Game1.player, out var b, magicalConstruction: true))
                        { b.daysOfConstructionLeft.Value = 0; built++; Rt($"CABIN built at {tx},{ty}"); }
                    }
            }
            if (t == 200)
            {
                foreach (var b in farm.buildings.Where(IsBunk))
                    Rt($"CABIN type={b.buildingType.Value} skin={b.skinId.Value ?? "default"} size={b.tilesWide.Value}x{b.tilesHigh.Value} door={b.humanDoor.Value} at {b.tileX.Value},{b.tileY.Value} indoors={b.GetIndoors()?.GetType().Name}");
                var first = farm.buildings.FirstOrDefault(IsBunk);
                if (first != null) Game1.player.Position = new Vector2((first.tileX.Value + 2) * 64, (first.tileY.Value + first.tilesHigh.Value + 3) * 64);
            }
            if (t == 900) { Rt("CABIN done"); cabinDone = true; }
        }
        catch (Exception ex) { Rt("CABIN EXCEPTION: " + ex); cabinDone = true; }
    }

    private bool humanDone;
    private int htStart = -1;

    /// <summary>RIMDEW_HUMANTEST=1: human raiders arrive, hands shoot them down, they stop being targets, and a downed one can be captured.</summary>
    private void TickHumanTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_HUMANTEST") != "1" || humanDone || !Context.IsWorldReady) return;
        if (htStart < 0) { htStart = (int)e.Ticks; Rt("HUMAN world ready"); }
        int t = (int)e.Ticks - htStart - 90;
        if (t == -80) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t < 0) return;
        var farm = Game1.getFarm();
        try
        {
            if (t == 60)
            {
                SpawnMonsters(4, "human");
                Rt($"HUMAN spawned: {farm.characters.OfType<HumanRaider>().Count()} raiders: " +
                   string.Join(", ", farm.characters.OfType<HumanRaider>().Select(r => $"{r.RaiderName}/{r.Look}/hp{r.Health}/{(r.Ranged ? "gun" : "melee")}")));
            }
            if (t >= 60 && t % 180 == 0 && t < 3000)
            {
                var hs = farm.characters.OfType<HumanRaider>().ToList();
                Rt($"HUMAN t={t} alive={hs.Count(r => !r.Downed)} downed={hs.Count(r => r.Downed)} playerHp={Game1.player.health} calm={Calm()} hands=[{string.Join(", ", actors.Values.Select(a => a.Name + ":" + a.Activity))}]");
            }
            if (t == 3000)
            {
                var hs = farm.characters.OfType<HumanRaider>().ToList();
                var down = hs.FirstOrDefault(r => r.Downed);
                if (down != null) CaptureRaider(farm, down);
                Rt($"HUMAN RESULT downed={hs.Count(r => r.Downed)} (before capture) captives={data.Captives.Count} [{string.Join(", ", data.Captives)}] stillOnFarm={farm.characters.OfType<HumanRaider>().Count()} defenses=[{string.Join("; ", Defenses.Distinct().TakeLast(6))}]");
                farm.characters.RemoveWhere(c => c is Monster);
                humanDone = true;
            }
        }
        catch (Exception ex) { Rt("HUMAN EXCEPTION: " + ex); humanDone = true; }
    }

    private bool dineDone;
    private int dtStart = -1;

    /// <summary>RIMDEW_DINETEST=1: hungry hands should carry food to a chair beside a table in their bunkhouse; a microwave on a table is a power node.</summary>
    private void TickDineTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_DINETEST") != "1" || dineDone || !Context.IsWorldReady) return;
        if (dtStart < 0) { dtStart = (int)e.Ticks; Rt("DINE world ready"); }
        int t = (int)e.Ticks - dtStart - 90;
        if (t == -80) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t < 0) return;
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        if (t % 30 == 0) { farm.characters.RemoveWhere(c => c is Monster); pendingSpawns.Clear(); }   // keep raids out of this test
        try
        {
            if (t == 30)
            {
                var hand = actors.Values.FirstOrDefault(a => a.Bunk?.GetIndoors() != null);
                if (hand == null) { Rt("DINE FAIL: no hand with a bunkhouse"); dineDone = true; return; }
                var loc = hand.Bunk!.GetIndoors();
                int w = loc.map.Layers[0].LayerWidth, exitY = loc.warps.FirstOrDefault()?.Y ?? loc.map.Layers[0].LayerHeight - 1;
                StardewValley.Objects.Furniture? Put(string kind, Func<int, int, bool> where)
                {
                    string? id = FurnitureIdFor(kind); if (id == null) return null;
                    for (int y = 2; y <= exitY - 3; y++)
                        for (int x = 1; x < w - 1; x++)
                        {
                            if (!where(x, y)) continue;
                            var f = ItemRegistry.Create("(F)" + id, 1, allowNull: true) as StardewValley.Objects.Furniture;
                            if (f == null || !f.canBePlacedHere(loc, new Vector2(x, y))) continue;
                            if (f.placementAction(loc, x * 64, y * 64, Game1.player)) return loc.furniture.LastOrDefault();
                        }
                    return null;
                }
                var table = Put("table", (x, y) => true);
                var chair = table == null ? null : Put("chair", (x, y) =>
                {
                    var bb = table.boundingBox.Value;
                    int x0 = bb.X / 64, y0 = bb.Y / 64, x1 = (bb.Right - 1) / 64, y1 = (bb.Bottom - 1) / 64;
                    return (x >= x0 && x <= x1 && (y == y0 - 1 || y == y1 + 1)) || (y >= y0 && y <= y1 && (x == x0 - 1 || x == x1 + 1));
                });
                var table2 = Put("table", (x, y) => table == null || !table.boundingBox.Value.Intersects(new Rectangle(x * 64 - 64, y * 64 - 64, 320, 192)));
                Rt($"DINE placed table={table?.Name} chair={chair?.Name} seats={chair?.GetSeatPositions().Count} table2={table2?.Name} in {loc.NameOrUniqueName}");
                if (table2 != null)
                {
                    var mw = ItemRegistry.Create("(BC)" + PId("Microwave")) as StardewValley.Object;
                    table2.heldObject.Value = mw;
                    RescanPower();
                    Rt($"DINE microwave on table: node exists={pNodes.Values.Any(n => n.Kind == "Microwave" && n.Table == table2)} tableType={table2.furniture_type.Value} lowDraw={table2.drawHeldObjectLow.Value}");
                    dineTable2 = table2; dineLoc = loc;
                }
                walkCache.Clear();
                var chestTile = new Vector2(door.X + 4, door.Y + 4);
                farm.objects.Remove(chestTile);
                var chest = new StardewValley.Objects.Chest(true, chestTile);
                chest.modData[TestKey] = "1";
                chest.Items.Add(ItemRegistry.Create("(O)24", 20));
                farm.objects[chestTile] = chest;
                foreach (var n in data.Hands) data.Hunger[n] = 85;
                Rt($"DINE chest at {chestTile}; hunger 85; hands={string.Join(",", data.Hands)}");
            }
            if (t >= 60 && t % 150 == 0 && t < 3000)
                Rt($"DINE t={t} " + string.Join(" | ", actors.Values.Select(a => $"{a.Name}[{(a.Inside ? "in" : "out")} {ToTile(a.Pos).X},{ToTile(a.Pos).Y} {a.Activity} meal={(a.Meal != null || a.MealPaste)} dining={a.Dining} hunger={HungerOf(a.Name):0}]")) +
                       $" defenses=[{string.Join("; ", Defenses.Distinct().TakeLast(4))}]");
            if (t == 40 && Environment.GetEnvironmentVariable("RIMDEW_NOTABLE") != "1")
            {
                var tbl = ItemRegistry.Create("(F)" + FurnitureIdFor("table")) as StardewValley.Objects.Furniture;
                var tile = new Vector2(door.X + 8, door.Y - 2);
                tbl!.TileLocation = tile;
                tbl.updateDrawPosition();
                farm.furniture.Add(tbl);
                tbl.heldObject.Value = ItemRegistry.Create("(BC)" + PId("Microwave")) as StardewValley.Object;
                RescanPower();
                Rt($"DINE farm table microwave node={pNodes.Values.Any(n => n.Kind == "Microwave" && n.Table == tbl)} box={tbl.boundingBox.Value}");
                Game1.player.Position = new Vector2((tile.X + 1) * 64, (tile.Y + 3) * 64); Game1.xLocationAfterWarp = 0;
            }
            
            if (t == 3000) { Rt("DINE done"); dineDone = true; }
        }
        catch (Exception ex) { Rt("DINE EXCEPTION: " + ex); dineDone = true; }
    }

    private StardewValley.Objects.Furniture? dineTable2;
    private GameLocation? dineLoc;

    private void TickRaidTest(UpdateTickedEventArgs e)
    {
        TickDineTest(e);
        TickHumanTest(e);
        TickCabinTest(e);
        TickColonyTest(e);
        if (!raidTest || !Context.IsWorldReady) return;
        if (rtStart < 0) { rtStart = (int)e.Ticks; Rt("world ready"); }
        int t = (int)e.Ticks - rtStart - 90;
        if (t == -80) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t < 0) return;
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        float x = door.X + 6, y = door.Y - 2;
        var player = Game1.player.Tile;
        try
        {
            if (t == 200) Rt("TRAITS: " + string.Join(" | ", data.Hands.Select(n => $"{n}: [{TraitSummary(n)}] dmgFactor={TraitDamageFactor(n):0.00}")));
            if (t == 60 && Environment.GetEnvironmentVariable("RIMDEW_AIMTEST") == "1")
            {
                GunBullet.Debug = m =>
                {
                    Rt("  bullet: " + m + (m.StartsWith("hit OTHER") ? "  <- " + WhatIsAt(farm, m) : ""));
                };
                var hand = actors.Values.First();
                var at = hand.Pos / 64f + new Vector2(0, 2);
                var b = new ShadowBrute(at * 64) { };
                farm.characters.Add(b);
                aimBrute = b;
                Rt($"AIM: hand {hand.Name} at {hand.Pos / 64f}, brute at {at} hp={b.Health}");
                return;
            }
            if (aimBrute != null)
            {
                if (t == 240) { Rt("MID cached: " + WhatIsAt(farm, "hit OTHER at 47.5,17.5") + " || " + WhatIsAt(farm, "hit OTHER at 47.5,18.5")); walkCache.Clear(); Rt("MID fresh: " + WhatIsAt(farm, "hit OTHER at 47.5,17.5") + " || " + WhatIsAt(farm, "hit OTHER at 47.5,18.5")); }
                if (t % 60 == 0) Rt($"AIM t={t} brute hp={aimBrute.Health} at={aimBrute.Tile} hands=[{string.Join(", ", actors.Values.Select(a => a.Name + "@" + ToTile(a.Pos) + ":" + a.Activity + " los=" + HasLineOfSight(ToTile(a.Pos), ToTile(aimBrute.Position)) + " stuck=" + a.StuckTicks + " unreach=" + a.Unreachable + " aim=" + a.Aim.ToString("0.00") + " shots=" + a.XpShooting))}]");
                if (t > 900) { raidTest = false; aimBrute = null; }
                return;
            }
            if (t == 60)
            {
                // Sapper vs trap and sandbags; a slime beside a second sandbag as the control.
                PlaceTest(new Vector2(x, y), "SpikeTrap", false);
                PlaceTest(new Vector2(x + 4, y), "SpikeTrap", false);
                PlaceTest(new Vector2(x + 8, y), "Sandbags", false);
                PlaceTest(new Vector2(x + 12, y), "Sandbags", false);
                var sap = new DustSpirit(new Vector2(x, y) * 64) { Health = 500 }; sap.MaxHealth = 500; sap.modData[SapperKey] = "1";
                var ctl = new GreenSlime(new Vector2(x + 4, y) * 64, 0) { Health = 500 }; ctl.MaxHealth = 500;
                var sapB = new DustSpirit(new Vector2(x + 8, y) * 64) { Health = 500 }; sapB.MaxHealth = 500; sapB.modData[SapperKey] = "1";
                var ctlB = new GreenSlime(new Vector2(x + 12, y) * 64, 0) { Health = 500 }; ctlB.MaxHealth = 500;
                foreach (var m in new Monster[] { sap, ctl, sapB, ctlB }) { m.Speed = 0; m.speed = 0; farm.characters.Add(m); }
                Defenses.Clear();
                Rt("placed sapper+trap, slime+trap, sapper+sandbag, slime+sandbag");
            }
            if (t == 400)
            {
                bool Trap(float dx) => farm.objects.TryGetValue(new Vector2(x + dx, y), out var o) && IsTrap(o);
                float Hp(float dx) => farm.objects.TryGetValue(new Vector2(x + dx, y), out var o) ? (float)GetD(o, HpKey, SandbagHp) : -1;
                Rt($"RESULT sapperTrapGone={!Trap(0)} slimeTrapStillThere={Trap(4)} (slime does not defuse; may have triggered) sandbagHp sapper={Hp(8)} slime={Hp(12)} defenses=[{string.Join("; ", Defenses.Distinct())}]");
                farm.characters.RemoveWhere(c => c is Monster);
                Game1.player.Position = new Vector2((x + 5) * 64, (y + 6) * 64);
            }
            if (t >= 500 && (t - 500) % 900 == 0 && (t - 500) / 900 < 5)
            {
                string[] types = { "standard", "sapper", "droppod", "mech", "manhunter" };
                string type = types[(t - 500) / 900];
                farm.characters.RemoveWhere(c => c is Monster);
                int hudBefore = Game1.hudMessages.Count;
                StartRaid(type, type + " raid", 1000);
                Rt($"started {type}: pending spawns={pendingSpawns.Count}, monsters now={farm.characters.OfType<Monster>().Count()} (should be 0 before warning ends)");
            }
            if (t >= 500 && (t - 500) % 900 == 650 && (t - 500) / 900 < 5)
            {
                string[] types = { "standard", "sapper", "droppod", "mech", "manhunter" };
                Rt($"RESULT {types[(t - 500) / 900]}: {Describe(farm.characters.OfType<Monster>(), Game1.player.Tile)}");
            }
            if (t == 5100)
            {
                Rt("done.");
                farm.characters.RemoveWhere(c => c is Monster);
                raidTest = false;
            }
        }
        catch (Exception ex) { Rt("EXCEPTION: " + ex); raidTest = false; }
    }
}
