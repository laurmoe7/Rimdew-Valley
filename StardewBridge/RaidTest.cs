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

    private bool cxDone;
    private int cxStart = -1;

    /// <summary>RIMDEW_CHESTTEST=1: open a chest menu (to see where the Storage settings button lands), then the settings panel; check the filter.</summary>
    private void TickChestSettingsTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_CHESTTEST") != "1" || cxDone || !Context.IsWorldReady) return;
        if (cxStart < 0) cxStart = (int)e.Ticks;
        int t = (int)e.Ticks - cxStart;
        if (t == 20) Game1.warpFarmer("Farm", 76, 20, 2);
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        try
        {
            if (t == 100 && float.TryParse(Environment.GetEnvironmentVariable("RIMDEW_UISCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var uiScale))
            {
                Game1.options.desiredUIScale = uiScale;
                typeof(Game1).GetMethod("refreshWindowSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(Game1.game1, null);
                Rt($"CHEST ui scale set to {uiScale}; ui viewport now {Game1.uiViewport.Width}x{Game1.uiViewport.Height}");
            }
            if (t == 300)
            {
                Game1.exitActiveMenu();
                var chest = new StardewValley.Objects.Chest(true, new Vector2(door.X + 5, door.Y + 6));
                chest.modData[TestKey] = "1";
                chest.Items.Add(ItemRegistry.Create("(O)24", 12)); chest.Items.Add(ItemRegistry.Create("(O)388", 40)); chest.Items.Add(ItemRegistry.Create("(O)340", 2));
                farm.objects[chest.TileLocation] = chest;
                Game1.player.Position = new Vector2(chest.TileLocation.X * 64, (chest.TileLocation.Y + 1) * 64);
                chest.ShowMenu();
                Rt($"CHEST menu open: {Game1.activeClickableMenu?.GetType().Name}");
            }
            if (t == 1500 && Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu { context: StardewValley.Objects.Chest ch } igm)
            {
                // block meals and wood, expand Foods and Raw food, then open the panel
                var blocked = new HashSet<string> { "meals", "wood", "fish" };
                SaveBlocked(ch, blocked);
                StorageTree[0].Open = true; StorageTree[0].Kids[1].Open = true;
                ch.modData[PriorityKey] = "3";
                if (storagePanel != null && !storagePanel.Open) storagePanel.Click(60, Game1.uiViewport.Height / 2);   // click the Storage tab
                var meal = ItemRegistry.Create("(O)" + PId("SimpleMeal"), 1);
                var other = new StardewValley.Objects.Chest(true, new Vector2(door.X + 9, door.Y + 6)); other.modData[TestKey] = "1"; farm.objects[other.TileLocation] = other;
                var dep = DepositChest(meal, ch.TileLocation);
                Rt($"CHEST panel open; blocked={string.Join(",", BlockedLeaves(ch))}; meal deposit goes to {(dep == ch ? "BLOCKED CHEST (wrong)" : dep == other ? "the chest that allows meals (right)" : "none")}; parsnip allowed={ChestAllows(ch, ItemRegistry.Create("(O)24"))} wood allowed={ChestAllows(ch, ItemRegistry.Create("(O)388"))} sword leaf={StorageLeaf(ItemRegistry.Create("(W)0"))} pick leaf={StorageLeaf(ItemRegistry.Create("(T)Pickaxe"))}");
            }
            if (t == 2700) { Rt("CHEST done"); cxDone = true; }
        }
        catch (Exception ex) { Rt("CHEST EXCEPTION: " + ex); cxDone = true; }
    }

    private bool drDone;
    private int drStart = -1;

    /// <summary>RIMDEW_DRAFTTEST=1: draft hands, order a move, check they hold position against slimes, focus fire; check chest priorities.</summary>
    private void TickDraftTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_DRAFTTEST") != "1" || drDone || !Context.IsWorldReady) return;
        if (drStart < 0) drStart = (int)e.Ticks;
        int t = (int)e.Ticks - drStart;
        if (t == 20) Game1.warpFarmer("Farm", 76, 20, 2);
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        if (t < 900 && t % 30 == 0 && t > 60 && t < 400) { farm.characters.RemoveWhere(c => c is Monster); pendingSpawns.Clear(); }
        try
        {
            if (t == 100) { ToggleDraftAll(); Rt($"DRAFT drafted={actors.Values.Count(a => a.Drafted)}/{actors.Count}"); }
            if (t == 400)
            {
                Rt("DRAFT after 300 ticks: " + string.Join(" | ", actors.Values.Select(a => $"{a.Name}[{(a.Inside ? "in" : "out")} {ToTile(a.Pos)} {a.Activity} armed={a.Armed}]")));
                var spot = new Point(door.X + 8, door.Y + 3);
                int i = 0;
                foreach (var a in actors.Values) { a.OrderTile = new Point(spot.X + i++ * 2, spot.Y); a.PathGoal = NoGoal; }
                Rt($"DRAFT ordered move to {spot}");
            }
            if (t == 700)
            {
                Rt("DRAFT after move order: " + string.Join(" | ", actors.Values.Select(a => $"{a.Name}[{ToTile(a.Pos)} {a.Activity}]")));
                var first = actors.Values.First();
                var p = first.Pos / 64f;
                SpawnMonsters(1, "slime");
                var m = farm.characters.OfType<Monster>().First();
                m.Position = (p + new Vector2(5, 0)) * 64f; m.Health = 400; m.MaxHealth = 400; m.speed = 0;
                dragMon = m;
                Rt($"DRAFT slime placed 5 tiles east at {ToTile(m.Position)}");
            }
            if (t == 900 && dragMon != null)
            {
                Rt($"DRAFT hold-position test: slime hp={dragMon.Health}/400 hands=[{string.Join(", ", actors.Values.Select(a => a.Name + "@" + ToTile(a.Pos) + ":" + a.Activity))}]");
                foreach (var a in actors.Values) a.Focus = dragMon;
            }
            if (t == 1200 && dragMon != null) Rt($"DRAFT focus fire: slime hp={dragMon.Health} hands=[{string.Join(", ", actors.Values.Select(a => a.Name + "@" + ToTile(a.Pos) + ":" + a.Activity))}]");
            if (t == 1300)
            {
                farm.characters.RemoveWhere(c => c is Monster);
                ToggleDraftAll();
                // chest priorities: two chests, both with parsnips
                var low = new StardewValley.Objects.Chest(true, new Vector2(door.X + 2, door.Y + 8)); low.Items.Add(ItemRegistry.Create("(O)24", 5));
                var high = new StardewValley.Objects.Chest(true, new Vector2(door.X + 12, door.Y + 8)); high.Items.Add(ItemRegistry.Create("(O)24", 5));
                low.modData[TestKey] = "1"; high.modData[TestKey] = "1";
                high.modData[PriorityKey] = "3"; low.modData[PriorityKey] = "0";
                farm.objects[low.TileLocation] = low; farm.objects[high.TileLocation] = high;
                var meal = ItemRegistry.Create("(O)" + PId("SimpleMeal"), 1);
                var dep = DepositChest(meal, low.TileLocation);
                var take = NearestChestWith("raw", high.TileLocation);
                Rt($"PRIORITY deposit goes to {(dep == high ? "HIGH" : dep == low ? "LOW" : "other " + dep?.TileLocation)} (want HIGH); supplies taken from {(take == low ? "LOW" : take == high ? "HIGH" : "other")} (want LOW, although HIGH is nearer)");
                Rt($"PRIORITY names: {string.Join(",", PriorityNames)}; high={PriorityNames[ChestPriority(high)]} low={PriorityNames[ChestPriority(low)]} default={PriorityNames[ChestPriority(new StardewValley.Objects.Chest(true, Vector2.Zero))]}");
                Game1.player.Position = new Vector2(high.TileLocation.X * 64, (high.TileLocation.Y + 1) * 64);
                high.ShowMenu();
            }
            if (t == 1360) { Rt($"DRAFT done; menu={Game1.activeClickableMenu?.GetType().Name}"); drDone = true; }
        }
        catch (Exception ex) { Rt("DRAFT EXCEPTION: " + ex); drDone = true; }
    }

    private Monster? dragMon;

    private bool frzDone;
    private int frzStart = -1;

    /// <summary>RIMDEW_FRZTEST=1: craft/place a freezer like a player does, right-click it, and see whether a storage menu opens.</summary>
    private void TickFreezerTest(UpdateTickedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("RIMDEW_FRZTEST") != "1" || frzDone || !Context.IsWorldReady) return;
        if (frzStart < 0) frzStart = (int)e.Ticks;
        int t = (int)e.Ticks - frzStart;
        if (t == 20) Game1.warpFarmer("Farm", 76, 20, 2);
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        var tile = new Vector2(door.X + 6, door.Y + 4);
        try
        {
            if (t == 200)
            {
                // crafted the way the crafting page does
                var recipe = new CraftingRecipe("Freezer");
                var item = recipe.createItem();
                Rt($"FRZ crafted item: {item.GetType().Name} id={item.QualifiedItemId} bigCraftable={(item as StardewValley.Object)?.bigCraftable.Value}");
                farm.objects.Remove(tile);
                var obj = (StardewValley.Object)item;
                bool placed = obj.placementAction(farm, (int)tile.X * 64, (int)tile.Y * 64, Game1.player);
                farm.objects.TryGetValue(tile, out var placedObj);
                Rt($"FRZ placed={placed} object at tile: {placedObj?.GetType().Name} id={placedObj?.ItemId} isChest={placedObj is StardewValley.Objects.Chest}");
                Game1.player.Position = new Vector2(tile.X * 64, (tile.Y + 2) * 64);
            }
            if (t == 240) Game1.exitActiveMenu();
            if (t == 260 && farm.objects.TryGetValue(tile, out var o))
            {
                var plain = new StardewValley.Objects.Chest(true, tile + new Vector2(2, 0));
                farm.objects[tile + new Vector2(2, 0)] = plain;
                Game1.player.Position = new Vector2((tile.X + 1) * 64, (tile.Y + 2) * 64);
                Game1.player.faceDirection(0);
                var chest = o as StardewValley.Objects.Chest;
                Rt($"FRZ freezer: loc={(chest?.Location?.Name ?? "NULL")} direct={chest?.checkForAction(Game1.player)}  plainChest: loc={(plain.Location?.Name ?? "NULL")} direct={plain.checkForAction(Game1.player)}");
                Rt($"FRZ via location.checkAction: freezer={farm.checkAction(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport, Game1.player)} plain={farm.checkAction(new xTile.Dimensions.Location((int)tile.X + 2, (int)tile.Y), Game1.viewport, Game1.player)}");
            }
            if (t == 340)
            {
                Rt($"FRZ menu after wait: {Game1.activeClickableMenu?.GetType().Name}");
                Game1.exitActiveMenu();
                // and the real input path: our button handler with a simulated right click is hard; log what the handler would do
                if (farm.objects.TryGetValue(tile, out var o2)) Rt($"FRZ IsRimObject={IsRimObject(o2)} IsPowerObject={IsPowerObject(o2)} isChest={o2 is StardewValley.Objects.Chest}");
                frzDone = true;
            }
        }
        catch (Exception ex) { Rt("FRZ EXCEPTION: " + ex); frzDone = true; }
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
            if (t == 150)
            {
                var first = farm.characters.OfType<HumanRaider>().FirstOrDefault();
                // (the player stays where they are, far from the raiders, so the march is what gets measured)
            }
            if (t >= 60 && t % 180 == 0 && t < 3600)
            {
                var hs = farm.characters.OfType<HumanRaider>().ToList();
                var doorPt = farm.GetMainFarmHouseEntry();
                Rt($"HUMAN t={t} alive={hs.Count(r => !r.Downed)} downed={hs.Count(r => r.Downed)} avgDistToDoor={(hs.Count == 0 ? 0 : hs.Average(r => Vector2.Distance(r.Tile, new Vector2(doorPt.X, doorPt.Y)))):0.0} looks=[{string.Join("; ", hs.Select(r => r.RaiderName + ": " + r.Look))}] playerHp={Game1.player.health} calm={Calm()} hands=[{string.Join(", ", actors.Values.Select(a => a.Name + ":" + a.Activity))}]");
            }
            if (t == 3600)
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
        TickFreezerTest(e);
        TickDraftTest(e);
        TickChestSettingsTest(e);
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
