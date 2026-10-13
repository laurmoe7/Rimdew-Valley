using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.Objects;
using StardewValley.GameData.Weapons;
using StardewValley.Projectiles;

namespace StardewBridge;

/// <summary>RimWorld guns in Stardew. Sprites are read from assets/guns (exported from your own RimWorld install,
/// not committed to git). Each gun is a real weapon item; while one is held it is drawn aimed at the cursor and
/// left click fires projectiles instead of swinging.</summary>
public partial class ModEntry
{
    private const string GunPrefix = "laurm.RimDew_";
    internal const string BunkId = "laurm.RimDew_Bunkhouse";
    internal const string BunkFreeId = "laurm.RimDew_BunkhouseFree";
    internal const string TestBuilder = "laurm.RimDew_Builder";
    private const string IconAsset = "Mods/laurm.StardewBridge/GunIcons";

    private record Gun(string Id, string Name, string Sprite, int MinDmg, int MaxDmg, int Cooldown, bool Auto,
                       int Pellets = 1, float Spread = 0f, float Speed = 18f, string Bullet = "Bullet_Small");

    private static readonly Gun[] Guns =
    {
        new("Revolver",        "Revolver",         "Revolver",        20, 28, 24, false),
        new("Autopistol",      "Autopistol",       "Autopistol",       9, 13, 11, true,  Spread: 0.06f),
        new("MachinePistol",   "Machine Pistol",   "MachinePistol",    6,  9,  6, true,  Spread: 0.10f),
        new("AssaultRifle",    "Assault Rifle",    "AssaultRifle",    13, 18,  9, true,  Spread: 0.05f),
        new("BoltActionRifle", "Bolt-Action Rifle","BoltActionRifle", 45, 60, 55, false, Speed: 24f, Bullet: "Bullet_Big"),
        new("SniperRifle",     "Sniper Rifle",     "SniperRifle",     85,110, 95, false, Speed: 30f, Bullet: "Bullet_Big"),
        new("Shotgun",         "Pump Shotgun",     "Shotgun",          9, 13, 55, false, Pellets: 6, Spread: 0.22f, Speed: 15f, Bullet: "Bullet_Shotgun"),
        new("ChainShotgun",    "Chain Shotgun",    "ChainShotgun",     8, 12, 30, false, Pellets: 5, Spread: 0.24f, Speed: 15f, Bullet: "Bullet_Shotgun"),
        new("Minigun",         "Minigun",          "Minigun",          7, 10,  3, true,  Spread: 0.14f),
    };

    private readonly Dictionary<string, Texture2D> gunSprites = new(); // cropped, full resolution (held in hand)
    private Texture2D? gunIconSheet;
    private int nextShotTick;
    private bool leftWasDown;
    private int lastShotTick = -100;

    // ---------- content: item definitions + inventory icons ----------
    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Buildings"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, BuildingData>().Data;
                if (!data.TryGetValue("Shed", out var shed)) return;
                var bunk = Newtonsoft.Json.JsonConvert.DeserializeObject<BuildingData>(Newtonsoft.Json.JsonConvert.SerializeObject(shed))!;
                bunk.Name = "Bunkhouse";
                bunk.Description = "A place for your farmhands to stay. Houses up to 4. They gather by the door.";
                bunk.Builder = "Robin";
                bunk.BuildCost = 2000;
                bunk.BuildDays = 2;
                bunk.BuildMaterials = new List<BuildingMaterial>
                {
                    new() { ItemId = "(O)388", Amount = 200 },   // wood
                    new() { ItemId = "(O)390", Amount = 100 }    // stone
                };
                bunk.BuildCondition = null;
                data[BunkId] = bunk;

                // Test variant: free and instant, only listed in the menu opened from the Test tab.
                var free = Newtonsoft.Json.JsonConvert.DeserializeObject<BuildingData>(Newtonsoft.Json.JsonConvert.SerializeObject(bunk))!;
                free.Name = "Bunkhouse (free)";
                free.Builder = TestBuilder;
                free.BuildCost = 0;
                free.BuildDays = 0;
                free.BuildMaterials = new List<BuildingMaterial>();
                data[BunkFreeId] = free;
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, ObjectData>().Data;
                for (int i = 0; i < Guns.Length; i++)
                {
                    var g = Guns[i];
                    data[GunPrefix + g.Id] = new ObjectData
                    {
                        Name = GunPrefix + g.Id,
                        DisplayName = g.Name,
                        Description = $"A {g.Name} from RimWorld. Hold left click to fire.",
                        Type = "Basic",
                        Category = 0,
                        Price = 0,
                        Texture = IconAsset,
                        SpriteIndex = i,
                        Edibility = -300,
                        ExcludeFromShippingCollection = true,
                        ExcludeFromRandomSale = true,
                        ContextTags = new List<string> { "not_giftable" }
                    };
                }
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Weapons"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, WeaponData>().Data;
                for (int i = 0; i < Guns.Length; i++)
                {
                    var g = Guns[i];
                    data[GunPrefix + g.Id] = new WeaponData
                    {
                        Name = GunPrefix + g.Id,
                        DisplayName = g.Name,
                        Description = $"A {g.Name} from RimWorld. Hold left click to fire.",
                        MinDamage = g.MinDmg,
                        MaxDamage = g.MaxDmg,
                        Type = 3,                 // sword class; the swing is suppressed while held
                        Texture = IconAsset,
                        SpriteIndex = i,
                        CanBeLostOnDeath = false
                    };
                }
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(IconAsset))
            e.LoadFrom(() => BuildIconSheet(), AssetLoadPriority.Exclusive);
    }

    private Texture2D? LoadGunSprite(Gun g) => LoadSprite(g.Id, g.Sprite);

    private Texture2D? LoadBulletSprite(Gun g) => LoadSprite("bullet:" + g.Bullet, g.Bullet);

    /// <summary>Load assets/guns/NAME.png, trim its padding, and cache it.</summary>
    private Texture2D? LoadSprite(string key, string file)
    {
        if (gunSprites.TryGetValue(key, out var cached)) return cached;
        try
        {
            var cropped = CropToContent(Helper.ModContent.Load<Texture2D>($"assets/guns/{file}.png"));
            gunSprites[key] = cropped;
            return cropped;
        }
        catch (Exception ex)
        {
            Monitor.Log($"Sprite '{file}' missing ({ex.Message}). Export RimWorld sprites to Mods/StardewBridge/assets/guns.", LogLevel.Warn);
            return null;
        }
    }

    /// <summary>Trim transparent padding so the gun fills its image.</summary>
    private static Texture2D CropToContent(Texture2D src)
    {
        var px = new Color[src.Width * src.Height];
        src.GetData(px);
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (px[y * src.Width + x].A > 8)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        if (maxX < 0) return src;
        int w = maxX - minX + 1, h = maxY - minY + 1;
        var outPx = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                outPx[y * w + x] = px[(minY + y) * src.Width + minX + x];
        var tex = new Texture2D(src.GraphicsDevice, w, h);
        tex.SetData(outPx);
        return tex;
    }

    /// <summary>One 16x16 icon per gun, area-averaged down from the cropped sprite and centred.</summary>
    private Texture2D BuildIconSheet()
    {
        if (gunIconSheet != null) return gunIconSheet;
        var gd = Game1.graphics.GraphicsDevice;
        var sheet = new Color[16 * 16 * Guns.Length];
        for (int i = 0; i < Guns.Length; i++)
        {
            var spr = LoadGunSprite(Guns[i]);
            if (spr == null) continue;
            var px = new Color[spr.Width * spr.Height];
            spr.GetData(px);
            float scale = 16f / Math.Max(spr.Width, spr.Height);       // source pixels per... inverse below
            int dw = Math.Max(1, (int)Math.Round(spr.Width * scale)), dh = Math.Max(1, (int)Math.Round(spr.Height * scale));
            int ox = (16 - dw) / 2, oy = (16 - dh) / 2;
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int sx0 = x * spr.Width / dw, sx1 = Math.Max(sx0 + 1, (x + 1) * spr.Width / dw);
                    int sy0 = y * spr.Height / dh, sy1 = Math.Max(sy0 + 1, (y + 1) * spr.Height / dh);
                    float r = 0, gC = 0, bC = 0, a = 0; int n = 0;
                    for (int sy = sy0; sy < sy1 && sy < spr.Height; sy++)
                        for (int sx = sx0; sx < sx1 && sx < spr.Width; sx++)
                        {
                            var c = px[sy * spr.Width + sx];
                            r += c.R; gC += c.G; bC += c.B; a += c.A; n++;
                        }
                    if (n == 0) continue;
                    // premultiplied colours average correctly
                    sheet[(i * 16 + oy + y) * 16 + ox + x] = new Color((int)(r / n), (int)(gC / n), (int)(bC / n), (int)(a / n));
                }
        }
        // icons are stacked vertically: sprite index i => row i of 16x16 cells in a 16-wide sheet
        gunIconSheet = new Texture2D(gd, 16, 16 * Guns.Length);
        gunIconSheet.SetData(sheet);
        return gunIconSheet;
    }

    private void GiveGun(Gun g)
    {
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)" + GunPrefix + g.Id));
    }

    internal void GiveGunById(string id) { var g = Guns.FirstOrDefault(x => x.Id == id); if (g != null) GiveGun(g); }

    internal void GiveAllGuns()
    {
        foreach (var g in Guns) GiveGun(g);
        Hud($"Gave {Guns.Length} RimWorld guns. Equip one and hold left click.", HUDMessage.achievement_type);
    }

    // ---------- held gun ----------
    private Gun? HeldGun()
    {
        if (!Context.IsPlayerFree || Game1.player.CurrentItem is not { } item) return null;
        string id = item.ItemId ?? "";
        return id.StartsWith(GunPrefix) ? Guns.FirstOrDefault(g => GunPrefix + g.Id == id) : null;
    }

    /// <summary>Hand position for someone standing with their feet at <paramref name="feet"/>, facing a Stardew direction
    /// (0 up, 1 right, 2 down, 3 left).</summary>
    private static Vector2 HandPosFor(Vector2 feet, int facing, bool posed = false) => feet + facing switch
    {
        1 => posed ? new Vector2(16, -34) : new Vector2(12, -22),   // posed: farmer sheet frame 89, arm straight out
        3 => posed ? new Vector2(-16, -34) : new Vector2(-12, -22),
        2 => new Vector2(12, -14),
        _ => new Vector2(-12, -24)
    };

    /// <summary>Farmer-sheet frame with both arms held out in front (facing right; flipped for left).</summary>
    private const int AimPoseFrame = 89;
    private bool playerPosed;

    private Vector2 HandPos()
    {
        var c = Game1.player.StandingPixel;
        return HandPosFor(new Vector2(c.X, c.Y), Game1.player.FacingDirection, playerPosed);
    }

    private float AimAngle()
    {
        var d = Helper.Input.GetCursorPosition().AbsolutePixels - HandPos();
        return (float)Math.Atan2(d.Y, d.X);
    }

    private Vector2 Muzzle(float angle) => HandPos() + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 40f;

    /// <summary>A normal damaging projectile that is drawn as a RimWorld bullet: a tracer rotated along its flight
    /// path with a short fading trail.</summary>
    private class GunBullet : BasicProjectile
    {
        private readonly Texture2D? sprite;
        private readonly float length;

        public GunBullet(Texture2D? sprite, float length, int damage, float vx, float vy, Vector2 start, string fireSound, GameLocation where)
            : base(damage, 12, 0, 0, 0f, vx, vy, start, "hitEnemy", "", fireSound, false, true, where, Game1.player, shotItemId: "(O)382")
        {
            this.sprite = sprite;
            this.length = length;
        }

        public override void draw(SpriteBatch b)
        {
            if (sprite == null) { base.draw(b); return; }
            var vel = new Vector2(xVelocity.Value, yVelocity.Value);
            if (vel == Vector2.Zero) return;
            float angle = (float)Math.Atan2(vel.Y, vel.X) + MathHelper.PiOver2;   // RimWorld bullet art points up
            var dir = Vector2.Normalize(vel);
            float scale = length / Math.Max(sprite.Width, sprite.Height);
            var origin = new Vector2(sprite.Width / 2f, sprite.Height / 2f);
            for (int i = 0; i < 4; i++)   // head + three fading trail copies
            {
                var world = position.Value + new Vector2(32, 32) - dir * (i * length * 0.55f);
                b.Draw(sprite, Game1.GlobalToLocal(Game1.viewport, world), null, Color.White * (1f - i * 0.28f), angle, origin,
                    scale * (1f - i * 0.12f), SpriteEffects.None, (position.Y + 64f) / 10000f);
            }
        }
    }

    private void SpawnBullet(GameLocation where, Gun gun, Vector2 muzzle, float angle, int dmg, bool withSound)
    {
        var sprite = LoadBulletSprite(gun);
        float length = gun.Bullet == "Bullet_Big" ? 44f : gun.Bullet == "Bullet_Shotgun" ? 24f : 32f;
        where.projectiles.Add(new GunBullet(sprite, length, dmg, (float)Math.Cos(angle) * gun.Speed, (float)Math.Sin(angle) * gun.Speed,
            muzzle - new Vector2(32, 32), "", where));   // position is top-left; drawn at +32,+32
    }

    /// <summary>Better shooters get better guns.</summary>
    private static Gun GunForSkill(int shooting) => shooting switch
    {
        < 4 => Guns[0],    // Revolver
        < 8 => Guns[1],    // Autopistol
        < 12 => Guns[3],   // Assault Rifle
        < 16 => Guns[4],   // Bolt-Action Rifle
        _ => Guns[5]       // Sniper Rifle
    };

    /// <summary>Guns from older builds were swords; swap them for the object version (right click then works normally).</summary>
    private void ConvertOldGuns()
    {
        var items = Game1.player.Items;
        for (int i = 0; i < items.Count; i++)
            if (items[i] is { } it && it.QualifiedItemId.StartsWith("(W)" + GunPrefix))
            {
                items[i] = ItemRegistry.Create("(O)" + it.ItemId);
                Monitor.Log($"Converted old gun {it.ItemId} to the new object version.", LogLevel.Info);
            }
    }

    private int toolLockTicks;

    /// <summary>Free the farmer from a stuck tool/special-move animation.</summary>
    internal void Unstick()
    {
        var p = Game1.player;
        p.completelyStopAnimatingOrDoingAction();
        p.UsingTool = false;
        p.canReleaseTool = false;
        p.CanMove = true;
        toolLockTicks = 0;
    }

    private void TickGuns(UpdateTickedEventArgs e)
    {
        // Safety net: holding a gun while locked in a tool action for ~0.5s means a special move got stuck.
        bool holdingGun = Game1.player.CurrentItem?.ItemId?.StartsWith(GunPrefix) == true;
        if (holdingGun && Game1.player.UsingTool && Game1.activeClickableMenu == null)
        {
            if (++toolLockTicks > 30) { Unstick(); Monitor.Log("Freed the farmer from a stuck weapon action.", LogLevel.Info); }
        }
        else toolLockTicks = 0;

        // Hardware mouse state: Game1.input is SMAPI's filtered view, which hides buttons we suppress (left click, to block the swing).
        bool leftDown = Microsoft.Xna.Framework.Input.Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        bool freshPress = leftDown && !leftWasDown;
        leftWasDown = leftDown;

        if (e.IsMultipleOf(60)) ConvertOldGuns();
        var gun = HeldGun();
        if (gun == null) return;
        float angle = AimAngle();
        // Turn the farmer toward the aim point.
        Game1.player.faceDirection(Math.Abs(Math.Cos(angle)) > Math.Abs(Math.Sin(angle)) ? (Math.Cos(angle) > 0 ? 1 : 3) : (Math.Sin(angle) > 0 ? 2 : 0));

        // Stardew's own arms-out pose when standing and aiming sideways (set after faceDirection, which resets the frame).
        int face = Game1.player.FacingDirection;
        playerPosed = (face == 1 || face == 3) && !Game1.player.isMoving() && Game1.player.CanMove && !Game1.player.UsingTool;
        if (playerPosed) Game1.player.FarmerSprite.setCurrentSingleFrame(AimPoseFrame, 32000, false, face == 3);
        Game1.player.mostRecentlyGrabbedItem = null;   // the carry pose would otherwise draw the held item above the head

        // Hold left click to keep firing at the gun's own fire rate.
        if (!leftDown || e.Ticks < nextShotTick) return;
        nextShotTick = (int)e.Ticks + gun.Cooldown;
        lastShotTick = (int)e.Ticks;

        PlayGunShot(gun, Muzzle(angle));
        var rng = Game1.random;
        for (int p = 0; p < gun.Pellets; p++)
        {
            float a = angle + (float)((rng.NextDouble() - 0.5) * 2 * gun.Spread);
            int dmg = rng.Next(gun.MinDmg, gun.MaxDmg + 1);
            SpawnBullet(Game1.currentLocation, gun, Muzzle(a), a, dmg, p == 0);
        }
    }

    /// <summary>The gun is a placeholder sword, so block both its swing (use-tool button) and its right-click special
    /// move (the action button, which starts a defensive swing and would leave the farmer stuck).</summary>
    private void SuppressSwing(ButtonPressedEventArgs e)
    {
        if (Game1.activeClickableMenu != null || HeldGun() == null) return;
        if (e.Button.IsActionButton()) Monitor.Log($"Action button pressed holding {Game1.player.CurrentItem?.QualifiedItemId}.", LogLevel.Debug);
        // New guns are plain objects (no swing, no special move), so right click still opens doors and talks to people.
        // Guns made by an older version of this mod are swords: block their swing and special move.
        if (Game1.player.CurrentItem?.QualifiedItemId.StartsWith("(W)") == true && (e.Button.IsUseToolButton() || e.Button.IsActionButton()))
            Helper.Input.Suppress(e.Button);
    }

    private void DrawHeldGun(object? sender, RenderedWorldEventArgs e)
    {
        var gun = HeldGun();
        if (gun == null) return;
        var c = Game1.player.StandingPixel;
        DrawGunSprite(e.SpriteBatch, gun, HandPos(), AimAngle(), Game1.player.FacingDirection, c.Y, lastShotTick);
    }

    /// <summary>Draw a gun in a hand, aimed along <paramref name="angle"/>. Works for the player and for farmhands.</summary>
    private void DrawGunSprite(SpriteBatch b, Gun gun, Vector2 hand, float angle, int facing, float footY, int shotTick)
    {
        if (facing == 0) return;   // seen from behind the gun would be drawn across the farmer's back, so hide it
        var tex = LoadGunSprite(gun);
        if (tex == null) return;
        var dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        bool left = dir.X < 0;
        int sinceShot = (int)Game1.ticks - shotTick;
        if (sinceShot >= 0 && sinceShot < 6) hand -= dir * (6 - sinceShot) * 1.2f;   // recoil kick

        float scale = 52f / Math.Max(tex.Width, tex.Height);
        var origin = new Vector2(left ? tex.Width * 0.72f : tex.Width * 0.28f, tex.Height * 0.62f);
        float depth = (footY + 10) / 10000f;
        b.Draw(tex, Game1.GlobalToLocal(Game1.viewport, hand), null, Color.White,
            left ? angle + (float)Math.PI : angle, origin, scale,
            left ? SpriteEffects.FlipHorizontally : SpriteEffects.None, depth);
    }
}
