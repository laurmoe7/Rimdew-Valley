using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace StardewBridge;

/// <summary>Farmhands live in their bunkhouse: they walk in through the door, wander inside, use whatever furniture you placed
/// (sleep in beds, sit, watch TV...) and walk back out when there is work. This is simulated even when you are not watching.</summary>
public partial class ModEntry
{
    private static Point? ExitTileOf(GameLocation loc)
    {
        var w = loc.warps.FirstOrDefault();
        return w == null ? null : new Point(w.X, w.Y);
    }

    private static void ResetPath(HandActor a)
    {
        a.Path = null;
        a.PathGoal = NoGoal;
        a.RepathTick = 0;
        a.Unreachable = false;
        a.PathPartial = false;
    }

    /// <summary>Hand reaches their bunkhouse door: step inside at the interior's exit tile.</summary>
    private void EnterBunk(HandActor a)
    {
        var loc = a.Bunk?.GetIndoors();
        if (loc == null) { a.Activity = "resting"; return; }
        var exit = ExitTileOf(loc) ?? new Point(loc.map.Layers[0].LayerWidth / 2, loc.map.Layers[0].LayerHeight - 2);
        a.Inside = true;
        a.Hidden = false;
        a.AtUse = false;
        a.UseTarget = null;
        a.NextIdlePick = (int)Game1.ticks + Game1.random.Next(60, 300);
        ResetPath(a);
        a.Pos = new Vector2(exit.X * 64, exit.Y * 64);
        a.Dir = 2;                 // facing up, into the room
        a.Moving = false;
        a.Activity = "indoors";
    }

    /// <summary>Hand walks out of the door onto the farm.</summary>
    private void Emerge(HandActor a)
    {
        a.Inside = false;
        a.Hidden = false;
        a.AtUse = false;
        a.UseTarget = null;
        ResetPath(a);
        int slotIdx = Math.Max(0, data.Hands.IndexOf(a.Name)) % BunkCapacity;
        a.Pos = (a.Door ?? a.Home) + new Vector2((slotIdx - 1.5f) * 26, 8);
        a.Dir = 0;
        a.Moving = false;
    }

    private static bool IsUsable(Furniture f)
    {
        if (f is BedFurniture || f is TV) return true;
        if (f.GetSeatPositions().Count > 0) return true;
        int t = f.furniture_type.Value;
        return t == Furniture.dresser || t == Furniture.bookcase || t == Furniture.table || t == Furniture.longTable || t == Furniture.fireplace;
    }

    private static string UseVerb(Furniture f) =>
        f is BedFurniture ? "sleeping" : f is TV ? "watching TV" : f.GetSeatPositions().Count > 0 ? "sitting"
        : f.furniture_type.Value == Furniture.bookcase ? "reading" : f.furniture_type.Value == Furniture.fireplace ? "warming up" : "browsing";

    private static int DirToward(Vector2 from, Vector2 to)
    {
        var d = to - from;
        return Math.Abs(d.X) > Math.Abs(d.Y) ? (d.X > 0 ? 1 : 3) : (d.Y > 0 ? 0 : 2);
    }

    /// <summary>How many hands can use this piece at once: single bed 1, double bed 2, seating one per seat, anything else 2.</summary>
    private static int UseCapacity(Furniture f)
    {
        if (f is BedFurniture bed) return bed.bedType == BedFurniture.BedType.Double ? 2 : 1;
        int seats = f.GetSeatPositions().Count;
        return seats > 0 ? seats : 2;
    }

    private int UsersOf(Furniture f, HandActor except) =>
        actors.Values.Count(o => o != except && o.Inside && o.UseTarget == f);

    /// <summary>Choose a piece of furniture and where to stand to use it.</summary>
    private void PickIdleUse(HandActor a, GameLocation loc)
    {
        var usable = loc.furniture.Where(f => IsUsable(f) && UsersOf(f, a) < UseCapacity(f)).ToList();   // skip anything already full
        if (usable.Count == 0) { a.NextIdlePick = (int)Game1.ticks + 600; return; }
        var f = usable[Game1.random.Next(usable.Count)];

        var here = ToTile(a.Pos);
        Point? spot = null;
        var seats = f is BedFurniture ? new List<Vector2>() : f.GetSeatPositions();
        if (seats.Count > 0)
        {
            var free = seats.Where(s => !actors.Values.Any(o => o != a && o.Inside && o.UseTarget == f && ToTile(o.UseSpot) == new Point((int)s.X, (int)s.Y))).ToList();
            if (free.Count == 0) { a.NextIdlePick = (int)Game1.ticks + 300; return; }
            var s = free[Game1.random.Next(free.Count)];
            spot = new Point((int)s.X, (int)s.Y);          // sit on the seat itself
        }
        else
        {
            // stand on the open tile next to it that is closest to us
            var bb = f.boundingBox.Value;
            int x0 = bb.X / 64, y0 = bb.Y / 64, x1 = (bb.Right - 1) / 64, y1 = (bb.Bottom - 1) / 64;
            int best = int.MaxValue;
            for (int x = x0 - 1; x <= x1 + 1; x++)
                for (int y = y0 - 1; y <= y1 + 1; y++)
                {
                    bool inside = x >= x0 && x <= x1 && y >= y0 && y <= y1;
                    var p = new Point(x, y);
                    if (inside || !Walkable(p, loc)) continue;
                    int d = Math.Abs(x - here.X) + Math.Abs(y - here.Y);
                    if (d < best) { best = d; spot = p; }
                }
        }
        if (spot == null) { a.NextIdlePick = (int)Game1.ticks + 600; return; }

        a.UseTarget = f;
        a.UseSpot = new Vector2(spot.Value.X * 64, spot.Value.Y * 64);
        a.UseDir = DirToward(a.UseSpot, new Vector2(f.boundingBox.Value.Center.X, f.boundingBox.Value.Center.Y) - new Vector2(32, 32));
        a.AtUse = false;
        ResetPath(a);
    }

    /// <summary>One tick of a hand who is inside their bunkhouse.</summary>
    private void TickIndoor(HandActor a, bool busy, bool fighting)
    {
        var loc = a.Bunk?.GetIndoors();
        if (loc == null) { Emerge(a); return; }
        a.Armed = false;

        if (busy || fighting)
        {
            // Work or a raid: stop whatever they were doing and walk out through the door.
            a.Hidden = false; a.AtUse = false; a.UseTarget = null;
            var exit = ExitTileOf(loc);
            if (exit == null) { Emerge(a); return; }
            a.Activity = "heading out";
            bool arrived = StepTo(a, new Vector2(exit.Value.X * 64, exit.Value.Y * 64), HandSpeed);
            if (a.Unreachable) { a.Unreachable = false; a.PathGoal = NoGoal; arrived = true; }
            if (arrived) Emerge(a);
            return;
        }

        if (a.UseTarget == null)
        {
            a.Moving = false;
            a.Activity = "indoors";
            if (Game1.ticks >= a.NextIdlePick) PickIdleUse(a, loc);
            return;
        }

        if (!loc.furniture.Contains(a.UseTarget))   // you picked it up
        {
            a.UseTarget = null; a.AtUse = false; a.Hidden = false;
            a.NextIdlePick = (int)Game1.ticks + 120;
            return;
        }

        if (!a.AtUse)
        {
            a.Activity = "walking";
            bool arrived = StepTo(a, a.UseSpot, HandSpeed * 0.75f);
            if (a.Unreachable) { a.UseTarget = null; ResetPath(a); a.NextIdlePick = (int)Game1.ticks + 300; return; }
            if (arrived)
            {
                a.AtUse = true;
                a.UseUntil = (int)Game1.ticks + Game1.random.Next(600, 1500);
                a.Dir = a.UseDir;
                a.Activity = UseVerb(a.UseTarget);
                a.Hidden = a.UseTarget is BedFurniture;   // asleep in bed: not drawn, shows Zzz instead
            }
            return;
        }

        a.Moving = false;
        if (Game1.ticks >= a.UseUntil)
        {
            a.AtUse = false; a.UseTarget = null; a.Hidden = false;
            a.NextIdlePick = (int)Game1.ticks + Game1.random.Next(240, 900);
        }
    }

    /// <summary>Draw the hands inside the bunkhouse the player is standing in.</summary>
    private void DrawInteriorHands(SpriteBatch b)
    {
        foreach (var a in actors.Values)
        {
            if (!a.Inside || a.Bunk?.GetIndoors() != Game1.currentLocation) continue;

            if (a.Hidden)
            {
                var c = a.UseTarget is { } f ? new Vector2(f.boundingBox.Value.Center.X - 32, f.boundingBox.Value.Center.Y - 64) : a.Pos;
                var lp = Game1.GlobalToLocal(Game1.viewport, c);
                string z = $"{a.Name}: Zzz";
                Utility.drawTextWithShadow(b, z, Game1.smallFont, new Vector2(lp.X - Game1.smallFont.MeasureString(z).X / 2 + 32, lp.Y), Color.LightSkyBlue, 1f, 0.99f);
                continue;
            }

            if (a.Body != null) DrawBody(b, a, a.AtUse ? 0f : 0f);
            var local = Game1.GlobalToLocal(Game1.viewport, a.Pos);
            string label = $"{a.Name} ({a.Activity})";
            var size = Game1.smallFont.MeasureString(label);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(local.X + 32 - size.X / 2, local.Y - 100), Color.White, 1f, 0.99f);
        }
    }
}
