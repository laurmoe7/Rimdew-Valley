using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>Developer hooks, driven by flag files in the mod folder (each is consumed on use, so normal launches ignore them):
/// dev_autoload.txt holds a save folder name to load straight from the title screen; dev_powertest.flag runs a power/turret
/// scenario once the world is ready. Nothing here ever saves.</summary>
public partial class ModEntry
{
    private string? autoloadSave;
    private bool powerTest, foodTest, chestTest, indoorTest, raidTest;
    private int ptStart = -1;
    private int ptKills0;
    private bool autoloadStarted;

    private void InitDevAutoload()
    {
        string a = Path.Combine(Helper.DirectoryPath, "dev_autoload.txt");
        if (File.Exists(a)) { autoloadSave = File.ReadAllText(a).Trim(); try { File.Delete(a); } catch { } }
        string f = Path.Combine(Helper.DirectoryPath, "dev_powertest.flag");
        powerTest = File.Exists(f);
        if (powerTest) { try { File.Delete(f); } catch { } }
        string ff = Path.Combine(Helper.DirectoryPath, "dev_foodtest.flag");
        foodTest = File.Exists(ff);
        if (foodTest) { try { File.Delete(ff); } catch { } }
        string cf = Path.Combine(Helper.DirectoryPath, "dev_chesttest.flag");
        chestTest = File.Exists(cf);
        if (chestTest) { try { File.Delete(cf); } catch { } }
        string inf = Path.Combine(Helper.DirectoryPath, "dev_indoortest.flag");
        indoorTest = File.Exists(inf);
        if (indoorTest) { try { File.Delete(inf); } catch { } }
        string rf = Path.Combine(Helper.DirectoryPath, "dev_raidtest.flag");
        raidTest = File.Exists(rf);
        if (raidTest) { try { File.Delete(rf); } catch { } }
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickRaidTest(e);
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickIndoorTest(e);
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickChestTest(e);
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickFoodTest(e);
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickAutoload(e);
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickPowerTest(e);
        Helper.Events.Display.Rendered += (_, _) => { if (Game1.activeClickableMenu == null) CaptureShot(); };
        Helper.Events.Display.RenderedActiveMenu += (_, _) => CaptureShot();
    }

    private void TickAutoload(UpdateTickedEventArgs e)
    {
        if (autoloadSave == null || autoloadStarted || e.Ticks < 30) return;
        if (Game1.activeClickableMenu is not TitleMenu) return;
        autoloadStarted = true;
        Monitor.Log($"[dev] autoloading save {autoloadSave}", LogLevel.Alert);
        try { SaveGame.Load(autoloadSave); Game1.exitActiveMenu(); }
        catch (Exception ex) { Monitor.Log("[dev] autoload failed: " + ex, LogLevel.Error); }
    }

    /// <summary>Draw the open menu into an offscreen target and save it (run from an update tick, never inside a draw).</summary>
    private void MenuShot(string name)
    {
        try
        {
            var gd = Game1.graphics.GraphicsDevice;
            int w = Game1.uiViewport.Width, h = Game1.uiViewport.Height;
            using var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(gd, w, h);
            gd.SetRenderTarget(rt);
            gd.Clear(new Color(70, 110, 60));
            using (var b = new Microsoft.Xna.Framework.Graphics.SpriteBatch(gd))
            {
                b.Begin(Microsoft.Xna.Framework.Graphics.SpriteSortMode.Deferred, Microsoft.Xna.Framework.Graphics.BlendState.AlphaBlend, Microsoft.Xna.Framework.Graphics.SamplerState.PointClamp);
                Game1.activeClickableMenu?.draw(b);
                b.End();
            }
            gd.SetRenderTarget(null);
            Directory.CreateDirectory(Path.Combine(Helper.DirectoryPath, "shots"));
            using var fs = File.Create(Path.Combine(Helper.DirectoryPath, "shots", name + ".png"));
            rt.SaveAsPng(fs, w, h);
            Pt($"menu screenshot saved: shots/{name}.png ({w}x{h})");
        }
        catch (Exception ex) { Pt("menu screenshot failed: " + ex.Message); Game1.graphics.GraphicsDevice.SetRenderTarget(null); }
    }

    private string? shotRequest;

    /// <summary>Save the game's own backbuffer (just this window, never the desktop) to Mods/StardewBridge/shots/NAME.png.</summary>
    private void CaptureShot()
    {
        if (shotRequest == null) return;
        string name = shotRequest; shotRequest = null;
        try
        {
            var gd = Game1.graphics.GraphicsDevice;
            int w = gd.PresentationParameters.BackBufferWidth, h = gd.PresentationParameters.BackBufferHeight;
            var px = new Color[w * h];
            gd.GetBackBufferData(px);
            for (int y = 0; y < h / 2; y++) for (int x = 0; x < w; x++) (px[y * w + x], px[(h - 1 - y) * w + x]) = (px[(h - 1 - y) * w + x], px[y * w + x]);   // backbuffer comes back upside down
            using var tex = new Microsoft.Xna.Framework.Graphics.Texture2D(gd, w, h);
            tex.SetData(px);
            Directory.CreateDirectory(Path.Combine(Helper.DirectoryPath, "shots"));
            using var fs = File.Create(Path.Combine(Helper.DirectoryPath, "shots", name + ".png"));
            tex.SaveAsPng(fs, w, h);
            Pt($"screenshot saved: shots/{name}.png ({w}x{h})");
        }
        catch (Exception ex) { Pt("screenshot failed: " + ex.Message); }
    }

    private void Pt(string msg) => Monitor.Log("[powertest] " + msg, LogLevel.Alert);

    private SObject? PlaceTest(Vector2 tile, string id, bool big = true)
    {
        var farm = Game1.getFarm();
        farm.objects.Remove(tile);
        farm.terrainFeatures.Remove(tile);
        var o = (SObject)ItemRegistry.Create((big ? "(BC)" : "(O)") + PId(id));
        o.TileLocation = tile;
        o.modData[TestKey] = "1";   // tagged so a save made mid-test is cleaned on the next load
        farm.objects[tile] = o;
        return o;
    }

    private int ptShots0, pendingExplosiveSlime;

    /// <summary>Scenario: a hand must fetch wood and ore from a chest to power and arm a turret; then slimes attack across
    /// sandbags and traps.</summary>
    private void TickPowerTest(UpdateTickedEventArgs e)
    {
        if (!powerTest || !Context.IsWorldReady) return;
        if (ptStart < 0) { ptStart = (int)e.Ticks; Pt("world ready"); GunBullet.Debug = m => { if (!m.StartsWith("hit OTHER")) Pt("  bullet: " + m); }; }
        int t = (int)e.Ticks - ptStart - 90;   // first 90 ticks: warp to the farm
        if (t == -80) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t < 0) return;
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        float x = door.X + 6, y = door.Y - 2;   // open ground east of the farmhouse
        try
        {
            if (t == 60)
            {
                Game1.timeOfDay = 1200;
                Game1.player.Position = new Vector2((x + 5) * 64, (y + 3) * 64);
                var gen = PlaceTest(new Vector2(x, y), "WoodGenerator")!;
                SetD(gen, FuelKey, 0);                       // empty: a hand has to refuel it
                PlaceTest(new Vector2(x + 1, y), "PowerBattery");
                PlaceTest(new Vector2(x + 3, y), "PowerPole");
                var tur = PlaceTest(new Vector2(x + 9, y), "MiniTurret")!;
                SetD(tur, AmmoKey, 0);                       // empty: a hand has to reload it
                // supply chest with wood and ore
                var chestTile = new Vector2(x - 3, y);
                farm.objects.Remove(chestTile);
                var chest = new Chest(true, chestTile);
                chest.modData[TestKey] = "1";
                chest.Items.Add(ItemRegistry.Create("(O)388", 20));
                chest.Items.Add(ItemRegistry.Create("(O)378", 40));
                farm.objects[chestTile] = chest;
                // defences in the slimes' path
                for (int k = 0; k < 4; k++) PlaceTest(new Vector2(x + 9 + (k - 1), y + 2), "Sandbags", false);
                PlaceTest(new Vector2(x + 10, y + 4), "SpikeTrap", false);
                PlaceTest(new Vector2(x + 7, y + 4), "ExplosiveTrap", false);
                data.Hands.Add("TestPwr");
                SyncHands(true);
                Pt($"placed grid at {x},{y}; chest at {chestTile.X},{chestTile.Y}; turret ammo 0, generator fuel 0; hand TestPwr hired");
            }
            if (t >= 120 && t % 90 == 0 && t < 2400) PtLog(t);
            if (t == 1200)
            {
                var gen = farm.objects[new Vector2(x, y)];
                var tur = farm.objects[new Vector2(x + 9, y)];
                Pt($"AFTER HAND WORK: fuel={GetD(gen, FuelKey):0.0}h ammo={GetD(tur, AmmoKey):0} defenseLog=[{string.Join("; ", Defenses)}]");
                if (GetD(gen, FuelKey) <= 0 || GetD(tur, AmmoKey) <= 0) Pt("FAIL: hand did not service the grid");
                ptShots0 = turretShots;
                shotRequest = "defense_setup";
                // slimes arrive from the south, through traps and past the sandbags
                for (int i = 0; i < 4; i++)
                    farm.characters.Add(new GreenSlime(new Vector2((x + 8 + i * 1.5f) * 64, (y + 5) * 64), 0));
                farm.characters.Add(new GreenSlime(new Vector2((x + 10) * 64, (y + 4) * 64), 0));   // stands on the spike trap
                Pt("spawned 4 slimes in front of the sandbags and 1 on the spike trap");
                pendingExplosiveSlime = (int)e.Ticks + 240;
            }
            if (pendingExplosiveSlime > 0 && (int)e.Ticks >= pendingExplosiveSlime)
            {
                pendingExplosiveSlime = 0;
                farm.characters.Add(new GreenSlime(new Vector2((x + 7) * 64, (y + 4) * 64), 0));   // steps on the explosive trap
                Pt("slime placed on the explosive trap");
            }
            if (t == 1500) shotRequest = "defense_fight";
            if (t == 2400)
            {
                var mons = farm.characters.OfType<Monster>().ToList();
                var sb = farm.objects.Pairs.Count(kv => IsSandbag(kv.Value));
                Pt($"RESULT turretShots={turretShots - ptShots0} monstersLeft={mons.Count} sandbagsLeft={sb} defenseLog=[{string.Join("; ", Defenses)}]");
                Pt("done. cleaning up test objects; nothing was saved.");
                CleanTestObjects();
                farm.characters.RemoveWhere(c => c is Monster);
                powerTest = false;
            }
        }
        catch (Exception ex) { Pt("EXCEPTION: " + ex); powerTest = false; }
    }

    private void PtLog(int t)
    {
        var g = grids.Select(g => $"[{g.Nodes}n sup={g.Supply:0}W use={g.Demand:0}W bat={g.Charge:0}/{g.Capacity:0} sat={g.Satisfaction:0.00}]");
        var gen = pNodes.Values.FirstOrDefault(n => n.Kind == "Generator");
        var tur = pNodes.Values.FirstOrDefault(n => n.Kind == "Turret");
        string hands = string.Join(",", actors.Values.Select(a => $"{a.Name}:{a.Activity}@{a.Pos.X / 64:0.0},{a.Pos.Y / 64:0.0}" + (a.Current != null ? $"[job={a.Current.Kind} fetched={a.Current.Fetched} stand={StandSpot(a.Current) / 64f} walkable={Walkable(ToTile(StandSpot(a.Current)))} path={(a.Path == null ? "null" : a.PathIdx + "/" + a.Path.Count)} goal={a.PathGoal} unreach={a.Unreachable} partial={a.PathPartial} timer={a.WorkTimer}]" : "")));
        Pt($"t={t} grids={string.Join(" ", g)} fuel={(gen != null ? GetD(gen.Obj, FuelKey) : -1):0.0}h ammo={(tur != null ? GetD(tur.Obj, AmmoKey) : -1):0} hands=[{hands}] shots={turretShots - ptShots0} monsters={Game1.getFarm().characters.OfType<Monster>().Count()}");
    }

    // ---------- food / temperature / repair scenario ----------
    private int ftStart = -1;
    private int ctStart = -1;

    /// <summary>Experiment: can a Chest carry a custom item id and draw with its data sprite?</summary>
    /// <summary>Draw the placed freezer / plain chest / microwave / dispenser (and the freezer's inventory icon) into an offscreen image.</summary>
    private void ObjectShot(string name)
    {
        try
        {
            var gd = Game1.graphics.GraphicsDevice;
            var farm = Game1.getFarm();
            int w = 640, h = 360;
            using var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(gd, w, h);
            gd.SetRenderTarget(rt);
            gd.Clear(new Color(90, 140, 70));
            using (var b = new Microsoft.Xna.Framework.Graphics.SpriteBatch(gd))
            {
                b.Begin(Microsoft.Xna.Framework.Graphics.SpriteSortMode.FrontToBack, Microsoft.Xna.Framework.Graphics.BlendState.AlphaBlend, Microsoft.Xna.Framework.Graphics.SamplerState.PointClamp);
                int vx = Game1.viewport.X / 64, vy = Game1.viewport.Y / 64;
                int[] xs = { 68, 70, 72, 74 };
                for (int i = 0; i < xs.Length; i++)
                {
                    var tile = new Vector2(xs[i], 19);
                    if (!farm.objects.TryGetValue(tile, out var o)) continue;
                    var saved = Game1.viewport;
                    Game1.viewport = new xTile.Dimensions.Rectangle((xs[i] - 1 - i * 2) * 64 - 0, 17 * 64 - 0, w, h);
                    o.draw(b, xs[i], 19, 1f);
                    Game1.viewport = saved;
                }
                var fz = ItemRegistry.Create("(BC)" + FreezerId);
                fz.drawInMenu(b, new Vector2(30, 200), 1f);
                var plain = ItemRegistry.Create("(BC)130");
                plain.drawInMenu(b, new Vector2(110, 200), 1f);
                b.End();
            }
            gd.SetRenderTarget(null);
            Directory.CreateDirectory(Path.Combine(Helper.DirectoryPath, "shots"));
            using var fs = File.Create(Path.Combine(Helper.DirectoryPath, "shots", name + ".png"));
            rt.SaveAsPng(fs, w, h);
            Monitor.Log($"[chesttest] object screenshot saved: {name}", LogLevel.Alert);
        }
        catch (Exception ex) { Monitor.Log("[chesttest] object screenshot failed: " + ex, LogLevel.Alert); Game1.graphics.GraphicsDevice.SetRenderTarget(null); }
    }

    private void TickChestTest(UpdateTickedEventArgs e)
    {
        if (!chestTest || !Context.IsWorldReady) return;
        if (ctStart < 0) ctStart = (int)e.Ticks;
        int t = (int)e.Ticks - ctStart;
        if (t == 60) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t == 200)
        {
            var farm = Game1.getFarm();
            var created = ItemRegistry.Create("(BC)" + PId("Freezer"));
            Monitor.Log($"[chesttest] ItemRegistry.Create gives {created.GetType().Name}", LogLevel.Alert);
            try
            {
                var tile = new Vector2(68, 19);
                var chest = (Chest)created;
                chest.TileLocation = tile;
                farm.objects[tile] = chest;
                Monitor.Log($"[chesttest] freezer placed: {chest.ItemId} name={chest.Name} display={chest.DisplayName}", LogLevel.Alert);
                var plain = new Chest(true, new Vector2(70, 19));
                farm.objects[new Vector2(70, 19)] = plain;
                var mw = PlaceTest(new Vector2(72, 19), "Microwave");
                var np = PlaceTest(new Vector2(74, 19), "NutrientDispenser");
            }
            catch (Exception ex) { Monitor.Log("[chesttest] FAILED " + ex, LogLevel.Alert); }
            Game1.player.Position = new Vector2(71 * 64, 21 * 64);
        }
        if (t == 400) ObjectShot("chest_experiment");
        if (t == 440) { var f = Game1.getFarm(); foreach (var k in new[] { 68, 70, 72, 74 }) f.objects.Remove(new Vector2(k, 19)); chestTest = false; }
    }

    private string? ftSeason; private int ftTime;

    private void Ft(string msg) => Monitor.Log("[foodtest] " + msg, LogLevel.Alert);

    private void TickFoodTest(UpdateTickedEventArgs e)
    {
        if (!foodTest || !Context.IsWorldReady) return;
        if (ftStart < 0) { ftStart = (int)e.Ticks; Ft("world ready"); ftSeason = Game1.currentSeason; ftTime = Game1.timeOfDay; }
        int t = (int)e.Ticks - ftStart - 90;
        if (t == -80) Game1.warpFarmer("Farm", 76, 20, 2);
        if (t < 0) return;
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        float x = door.X + 6, y = door.Y - 2;
        try
        {
            if (t == 60)
            {
                Game1.timeOfDay = 1200;
                Game1.player.Position = new Vector2((x + 5) * 64, (y + 3) * 64);
                var gen = PlaceTest(new Vector2(x, y), "WoodGenerator")!;
                SetD(gen, FuelKey, 12);
                PlaceTest(new Vector2(x + 2, y), "ElectricStove");
                PlaceTest(new Vector2(x + 4, y), "Heater");
                PlaceTest(new Vector2(x + 6, y), "Cooler");
                var sb = PlaceTest(new Vector2(x + 2, y + 3), "Sandbags", false)!;
                sb.modData[HpKey] = "20";
                var chestTile = new Vector2(x - 3, y);
                farm.objects.Remove(chestTile);
                var chest = new Chest(true, chestTile);
                chest.modData[TestKey] = "1";
                chest.Items.Add(ItemRegistry.Create("(O)24", 6));      // parsnips: raw
                chest.Items.Add(ItemRegistry.Create("(O)340", 1));     // honey: snack
                chest.Items.Add(ItemRegistry.Create("(O)771", 3));     // fiber for repairs
                farm.objects[chestTile] = chest;
                foreach (var n in data.Hands) data.Hunger[n] = 75;     // everyone is hungry
                var disp = PlaceTest(new Vector2(x + 8, y), "NutrientDispenser")!;
                SetD(disp, PasteKey, 14);                               // low enough that a hand tops it up
                PlaceTest(new Vector2(x + 10, y), "Microwave");
                var frzTile = new Vector2(x - 3, y + 2);
                var frz = ItemRegistry.Create("(BC)" + FreezerId) as Chest;
                Ft($"freezer item is a {ItemRegistry.Create("(BC)" + FreezerId).GetType().Name}");
                if (frz != null)
                {
                    frz.TileLocation = frzTile; frz.modData[TestKey] = "1";
                    frz.Items.Add(ItemRegistry.Create("(O)233", 2));     // ice cream: meant to be frozen
                    frz.Items.Add(ItemRegistry.Create("(O)24", 10));     // frozen parsnips: need the microwave
                    farm.objects[frzTile] = frz;
                }
                var farChest = new Chest(true, new Vector2(x - 8, y + 5));
                farChest.modData[TestKey] = "1";
                farChest.Items.Add(ItemRegistry.Create("(O)24", 40));   // unprotected: should spoil
                farm.objects[new Vector2(x - 8, y + 5)] = farChest;
                PlantTestPatch(); GrowAllCrops();                       // a field of fully grown parsnips: harvest at once, never water
                doneWater = doneHarvest = 0;
                Ft($"set up: stove/heater/cooler at {x + 2},{x + 4},{x + 6} row {y}; damaged sandbags at {x + 2},{y + 3}; chest with 6 parsnips, 1 honey, 3 fiber; hands hunger 75");
            }
            if (t >= 120 && t % 120 == 0 && t < 3000)
            {
                var meals = CountFood(i => FoodGrade(i) == 3);
                var sb = farm.objects.TryGetValue(new Vector2(x + 2, y + 3), out var s2) ? GetD(s2, HpKey, SandbagHp) : -1;
                Ft($"t={t} meals={meals} raw={CountFood(i => FoodGrade(i) == 1)} sandbagHp={sb:0} hunger=[{string.Join(" ", data.Hands.Select(n => n + ":" + HungerOf(n).ToString("0")))}] hands=[{string.Join(",", actors.Values.Select(a => a.Name + ":" + a.Activity + "@" + (a.Pos.X / 64).ToString("0.0") + "," + (a.Pos.Y / 64).ToString("0.0") + (a.Current != null ? $"[{a.Current.Kind} stand={StandSpot(a.Current) / 64f} path={(a.Path == null ? "null" : a.PathIdx + "/" + a.Path.Count)} unreach={a.Unreachable} timer={a.WorkTimer}]" : "[none] jobs=" + a.Jobs.Count)))}]");
            }
            if (t == 2000) Game1.activeClickableMenu = new RimDewMenu(this, 2);
            if (t == 2030) MenuShot("colony_tab");
            if (t == 2060) Game1.exitActiveMenu();
            if (t == 2070) Game1.activeClickableMenu = new RimDewMenu(this, 5);
            if (t == 2100) MenuShot("items_tab");
            if (t == 2110) Game1.exitActiveMenu();
            if (t == 2120) Game1.activeClickableMenu = new RimDewMenu(this, 6);
            if (t == 2150) MenuShot("test_tab");
            if (t == 2160) Game1.exitActiveMenu();
            if (t == 2500)
            {
                var near = (Chest)farm.objects[new Vector2(x - 3, y + 2)];
                var far = (Chest)farm.objects[new Vector2(x - 8, y + 5)];
                int nearBefore = near.Items.Where(Perishable).Sum(i => i.Stack), farBefore = far.Items.Where(Perishable).Sum(i => i.Stack);
                var (lost, saved) = SpoilFood(0.5);
                int nearAfter = near.Items.Where(Perishable).Sum(i => i.Stack), farAfter = far.Items.Where(Perishable).Sum(i => i.Stack);
                Ft($"SPOILAGE (50% test rate): beside freezer {nearBefore}->{nearAfter}, unprotected {farBefore}->{farAfter}; lost={lost} saved={saved}");
            }
            if (t == 3000)
            {
                // temperature: winter night, with and without the heater
                Game1.currentSeason = "winter"; Game1.timeOfDay = 300;
                var fake = actors.Values.First();
                var savedPos = fake.Pos; var savedInside = fake.Inside;
                fake.Inside = false;
                fake.Pos = new Vector2((x + 14) * 64, (y + 2) * 64);
                double far = TempFor(fake);
                fake.Pos = new Vector2((x + 4) * 64, (y + 2) * 64);
                double nearHeater = TempFor(fake);
                fake.Pos = savedPos; fake.Inside = savedInside;
                SetTempEvent(-18);
                fake.Pos = new Vector2((x + 14) * 64, (y + 2) * 64); fake.Inside = false;
                double coldSnap = TempFor(fake);
                fake.Pos = savedPos; fake.Inside = savedInside;
                SetTempEvent(0);
                Game1.currentSeason = "summer"; Game1.timeOfDay = 1500;
                SetTempEvent(15);
                fake.Pos = new Vector2((x + 14) * 64, (y + 2) * 64); fake.Inside = false;
                double heatwave = TempFor(fake);
                fake.Pos = new Vector2((x + 6) * 64, (y + 2) * 64);
                double nearCooler = TempFor(fake);
                fake.Pos = savedPos; fake.Inside = savedInside;
                SetTempEvent(0);
                Ft($"TEMPS winter night open ground {far:0.0} C, beside heater {nearHeater:0.0} C, cold snap open {coldSnap:0.0} C; summer heat wave open {heatwave:0.0} C, beside cooler {nearCooler:0.0} C");
                Game1.currentSeason = ftSeason!; Game1.timeOfDay = 1200;
            }
            if (t == 3100)
            {
                Ft($"RESULT paste={StorageSummary().paste} harvested={doneHarvest} watered={doneWater} cropsLeftReady={FarmCrops(farm).Count(c => c.dirt.readyForHarvest())} mealsLeft={CountFood(i => FoodGrade(i) == 3)} hunger=[{string.Join(" ", data.Hands.Select(n => n + ":" + HungerOf(n).ToString("0")))}] log=[{string.Join("; ", Defenses)}]");
                foreach (var n in data.Hands) data.Hunger[n] = 25;
                CleanTestObjects();
                Game1.timeOfDay = ftTime;
                foodTest = false;
            }
        }
        catch (Exception ex) { Ft("EXCEPTION: " + ex); foodTest = false; }
    }

    // ---------- indoor appliances scenario ----------
    private int itStart = -1;
    private StardewValley.Buildings.Building? itBuilding;

    private void It(string msg) => Monitor.Log("[indoortest] " + msg, LogLevel.Alert);

    private string ItStatus()
    {
        var parts = pNodes.Values.Where(n => n.Loc != null).Select(n => $"{n.Kind}:{(n.Online ? "ON" : "off")}(grid {n.Grid}: {grids[n.Grid].Supply:0}/{grids[n.Grid].Demand:0}W)");
        return string.Join(" ", parts);
    }

    private void TickIndoorTest(UpdateTickedEventArgs e)
    {
        if (!indoorTest || !Context.IsWorldReady) return;
        if (itStart < 0) { itStart = (int)e.Ticks; It("world ready"); }
        int t = (int)e.Ticks - itStart;
        var farm = Game1.getFarm();
        var door = farm.GetMainFarmHouseEntry();
        try
        {
            if (t == 20) Game1.warpFarmer("Farm", 76, 20, 2);
            if (t == 150)
            {
                Game1.timeOfDay = 1200;
                foreach (var id in new[] { "Heater", "Microwave", "NutrientDispenser", "WoodGenerator", "MiniTurret" })
                    It($"{id}: can be placed indoors = {Game1.bigCraftableData["laurm.RimPower_" + id].CanBePlacedIndoors}");
                bool built = false;
                for (int ty = 6; ty < 70 && !built; ty += 2)
                    for (int tx = 6; tx < 72 && !built; tx += 2)
                        if (farm.buildStructure(BunkFreeId, new Vector2(tx, ty), Game1.player, out var b, magicalConstruction: true))
                        { b.daysOfConstructionLeft.Value = 0; itBuilding = b; built = true; It($"bunkhouse at {b.tileX.Value},{b.tileY.Value} ({b.tilesWide.Value}x{b.tilesHigh.Value}), door {b.humanDoor.Value}"); }
                if (!built) { It("FAIL: no room for a bunkhouse"); indoorTest = false; }
            }
            if (t == 300 && itBuilding != null)
            {
                var indoors = itBuilding.GetIndoors();
                if (indoors == null) { It("FAIL: no interior"); indoorTest = false; return; }
                int n = 0;
                foreach (var id in new[] { "Heater", "Microwave", "NutrientDispenser" })
                {
                    var tile = new Vector2(3 + n++, 4);
                    indoors.objects.Remove(tile);
                    var o = (SObject)ItemRegistry.Create("(BC)laurm.RimPower_" + id);
                    o.TileLocation = tile; o.modData[TestKey] = "1";
                    indoors.objects[tile] = o;
                }
                It($"placed heater, microwave and dispenser inside {indoors.NameOrUniqueName}");
            }
            if (t == 420) It("NO feeder outside: " + ItStatus());
            if (t == 450 && itBuilding != null)
            {
                var gen = PlaceTest(new Vector2(itBuilding.tileX.Value + itBuilding.tilesWide.Value + 1, itBuilding.tileY.Value + 1), "WoodGenerator")!;
                SetD(gen, FuelKey, 8);
                It($"generator placed at {gen.TileLocation} (1 tile from the building)");
            }
            if (t == 600) It("generator adjacent: " + ItStatus());
            if (t == 630 && itBuilding != null)
            {
                // an indoor heater should warm a hand standing inside on a freezing night
                SetTempEvent(-18); Game1.currentSeason = "winter"; Game1.timeOfDay = 300;
                var fake = new HandActor { Name = "ItFake", Inside = true, Bunk = itBuilding, Pos = new Vector2(4 * 64, 5 * 64) };
                double withHeater = TempFor(fake);
                var farmGen = farm.objects[new Vector2(itBuilding.tileX.Value + itBuilding.tilesWide.Value + 1, itBuilding.tileY.Value + 1)];
                farm.objects.Remove(farmGen.TileLocation);
                RescanPower(); SimulatePower(0);
                double noPower = TempFor(fake);
                SetTempEvent(0); Game1.currentSeason = ftSeason ?? "spring"; Game1.timeOfDay = 1200;
                It($"cold-snap night inside: heater powered {withHeater:0.0} C, generator removed {noPower:0.0} C");
            }
            if (t == 700)
            {
                if (itBuilding != null) farm.destroyStructure(itBuilding);
                CleanTestObjects();
                It("RESULT done; test bunkhouse and objects removed. nothing was saved.");
                indoorTest = false;
            }
        }
        catch (Exception ex) { It("EXCEPTION: " + ex); indoorTest = false; }
    }
}
