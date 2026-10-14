using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewBridge;

/// <summary>Developer harness. If Mods/StardewBridge/dev_selftest.flag exists, then once a save is loaded this hires two test
/// hands, builds a free bunkhouse, furnishes it, watches the hands for a while, runs a raid, and logs everything with a
/// "[selftest]" prefix. It never saves: quitting the game afterwards leaves the save untouched.</summary>
public partial class ModEntry
{
    private bool selfTest;
    private int stStart = -1;
    private int stuckLogs;
    private bool sawInside, sawUse, sawHidden, sawOutsideAfterInside, sawDefending, sawIndoorMoving;
    private int stShotsBefore;

    private void InitSelfTest()
    {
        string flag = Path.Combine(Helper.DirectoryPath, "dev_selftest.flag");
        selfTest = File.Exists(flag);
        if (selfTest) { try { File.Delete(flag); } catch { } }   // consume it so normal launches don't re-run
    }

    private void St(string msg) => Monitor.Log("[selftest] " + msg, LogLevel.Alert);

    private void TickSelfTest(UpdateTickedEventArgs e)
    {
        if (!selfTest || !Context.IsWorldReady) return;
        if (stStart < 0) { stStart = (int)e.Ticks; St("world ready, starting"); }
        int t = (int)e.Ticks - stStart;
        var farm = Game1.getFarm();

        try
        {
            if (t == 90)
            {
                foreach (var n in new[] { "TestA", "TestB" }) if (!data.Hands.Contains(n)) data.Hands.Add(n);
                SyncHands(false);
                // free bunkhouse near the farmhouse
                var door = farm.GetMainFarmHouseEntry();
                bool built = false;
                for (int dx = 8; dx < 40 && !built; dx += 2)
                    foreach (int dy in new[] { 0, -4, 4, -8, 8, 12, -12 })
                    {
                        var tile = new Vector2(door.X + dx, door.Y + dy);
                        if (farm.buildStructure(BunkFreeId, tile, Game1.player, out var b, magicalConstruction: true))
                        {
                            b.daysOfConstructionLeft.Value = 0;
                            built = true;
                            St($"bunkhouse built at {tile.X},{tile.Y}");
                            break;
                        }
                    }
                if (!built) St("FAIL: could not place a bunkhouse");
                SyncHands(true);
            }
            if (t == 240)
            {
                var bunk = farm.buildings.FirstOrDefault(b => b.buildingType.Value == BunkFreeId || b.buildingType.Value == BunkId);
                var indoors = bunk?.GetIndoors();
                if (indoors == null) { St("FAIL: no bunkhouse interior"); }
                else
                {
                    int placed = FurnishLocation(indoors);
                    St($"furnished interior {indoors.NameOrUniqueName} ({indoors.map.Layers[0].LayerWidth}x{indoors.map.Layers[0].LayerHeight}), {placed} pieces; " +
                       $"furniture now: {string.Join(", ", indoors.furniture.Select(f => f.Name))}; housed hands: {string.Join(",", HousedNames())}");
                    SyncHands(true);     // send everyone to bed
                }
            }
            if (t >= 300 && t % 120 == 0 && t < 4800) LogHands(t);
            if (t >= 2800 && t <= 3300 && t % 20 == 0 && actors.TryGetValue("Sam", out var sam))
                St($"trace t={t} Sam pos=({sam.Pos.X:0},{sam.Pos.Y:0}) tile={ToTile(sam.Pos)} moving={sam.Moving} dir={sam.Dir} idx={sam.PathIdx}/{sam.Path?.Count.ToString() ?? "null"} " +
                   $"next={(sam.Path != null && sam.PathIdx < sam.Path.Count ? $"({sam.Path[sam.PathIdx].X:0},{sam.Path[sam.PathIdx].Y:0})" : "-")} goal={sam.PathGoal} stuck={sam.StuckTicks} repathIn={Math.Max(0, sam.RepathTick - (int)Game1.ticks)} unreach={sam.Unreachable} partial={sam.PathPartial}");

            if (t == 2700)
            {
                stShotsBefore = actors.Values.Sum(a => a.XpShooting);
                St($"raid starting (4 slimes), monsters now: {farm.characters.OfType<StardewValley.Monsters.Monster>().Count()}");
                SpawnMonsters(4, "slime");
            }
            if (t == 4500)
            {
                int shots = actors.Values.Sum(a => a.XpShooting) - stShotsBefore;
                int left = farm.characters.OfType<StardewValley.Monsters.Monster>().Count();
                St($"RESULT inside={sawInside} movedIndoors={sawIndoorMoving} usedFurniture={sawUse} sleptHidden={sawHidden} cameOut={sawOutsideAfterInside} " +
                   $"defended={sawDefending} shotsFired={shots} monstersLeft={left} stuckLogs={stuckLogs}");
                St("done. You can close the game; nothing was saved.");
                farm.characters.RemoveWhere(c => c is StardewValley.Monsters.Monster);
            }
        }
        catch (Exception ex) { St("EXCEPTION: " + ex); selfTest = false; }
    }

    private void LogHands(int t)
    {
        var parts = new List<string>();
        foreach (var a in actors.Values)
        {
            var loc = LocOf(a);
            var tile = ToTile(a.Pos);
            if (a.Inside) { sawInside = true; if (a.Moving) sawIndoorMoving = true; }
            if (a.AtUse) sawUse = true;
            if (a.Hidden) sawHidden = true;
            if (!a.Inside && sawInside) sawOutsideAfterInside = true;
            if (a.Activity.Contains("defending")) sawDefending = true;
            parts.Add($"{a.Name}[{(a.Inside ? "in" : "out")} {loc.Name} {tile.X},{tile.Y} {a.Activity}{(a.UseTarget != null ? ":" + a.UseTarget.Name : "")}{(a.Hidden ? " HIDDEN" : "")}]");
        }
        St($"t={t} " + string.Join(" ", parts));
    }
}
