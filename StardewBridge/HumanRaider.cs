using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Shirts;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.Projectiles;

namespace StardewBridge;

/// <summary>A human raider: a randomized (and a bit strange looking) farmer. Like RimWorld raiders they gather for about 10 seconds where
/// they arrive, then march on the farmhouse, fight whatever they meet (the player, with fists or guns) and camp at the door.
/// At 0 HP they fall down instead of dying; you can capture a downed raider. They are never written to the save (they are removed before
/// saving), so the save stays plain vanilla.</summary>
public class HumanRaider : ShadowBrute
{
    public const int GatherTicks = 600;     // 10 seconds before the march

    public bool Downed;
    public string Look = "";               // short description of how they look (shown for captives)
    public string RaiderName = "Raider";
    public bool Ranged;
    public Farmer? Body;
    private int nextShot;
    private int spawnTick = -1;
    private Vector2 lastPos;
    private int moveTick = -100;
    private int stuckTicks, detourUntil, detourDir;
    private Vector2 spread;                 // each raider camps at its own spot around the door
    private List<Vector2>? path;            // route to the camp, from the farmhands' pathfinder
    private int pathIdx, nextRepath;
    private static int announcedAt = -100000;

    public HumanRaider() { }

    public HumanRaider(Vector2 position, string name, bool ranged, Farmer body, string look) : base(position)
    {
        RaiderName = name;
        Ranged = ranged;
        Body = body;
        Look = look;
        Health = MaxHealth = 70 + Game1.random.Next(0, 31);
        damageToFarmer.Value = ranged ? 6 : 10;
        lastPos = position;
        spread = new Vector2(Game1.random.Next(-3, 4), Game1.random.Next(1, 4));
    }

    public static string[] Names => new[] { "Vex", "Rook", "Jax", "Mara", "Kane", "Dray", "Sloane", "Brek", "Tove", "Ash", "Corva", "Hale" };

    public override int takeDamage(int damage, int xTrajectory, int yTrajectory, bool isBomb, double addedPrecision, Farmer who)
    {
        if (Downed) return 0;
        int dealt = base.takeDamage(damage, xTrajectory, yTrajectory, isBomb, addedPrecision, who);
        if (Health <= 0) Fall();
        return dealt;
    }

    private void Fall()
    {
        Health = 1;      // never reaches 0, so the game does not kill or loot it
        Downed = true;
        damageToFarmer.Value = 0;
        speed = 0;
        xVelocity = yVelocity = 0;
        currentLocation?.playSound("hitEnemy");
        ModEntry.Instance?.NoteRaiderDown(this);
    }

    /// <summary>A downed raider lies on the ground: no collision box, so the player, hands and other raiders walk over them.
    /// (Capture uses its own click area; every targeting check already skips downed raiders.)</summary>
    public override Rectangle GetBoundingBox() => Downed ? new Rectangle(-10000, -10000, 1, 1) : base.GetBoundingBox();

    public override void update(GameTime time, GameLocation location)
    {
        if (Downed) return;   // lies where it fell: no AI, no contact damage
        base.update(time, location);
    }

    public override void behaviorAtGameTick(GameTime time)
    {
        if (Downed || currentLocation == null) return;
        if (spawnTick < 0) spawnTick = (int)Game1.ticks;
        int age = (int)Game1.ticks - spawnTick;

        // 1. gather where they arrived
        if (age < GatherTicks) return;

        // 2. the march (announced once per raid)
        if (Game1.ticks - announcedAt > 1800)
        {
            announcedAt = (int)Game1.ticks;
            Game1.addHUDMessage(new HUDMessage("The raiders are marching on your farmhouse!", HUDMessage.error_type));
        }

        var player = Game1.player;
        var here = GetBoundingBox().Center.ToVector2();
        bool playerNear = player.currentLocation == currentLocation && Vector2.Distance(here, player.GetBoundingBox().Center.ToVector2()) < 7 * 64;

        // 3. anyone in their way gets attacked: brute chase and melee when the player is close, gunfire from the ones who carry guns
        if (playerNear)
        {
            base.behaviorAtGameTick(time);
            TryShoot(here, player);
            return;
        }

        // 4. otherwise keep marching to their camp spot in front of the farmhouse door, banging on it when they arrive
        var door = (currentLocation as Farm)?.GetMainFarmHouseEntry() ?? new Point(64, 15);
        var camp = new Vector2((door.X + spread.X) * 64, (door.Y + spread.Y) * 64);
        if (Vector2.Distance(Position, camp) > 48f) MoveToward(camp);
        else if (Game1.ticks % 90 == 0 && Game1.random.NextDouble() < 0.5) currentLocation.playSound("woodWhack");
    }

    private void TryShoot(Vector2 from, Farmer player)
    {
        if (!Ranged || Game1.ticks < nextShot) return;
        var to = player.GetBoundingBox().Center.ToVector2();
        float dist = Vector2.Distance(from, to);
        if (dist < 160 || dist > 520) return;      // close up they fight with their hands instead
        nextShot = (int)Game1.ticks + 100 + Game1.random.Next(0, 60);
        var dir = Vector2.Normalize(to - from);
        currentLocation.projectiles.Add(new BasicProjectile(8, 12, 0, 0, 0f, dir.X * 10f, dir.Y * 10f, from - new Vector2(32, 32),
            "hitEnemy", "", "Cowboy_gunshot", false, false, currentLocation, this));
    }

    /// <summary>Walk toward a point in a straight line, sliding along obstacles and sidestepping when blocked for a while.</summary>
    private void MoveToward(Vector2 goal)
    {
        // Follow a real route (cliffs, water and trees are in the way on most farms); re-plan every 3 seconds or when blocked.
        var target = goal;
        if (currentLocation is Farm && ModEntry.Instance != null)
        {
            if (path == null || Game1.ticks >= nextRepath)
            {
                path = ModEntry.Instance.RaiderRoute(new Point((int)((Position.X + 32) / 64), (int)((Position.Y + 32) / 64)), new Point((int)(goal.X / 64), (int)(goal.Y / 64)));
                pathIdx = 0;
                nextRepath = (int)Game1.ticks + 180;
            }
            if (path != null)
            {
                while (pathIdx < path.Count - 1 && Vector2.Distance(Position, path[pathIdx]) < 20f) pathIdx++;
                if (pathIdx < path.Count) target = path[pathIdx];
            }
        }
        var dir = target - Position;
        if (dir.LengthSquared() < 1f) return;
        dir.Normalize();
        if (Game1.ticks < detourUntil)
            dir = detourDir switch { 0 => new Vector2(-dir.Y, dir.X), 1 => new Vector2(dir.Y, -dir.X), _ => -dir };   // sidestep or back off
        float step = 2.6f;
        var moved = false;
        foreach (var d in new[] { dir, new Vector2(dir.X, 0), new Vector2(0, dir.Y) })
        {
            if (d.LengthSquared() < 0.01f) continue;
            var next = Position + d * step;
            var box = new Rectangle((int)next.X + 8, (int)next.Y + 8, 48, 48);
            if (currentLocation.isCollidingPosition(box, Game1.viewport, false, 0, false, this)) continue;
            Position = next;
            moved = true;
            break;
        }
        FacingDirection = Math.Abs(dir.X) > Math.Abs(dir.Y) ? (dir.X > 0 ? 1 : 3) : (dir.Y > 0 ? 2 : 0);
        stuckTicks = moved ? 0 : stuckTicks + 1;
        if (stuckTicks > 45) { detourUntil = (int)Game1.ticks + 50; detourDir = Game1.random.Next(3); stuckTicks = 0; nextRepath = 0; }
    }

    public override void draw(SpriteBatch b)
    {
        if (IsInvisible || Body == null || !Utility.isOnScreen(Position, 128)) return;
        var f = Body;
        bool moving = Vector2.DistanceSquared(Position, lastPos) > 0.04f;
        lastPos = Position;
        if (moving) moveTick = (int)Game1.ticks;
        int facing = FacingDirection;
        // Farmer sheet: stand, step, stand, step2 per direction; left reuses right, flipped.
        int[] cycle = facing switch { 0 => new[] { 12, 13, 12, 14 }, 1 or 3 => new[] { 6, 7, 6, 8 }, _ => new[] { 0, 1, 0, 2 } };
        int frame = Downed ? 5 : cycle[Game1.ticks - moveTick < 6 ? (int)(Game1.ticks / 6 % cycle.Length) : 0];
        f.currentLocation = currentLocation;
        f.Position = Position;
        f.FacingDirection = Downed ? 2 : facing;
        f.FarmerSprite.setCurrentSingleFrame(frame, 32000, false, !Downed && facing == 3);
        f.draw(b);

        var local = Game1.GlobalToLocal(Game1.viewport, Position);
        // Downed: just the name, plus the capture hint only for the one the player is standing next to.
        bool near = Downed && Vector2.Distance(Tile, Game1.player.Tile) < 2.5f;
        string label = Downed ? RaiderName + (near ? " - right-click to capture" : " (downed)") : $"{RaiderName}{(Ranged ? " (gun)" : "")}";
        DrawLabel(b, label, new Vector2(local.X + 32, local.Y - (Downed ? 24 : 92)), Downed ? Color.Gold : Color.OrangeRed, near ? 0.995f : 0.99f);
    }

    /// <summary>Text on a dark backing box, centred on x, so it stays readable over grass, paths and other labels.</summary>
    private static void DrawLabel(SpriteBatch b, string text, Vector2 at, Color color, float depth)
    {
        const float scale = 0.8f;
        var size = Game1.smallFont.MeasureString(text) * scale;
        var box = new Rectangle((int)(at.X - size.X / 2) - 6, (int)at.Y - 2, (int)size.X + 12, (int)size.Y + 2);
        b.Draw(Game1.staminaRect, box, null, Color.Black * 0.6f, 0f, Vector2.Zero, SpriteEffects.None, depth - 0.0001f);
        b.DrawString(Game1.smallFont, text, new Vector2(box.X + 6, box.Y + 1), color, 0f, Vector2.Zero, scale, SpriteEffects.None, depth);
    }
}

public partial class ModEntry
{
    /// <summary>A walking route on the farm for a raider (tile corners in pixels), or null if there is none.</summary>
    internal List<Vector2>? RaiderRoute(Point from, Point to) => FindPath(from, to, true, out _);

    private static readonly string[] ColorWords = { "red", "orange", "yellow", "lime", "green", "teal", "cyan", "blue", "purple", "magenta", "pink" };

    private static string ColorWord(Color c)
    {
        float max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max < 60) return "black";
        if (max - min < 28) return max > 190 ? "white" : "grey";
        float h;
        if (max == c.R) h = 60f * (((c.G - c.B) / (max - min) + 6) % 6);
        else if (max == c.G) h = 60f * ((c.B - c.R) / (max - min) + 2);
        else h = 60f * ((c.R - c.G) / (max - min) + 4);
        return ColorWords[(int)(h / 360f * ColorWords.Length) % ColorWords.Length];
    }

    private static Color RandomStrangeColor(Random rng) => new(rng.Next(256), rng.Next(256), rng.Next(256));

    /// <summary>A randomly generated farmer to draw a raider with: random hair, skin, clothes and hat, in colours that are a little off.</summary>
    internal (Farmer body, string look)? MakeRaiderBody()
    {
        try
        {
            var rng = Game1.random;
            bool male = rng.Next(2) == 0;
            var f = new Farmer(new FarmerSprite("Characters/Farmer/farmer_base"), Vector2.Zero, 1, "Raider", new List<Item>(), male);
            f.skin.Value = rng.Next(0, 24);
            f.hair.Value = rng.Next(0, 56);
            var hair = RandomStrangeColor(rng);
            f.hairstyleColor.Value = hair;
            f.newEyeColor.Value = RandomStrangeColor(rng);
            f.pantsColor.Value = RandomStrangeColor(rng);
            f.accessory.Value = rng.Next(-1, 19);

            var shirts = Helper.GameContent.Load<Dictionary<string, ShirtData>>("Data/Shirts").Keys.ToList();
            var shirtColor = RandomStrangeColor(rng);
            if (shirts.Count > 0)
            {
                f.changeShirt(shirts[rng.Next(shirts.Count)]);
                if (f.shirtItem.Value != null) f.shirtItem.Value.clothesColor.Value = shirtColor;
            }
            string hat = "";
            if (rng.NextDouble() < 0.7)
            {
                var hats = Helper.GameContent.Load<Dictionary<string, string>>("Data/Hats").Keys.ToList();
                if (hats.Count > 0 && ItemRegistry.Create("(H)" + hats[rng.Next(hats.Count)], 1, allowNull: true) is Hat h) { f.hat.Value = h; hat = ", hat"; }
            }
            f.FarmerRenderer.MarkSpriteDirty();
            return (f, $"{ColorWord(hair)} hair, {ColorWord(shirtColor)} shirt{hat}");
        }
        catch (Exception e)
        {
            Monitor.Log($"Could not build a raider body: {e.Message}", LogLevel.Warn);
            return null;
        }
    }
}
