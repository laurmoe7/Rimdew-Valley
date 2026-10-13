using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Weapons;
using StardewValley.Projectiles;

namespace StardewBridge;

/// <summary>RimWorld guns in Stardew. Sprites are read from assets/guns (exported from your own RimWorld install,
/// not committed to git). Each gun is a real weapon item; while one is held it is drawn aimed at the cursor and
/// left click fires projectiles instead of swinging.</summary>
public partial class ModEntry
{
    private const string GunPrefix = "laurm.RimDew_";
    private const string IconAsset = "Mods/laurm.StardewBridge/GunIcons";

    private record Gun(string Id, string Name, string Sprite, int MinDmg, int MaxDmg, int Cooldown, bool Auto,
                       int Pellets = 1, float Spread = 0f, float Speed = 18f);

    private static readonly Gun[] Guns =
    {
        new("Revolver",        "Revolver",         "Revolver",        20, 28, 24, false),
        new("Autopistol",      "Autopistol",       "Autopistol",       9, 13, 11, true,  Spread: 0.06f),
        new("MachinePistol",   "Machine Pistol",   "MachinePistol",    6,  9,  6, true,  Spread: 0.10f),
        new("AssaultRifle",    "Assault Rifle",    "AssaultRifle",    13, 18,  9, true,  Spread: 0.05f),
        new("BoltActionRifle", "Bolt-Action Rifle","BoltActionRifle", 45, 60, 55, false, Speed: 24f),
        new("SniperRifle",     "Sniper Rifle",     "SniperRifle",     85,110, 95, false, Speed: 30f),
        new("Shotgun",         "Pump Shotgun",     "Shotgun",          9, 13, 55, false, Pellets: 6, Spread: 0.22f, Speed: 15f),
        new("ChainShotgun",    "Chain Shotgun",    "ChainShotgun",     8, 12, 30, false, Pellets: 5, Spread: 0.24f, Speed: 15f),
        new("Minigun",         "Minigun",          "Minigun",          7, 10,  3, true,  Spread: 0.14f),
    };

    private readonly Dictionary<string, Texture2D> gunSprites = new(); // cropped, full resolution (held in hand)
    private Texture2D? gunIconSheet;
    private int nextShotTick;

    // ---------- content: item definitions + inventory icons ----------
    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Weapons"))
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

    private Texture2D? LoadGunSprite(Gun g)
    {
        if (gunSprites.TryGetValue(g.Id, out var cached)) return cached;
        try
        {
            var raw = Helper.ModContent.Load<Texture2D>($"assets/guns/{g.Sprite}.png");
            var cropped = CropToContent(raw);
            gunSprites[g.Id] = cropped;
            return cropped;
        }
        catch (Exception ex)
        {
            Monitor.Log($"Gun sprite '{g.Sprite}' missing ({ex.Message}). Export RimWorld sprites to Mods/StardewBridge/assets/guns.", LogLevel.Warn);
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
        Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(W)" + GunPrefix + g.Id));
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

    private Vector2 Muzzle(float angle)
    {
        var c = Game1.player.StandingPixel;
        return new Vector2(c.X, c.Y - 40) + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 44f;
    }

    private float AimAngle()
    {
        var cursor = Helper.Input.GetCursorPosition().AbsolutePixels;
        var c = Game1.player.StandingPixel;
        var d = cursor - new Vector2(c.X, c.Y - 40);
        return (float)Math.Atan2(d.Y, d.X);
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

        var gun = HeldGun();
        if (gun == null) return;
        float angle = AimAngle();
        // Turn the farmer toward the aim point.
        Game1.player.faceDirection(Math.Abs(Math.Cos(angle)) > Math.Abs(Math.Sin(angle)) ? (Math.Cos(angle) > 0 ? 1 : 3) : (Math.Sin(angle) > 0 ? 2 : 0));

        var state = Helper.Input.GetState(SButton.MouseLeft);
        bool wantsFire = gun.Auto ? state is SButtonState.Pressed or SButtonState.Held : state == SButtonState.Pressed;
        if (!wantsFire || e.Ticks < nextShotTick) return;
        nextShotTick = (int)e.Ticks + gun.Cooldown;

        var rng = Game1.random;
        for (int p = 0; p < gun.Pellets; p++)
        {
            float a = angle + (float)((rng.NextDouble() - 0.5) * 2 * gun.Spread);
            int dmg = rng.Next(gun.MinDmg, gun.MaxDmg + 1);
            var proj = new BasicProjectile(dmg, 12, 0, 0, 0f, (float)Math.Cos(a) * gun.Speed, (float)Math.Sin(a) * gun.Speed,
                Muzzle(a), "hitEnemy", "", p == 0 ? "slingshot" : "", false, true, Game1.currentLocation, Game1.player,
                shotItemId: "(O)382");
            Game1.currentLocation.projectiles.Add(proj);
        }
    }

    /// <summary>The gun is a placeholder sword, so block both its swing (use-tool button) and its right-click special
    /// move (the action button, which starts a defensive swing and would leave the farmer stuck).</summary>
    private void SuppressSwing(ButtonPressedEventArgs e)
    {
        if (Game1.activeClickableMenu != null || HeldGun() == null) return;
        if (e.Button.IsUseToolButton() || e.Button.IsActionButton())
            Helper.Input.Suppress(e.Button);
    }

    private void DrawHeldGun(object? sender, RenderedWorldEventArgs e)
    {
        var gun = HeldGun();
        if (gun == null) return;
        var tex = LoadGunSprite(gun);
        if (tex == null) return;

        float angle = AimAngle();
        bool left = Math.Cos(angle) < 0;
        var c = Game1.player.StandingPixel;
        var pivot = Game1.GlobalToLocal(Game1.viewport, new Vector2(c.X, c.Y - 40));
        float width = Math.Max(tex.Width, tex.Height);
        float scale = 56f / width;                                   // longest side ~56 px on screen
        e.SpriteBatch.Draw(tex, pivot, null, Color.White, left ? angle + (float)Math.PI : angle,
            new Vector2(left ? tex.Width * 0.7f : tex.Width * 0.3f, tex.Height / 2f), scale,
            left ? SpriteEffects.FlipHorizontally : SpriteEffects.None, (c.Y + 8) / 10000f);
    }
}
