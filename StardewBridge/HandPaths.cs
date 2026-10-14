using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewBridge;

/// <summary>Terrain-aware movement for farmhands: tile A* over the farm using the game's own collision, plus a
/// line-of-sight test so they only shoot targets their bullets can reach.</summary>
public partial class ModEntry
{
    private readonly Dictionary<Point, bool> walkCache = new();
    private static readonly Point NoGoal = new(-99999, -99999);

    private static Point ToTile(Vector2 topLeftPx) =>
        new((int)Math.Floor((topLeftPx.X + 32) / 64), (int)Math.Floor((topLeftPx.Y + 32) / 64));

    private bool Walkable(Point t)
    {
        if (walkCache.TryGetValue(t, out var cached)) return cached;
        var farm = Game1.getFarm();
        bool ok = t.X >= 0 && t.Y >= 0 && t.X < farm.map.Layers[0].LayerWidth && t.Y < farm.map.Layers[0].LayerHeight
                  && farm.isTilePassable(new xTile.Dimensions.Location(t.X, t.Y), Game1.viewport)
                  // Farmer-mode collision: the version that correctly reports open ground (the NPC-pathfinding mode reported every tile blocked).
                  && !farm.isCollidingPosition(new Rectangle(t.X * 64 + 6, t.Y * 64 + 6, 52, 52), Game1.viewport, true, 0, false, Game1.player);
        walkCache[t] = ok;
        return ok;
    }

    /// <summary>Tiles to walk through (world px, top-left), excluding the start tile. Empty = already there; null = no route.</summary>
    private List<Vector2>? FindPath(Point start, Point goal, bool allowPartial, out bool partial)
    {
        partial = false;
        if (start == goal) return new List<Vector2>();
        const int pad = 30, maxNodes = 4000;
        int minX = Math.Min(start.X, goal.X) - pad, maxX = Math.Max(start.X, goal.X) + pad;
        int minY = Math.Min(start.Y, goal.Y) - pad, maxY = Math.Max(start.Y, goal.Y) + pad;

        var open = new PriorityQueue<Point, int>();
        var cost = new Dictionary<Point, int> { [start] = 0 };
        var from = new Dictionary<Point, Point>();
        open.Enqueue(start, 0);
        var dirs = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };

        var best = start;
        int bestDist = Math.Abs(goal.X - start.X) + Math.Abs(goal.Y - start.Y);
        int visited = 0;
        while (open.Count > 0 && visited++ < maxNodes)
        {
            var cur = open.Dequeue();
            int curDist = Math.Abs(goal.X - cur.X) + Math.Abs(goal.Y - cur.Y);
            if (curDist < bestDist) { bestDist = curDist; best = cur; }
            if (cur == goal)
            {
                var path = new List<Vector2>();
                for (var p = goal; p != start; p = from[p]) path.Add(new Vector2(p.X * 64, p.Y * 64));
                path.Reverse();
                return path;
            }
            foreach (var d in dirs)
            {
                var n = new Point(cur.X + d.X, cur.Y + d.Y);
                if (n.X < minX || n.X > maxX || n.Y < minY || n.Y > maxY) continue;
                if (n != goal && !Walkable(n)) continue;          // the goal tile itself is always allowed (doors, crops' stand spots)
                int c = cost[cur] + 1;
                if (cost.TryGetValue(n, out var old) && old <= c) continue;
                cost[n] = c;
                from[n] = cur;
                open.Enqueue(n, c + Math.Abs(goal.X - n.X) + Math.Abs(goal.Y - n.Y));
            }
        }

        // Goal not reachable (a bat over a cliff, a monster behind water...): walk as close as the terrain allows.
        if (allowPartial && best != start)
        {
            partial = true;
            var path = new List<Vector2>();
            for (var p = best; p != start; p = from[p]) path.Add(new Vector2(p.X * 64, p.Y * 64));
            path.Reverse();
            return path;
        }
        return null;
    }

    /// <summary>Walk a hand toward a pixel goal along a real path. Returns true on arrival. If no route exists,
    /// sets <c>Unreachable</c> and does not move (so they never clip through terrain).</summary>
    private bool StepTo(HandActor a, Vector2 goalPx, float speed, bool allowPartial = false)
    {
        var goalTile = ToTile(goalPx);
        if (goalTile != a.PathGoal && Game1.ticks >= a.RepathTick)
        {
            a.Path = FindPath(ToTile(a.Pos), goalTile, allowPartial, out bool partial);
            a.PathPartial = partial;
            a.PathGoal = goalTile;
            a.PathIdx = 0;
            a.Unreachable = a.Path == null;
            if (a.Path == null) a.RepathTick = (int)Game1.ticks + 45;   // don't search again every frame
        }
        if (a.Unreachable) { a.Moving = false; return false; }

        bool onPath = a.Path != null && a.PathIdx < a.Path.Count;
        if (!onPath && a.PathPartial) { a.Moving = false; return false; }   // as close as terrain allows: stop, never cut across
        var next = onPath ? a.Path![a.PathIdx] : goalPx;
        var d = next - a.Pos;
        float dist = d.Length();
        if (dist <= speed)
        {
            a.Pos = next;
            if (onPath) { a.PathIdx++; return false; }
            a.Moving = false;
            return true;
        }
        a.Pos += d / dist * speed;
        a.Moving = true;
        a.Dir = Math.Abs(d.X) > Math.Abs(d.Y) ? (d.X > 0 ? 1 : 3) : (d.Y > 0 ? 0 : 2);
        return false;
    }

    /// <summary>Closest walkable tile to <paramref name="from"/> within <paramref name="radius"/>, searching outward in rings.</summary>
    private Point? NearestWalkable(Point from, int radius)
    {
        for (int r = 0; r <= radius; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var p = new Point(from.X + dx, from.Y + dy);
                    if (Walkable(p)) return p;
                }
        return null;
    }

    /// <summary>Free a hand that has stopped making progress: forget cached terrain and routes, and if it is standing on
    /// ground it could not walk on (a doorway, a building edge), put it on the nearest walkable tile.</summary>
    private void RescueHand(HandActor a)
    {
        walkCache.Clear();
        a.PathGoal = NoGoal;
        a.RepathTick = 0;
        a.Path = null;
        var here = ToTile(a.Pos);
        if (!Walkable(here) && NearestWalkable(here, 6) is { } n) a.Pos = new Vector2(n.X * 64, n.Y * 64);
    }

    private void LogStuck(HandActor a, Point monsterTile, float dist)
    {
        var t = ToTile(a.Pos);
        string W(Point p) => Walkable(p) ? "open" : "blocked";
        Monitor.Log($"[stuck] {a.Name} at tile {t.X},{t.Y} ({W(t)}); neighbours N {W(new Point(t.X, t.Y - 1))}, E {W(new Point(t.X + 1, t.Y))}, " +
                    $"S {W(new Point(t.X, t.Y + 1))}, W {W(new Point(t.X - 1, t.Y))}; target tile {monsterTile.X},{monsterTile.Y}, dist {dist:0}; " +
                    $"path {(a.Path == null ? "none" : a.Path.Count.ToString())}{(a.PathPartial ? " (partial)" : "")}, unreachable {a.Unreachable}", LogLevel.Info);
    }

    /// <summary>True if every tile between the two (exclusive) is open ground, i.e. a bullet would not hit terrain first.</summary>
    private bool HasLineOfSight(Point from, Point to)
    {
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            if ((x0 != from.X || y0 != from.Y) && (x0 != x1 || y0 != y1) && !Walkable(new Point(x0, y0))) return false;
            if (x0 == x1 && y0 == y1) return true;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
