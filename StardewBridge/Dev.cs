using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>Developer hooks, driven by flag files in the mod folder (each is consumed on use, so normal launches ignore them):
/// dev_autoload.txt holds a save folder name to load straight from the title screen; dev_powertest.flag runs a power/turret
/// scenario once the world is ready. Nothing here ever saves.</summary>
public partial class ModEntry
{
    private string? autoloadSave;
    private bool powerTest;
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
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickAutoload(e);
        Helper.Events.GameLoop.UpdateTicked += (_, e) => TickPowerTest(e);
        Helper.Events.Display.Rendered += (_, _) => CaptureShot();
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

    private SObject? PlaceTest(Microsoft.Xna.Framework.Vector2 tile, string id)
    {
        var farm = Game1.getFarm();
        farm.objects.Remove(tile);
        farm.terrainFeatures.Remove(tile);
        var o = (SObject)ItemRegistry.Create("(BC)" + PId(id));
        o.TileLocation = tile;
        farm.objects[tile] = o;
        return o;
    }

    private void TickPowerTest(UpdateTickedEventArgs e)
    {
        if (!powerTest || !Context.IsWorldReady) return;
        if (ptStart < 0) { ptStart = (int)e.Ticks; Pt("world ready"); GunBullet.Debug = m => Pt("  bullet: " + m); }
        int t = (int)e.Ticks - ptStart - 90;   // first 90 ticks: warp to the farm
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
                SetD(gen, FuelKey, 6);
                PlaceTest(new Vector2(x + 1, y), "PowerBattery");
                PlaceTest(new Vector2(x + 2, y), "SolarPanel");
                PlaceTest(new Vector2(x + 3, y), "PowerPole");
                PlaceTest(new Vector2(x + 9, y), "MiniTurret");
                PlaceTest(new Vector2(x + 9, y + 12), "MiniTurret");   // out of range of the pole: must stay offline
                Pt($"placed grid at {x},{y}; powered turret at {x + 9},{y}; isolated turret at {x + 9},{y + 12}");
            }
            if (t >= 120 && t % 60 == 0 && t < 1500) PtLog(t);
            if (t == 180)
            {
                ptKills0 = turretShots;
                for (int i = 0; i < 4; i++)
                    farm.characters.Add(new GreenSlime(new Vector2((x + 9 + (i - 2) * 1.5f) * 64, (y + 3 + (i % 2)) * 64), 0));
                for (int i = 0; i < 2; i++)
                    farm.characters.Add(new GreenSlime(new Vector2((x + 8 + i) * 64, (y + 17) * 64), 0));   // near the unpowered turret
                Pt("spawned 4 slimes near the powered turret and 2 near the isolated one");
            }
            if (t >= 244 && t <= 275)
                Pt($"  t={t} bullets=" + string.Join(" ", farm.projectiles.Select(p => $"({p.position.X / 64:0.00},{p.position.Y / 64:0.00} v={p.xVelocity.Value:0},{p.yVelocity.Value:0})")));
            if (t >= 244 && t <= 248)
                Pt($"  boxes t={t}: bullets=" + string.Join(" ", farm.projectiles.Select(p => p.getBoundingBox().ToString())) + " monsters=" + string.Join(" ", farm.characters.OfType<Monster>().Take(4).Select(m => m.Name + m.GetBoundingBox() + "hp" + m.Health + (m.IsInvisible ? "INV" : ""))));
            if (t == 244)
                for (int yy = 12; yy <= 20; yy++)
                {
                    var tl = new Vector2(79, yy);
                    Pt($"  tile 79,{yy}: obj={(farm.objects.TryGetValue(tl, out var oo) ? oo.Name : "-")} terrain={(farm.terrainFeatures.TryGetValue(tl, out var tf) ? tf.GetType().Name : "-")} passable={farm.isTilePassable(tl)}");
                }
            if (t == 300) shotRequest = "turret_day";
            if (t == 1000) shotRequest = "turret_night";
            if (t == 900)
            {
                Game1.timeOfDay = 2200;   // night: solar should be 0, generator/battery carry the grid
                Pt("time set to 22:00");
            }
            if (t == 1500)
            {
                var mons = farm.characters.OfType<Monster>().ToList();
                var tur = pNodes.Values.Where(n => n.Kind == "Turret").Select(n => $"{n.Tile.X},{n.Tile.Y}:{(n.Online ? "online" : "offline")}");
                Pt($"RESULT turretShots={turretShots - ptKills0} monstersLeft={mons.Count} turrets=[{string.Join(" ", tur)}]");
                Pt("done. nothing was saved.");
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
        Pt($"t={t} time={Game1.timeOfDay} sun={Daylight():0.00} grids={string.Join(" ", g)} fuel={(gen != null ? GetD(gen.Obj, FuelKey) : -1):0.00}h shots={turretShots - ptKills0} monsters={Game1.getFarm().characters.OfType<Monster>().Count()} projectiles={Game1.getFarm().projectiles.Count}"
            + (t is >= 240 and <= 360 ? " hp=[" + string.Join(",", Game1.getFarm().characters.OfType<Monster>().Select(m => $"{m.Health}@{m.Tile.X:0},{m.Tile.Y:0}")) + "]"
                + " proj=[" + string.Join(" ", Game1.getFarm().projectiles.Take(3).Select(p => $"{p.position.X / 64:0.0},{p.position.Y / 64:0.0}")) + "]" : ""));
    }
}
