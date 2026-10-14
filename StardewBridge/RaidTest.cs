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

    private void TickRaidTest(UpdateTickedEventArgs e)
    {
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
