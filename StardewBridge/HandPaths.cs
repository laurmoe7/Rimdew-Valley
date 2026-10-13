using Microsoft.Xna.Framework;
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
                  && !farm.isCollidingPosition(new Rectangle(t.X * 64 + 6, t.Y * 64 + 6, 52, 52), Game1.viewport, true, 0, false, Game1.player);
        walkCache[t] = ok;
        return ok;
    }

    /// <summary>Tiles to walk through (world px, top-left), excluding the start tile. Empty = already there; null = no route.</summary>
    private List<Vector2>? FindPath(Point start, Point goal)
    {
        if (start == goal) return new List<Vector2>();
        const int pad = 30, maxNodes = 4000;
        int minX = Math.Min(start.X, goal.X) - pad, maxX = Math.Max(start.X, goal.X) + pad;
        int minY = Math.Min(start.Y, goal.Y) - pad, maxY = Math.Max(start.Y, goal.Y) + pad;

        var open = new PriorityQueue<Point, int>();
        var cost = new Dictionary<Point, int> { [start] = 0 };
        var from = new Dictionary<Point, Point>();
        open.Enqueue(start, 0);
        var dirs = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };

        int visited = 0;
        while (open.Count > 0 && visited++ < maxNodes)
        {
            var cur = open.Dequeue();
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
        return null;
    }

    /// <summary>Walk a hand toward a pixel goal along a real path. Returns true on arrival. If no route exists,
    /// sets <c>Unreachable</c> and does not move (so they never clip through terrain).</summary>
    private bool StepTo(HandActor a, Vector2 goalPx, float speed)
    {
        var goalTile = ToTile(goalPx);
        if (goalTile != a.PathGoal)
        {
            a.Path = FindPath(ToTile(a.Pos), goalTile);
            a.PathGoal = goalTile;
            a.PathIdx = 0;
            a.Unreachable = a.Path == null;
        }
        if (a.Unreachable) { a.Moving = false; return false; }

        bool onPath = a.Path != null && a.PathIdx < a.Path.Count;
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
