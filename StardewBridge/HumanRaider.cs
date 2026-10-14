using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Projectiles;

namespace StardewBridge;

/// <summary>A human raider: chases and fights like a Shadow Brute, drawn as a villager, and half of them shoot from range.
/// At 0 HP they fall down instead of dying; you can capture a downed raider. They are never written to the save
/// (they are removed before saving), so the save stays plain vanilla.</summary>
public class HumanRaider : ShadowBrute
{
    public bool Downed;
    public string Look = "Marlon";
    public string RaiderName = "Raider";
    public bool Ranged;
    private int nextShot;
    private Vector2 lastPos;
    private int moveTick = -100;
    private Texture2D? sheet;

    public HumanRaider() { }

    public HumanRaider(Vector2 position, string look, string name, bool ranged) : base(position)
    {
        Look = look;
        RaiderName = name;
        Ranged = ranged;
        Health = MaxHealth = 70 + Game1.random.Next(0, 31);
        damageToFarmer.Value = ranged ? 6 : 10;
        lastPos = position;
    }

    public static string[] Looks => new[] { "Marlon", "Gus", "Pam", "George", "Kent", "Willy" };
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

    public override void update(GameTime time, GameLocation location)
    {
        if (Downed) return;   // lies where it fell: no AI, no contact damage
        base.update(time, location);
    }

    public override void behaviorAtGameTick(GameTime time)
    {
        if (Downed) return;
        base.behaviorAtGameTick(time);
        if (!Ranged || currentLocation == null || Game1.ticks < nextShot) return;
        var player = Game1.player;
        if (player.currentLocation != currentLocation) return;
        var from = GetBoundingBox().Center.ToVector2();
        var to = player.GetBoundingBox().Center.ToVector2();
        float dist = Vector2.Distance(from, to);
        if (dist < 160 || dist > 520) return;      // close up they fight with their hands instead
        nextShot = (int)Game1.ticks + 100 + Game1.random.Next(0, 60);
        var dir = Vector2.Normalize(to - from);
        currentLocation.projectiles.Add(new BasicProjectile(8, 12, 0, 0, 0f, dir.X * 10f, dir.Y * 10f, from - new Vector2(32, 32),
            "hitEnemy", "", "Cowboy_gunshot", false, false, currentLocation, this));
    }

    public override void draw(SpriteBatch b)
    {
        if (IsInvisible || !Utility.isOnScreen(Position, 128)) return;
        sheet ??= Game1.content.Load<Texture2D>("Characters/" + Look);
        var local = Game1.GlobalToLocal(Game1.viewport, Position);
        float depth = Math.Max(0f, GetBoundingBox().Bottom / 10000f);

        bool moving = Vector2.DistanceSquared(Position, lastPos) > 0.04f;
        lastPos = Position;
        if (moving) moveTick = (int)Game1.ticks;
        int row = FacingDirection switch { 2 => 0, 1 => 1, 0 => 2, _ => 3 };
        int col = Game1.ticks - moveTick < 6 ? (int)(Game1.ticks / 8 % 4) : 0;
        var src = new Rectangle(col * 16, Math.Min(row * 32, sheet.Height - 32), 16, 32);
        var tint = Downed ? new Color(200, 160, 160) : new Color(255, 190, 190);   // raiders have a red cast
        float flash = shakeTimer > 0 ? 0.5f : 0f;

        b.Draw(Game1.shadowTexture, local + new Vector2(32, 60), null, Color.White, 0f,
            new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y), 4f, SpriteEffects.None, depth - 0.0001f);
        if (Downed)
            b.Draw(sheet, local + new Vector2(32, 44), src, tint, MathHelper.PiOver2, new Vector2(8, 24), 4f, SpriteEffects.None, depth);
        else
            b.Draw(sheet, new Rectangle((int)local.X, (int)local.Y - 64, 64, 128), src, Color.Lerp(tint, Color.White, flash), 0f, Vector2.Zero, SpriteEffects.None, depth);

        string label = Downed ? $"{RaiderName} (downed: right-click to capture)" : $"{RaiderName}{(Ranged ? " (gun)" : "")}";
        var size = Game1.smallFont.MeasureString(label);
        Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(local.X + 32 - size.X / 2, local.Y - (Downed ? 24 : 92)), Downed ? Color.Gold : Color.OrangeRed, 1f, 0.99f);
    }
}
