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
    private bool powerTest, foodTest;
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
                Ft($"RESULT mealsLeft={CountFood(i => FoodGrade(i) == 3)} hunger=[{string.Join(" ", data.Hands.Select(n => n + ":" + HungerOf(n).ToString("0")))}] log=[{string.Join("; ", Defenses)}]");
                foreach (var n in data.Hands) data.Hunger[n] = 25;
                CleanTestObjects();
                Game1.timeOfDay = ftTime;
                foodTest = false;
            }
        }
        catch (Exception ex) { Ft("EXCEPTION: " + ex); foodTest = false; }
    }
}
