using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Monsters;
using SObject = StardewValley.Object;

namespace StardewBridge;

/// <summary>RimWorld-style defences: sandbags (a barricade that monsters have to chew through, and that bullets fly over),
/// spike traps (hurt monsters that step on them, wear out) and explosive traps (one big blast).</summary>
public partial class ModEntry
{
    private const string DefenseSheetAsset = "Mods/laurm.StardewBridge/DefenseSprites";
    private const string HpKey = "laurm.RimPower/hp", UsesKey = "laurm.RimPower/uses";
    private const int SandbagHp = 100, SpikeUses = 20;

    private record DefItem(string Id, string Name, string Description, string Recipe, string Ingredients, int Count);

    private static readonly DefItem[] DefItems =
    {
        new("Sandbags",      "Sandbags",       "A barricade. Monsters chew through it (100 HP); bullets fly over it.", "Sandbags",       "771 8 330 2", 4),
        new("SpikeTrap",     "Spike Trap",     "Hurts monsters that walk over it. Wears out after 20 hits.",         "Spike Trap",     "335 1 388 6", 2),
        new("ExplosiveTrap", "Explosive Trap", "A one-shot blast when a monster steps on it.",                       "Explosive Trap", "286 1 335 1 382 1", 1),
    };

    private static bool IsSandbag(SObject? o) => o != null && !o.bigCraftable.Value && o.ItemId == PId("Sandbags");
    private static bool IsTrap(SObject? o) => o != null && !o.bigCraftable.Value && o.ItemId.StartsWith(PowerPrefix) && o.ItemId.EndsWith("Trap");

    private readonly Dictionary<int, int> trapCooldown = new();
    private Texture2D? defenseSheet;

    private void DefenseAssets(AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, ObjectData>().Data;
                for (int i = 0; i < DefItems.Length; i++)
                {
                    var d = DefItems[i];
                    data[PId(d.Id)] = new ObjectData
                    {
                        Name = PId(d.Id), DisplayName = d.Name, Description = d.Description, Type = "Crafting", Category = -9, Price = 0,
                        Texture = DefenseSheetAsset, SpriteIndex = i, Edibility = -300,
                        ExcludeFromShippingCollection = true, ExcludeFromRandomSale = true
                    };
                }
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, string>().Data;
                foreach (var d in DefItems) data[d.Recipe] = $"{d.Ingredients}/Home/{PId(d.Id)} {d.Count}/false/default/{d.Name}";
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(DefenseSheetAsset))
            e.LoadFrom(() => BuildDefenseSheet(), AssetLoadPriority.Exclusive);
    }

    private Texture2D BuildDefenseSheet()
    {
        if (defenseSheet != null) return defenseSheet;
        const int S = 16;
        var px = new Color[S * S * DefItems.Length];
        void Set(int cell, int x, int y, Color c) { if (x >= 0 && x < S && y >= 0 && y < S) px[y * S * DefItems.Length + cell * S + x] = c; }
        void Rect(int cell, int x, int y, int w, int h, Color c) { for (int j = y; j < y + h; j++) for (int i = x; i < x + w; i++) Set(cell, i, j, c); }

        // 0 sandbags: three stacked sacks with stitching
        var bag = new Color(190, 160, 108); var bagL = new Color(214, 188, 138); var bagD = new Color(140, 112, 72);
        void Sack(int x, int y, int w) { Rect(0, x, y, w, 5, bag); Rect(0, x, y, w, 1, bagL); Rect(0, x, y + 4, w, 1, bagD); Rect(0, x + w / 2, y + 1, 1, 3, bagD); }
        Sack(1, 9, 7); Sack(8, 9, 7); Sack(4, 4, 8);
        Rect(0, 1, 13, 14, 1, new Color(60, 48, 30));

        // 1 spike trap: steel plate with a grid of spikes
        var steel = new Color(104, 108, 118); var tip = new Color(220, 224, 232);
        Rect(1, 2, 3, 12, 11, steel); Rect(1, 2, 3, 12, 1, new Color(140, 144, 154)); Rect(1, 2, 13, 12, 1, new Color(70, 72, 80));
        for (int gx = 0; gx < 3; gx++) for (int gy = 0; gy < 3; gy++) { int sx = 4 + gx * 4, sy = 5 + gy * 3; Set(1, sx, sy, tip); Set(1, sx, sy + 1, new Color(170, 174, 184)); }

        // 2 explosive trap: squat olive canister with a red cap and a blinking light
        var olive = new Color(92, 104, 70); var oliveD = new Color(62, 72, 48);
        Rect(2, 3, 5, 10, 8, olive); Rect(2, 3, 12, 10, 1, oliveD); Rect(2, 3, 5, 1, 8, new Color(122, 136, 94));
        Rect(2, 5, 3, 6, 2, new Color(176, 50, 40)); Rect(2, 7, 1, 2, 2, new Color(60, 60, 60));
        Set(2, 8, 8, new Color(255, 80, 60)); Set(2, 7, 8, new Color(255, 80, 60));

        var dark = new Color(28, 24, 22);
        for (int cell = 0; cell < DefItems.Length; cell++)
        {
            var copy = (Color[])px.Clone();
            bool Solid(int x, int y) => x >= 0 && x < S && y >= 0 && y < S && copy[y * S * DefItems.Length + cell * S + x].A > 0;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    if (!Solid(x, y) && (Solid(x - 1, y) || Solid(x + 1, y) || Solid(x, y - 1) || Solid(x, y + 1))) Set(cell, x, y, dark);
        }
        var tex = new Texture2D(Game1.graphics.GraphicsDevice, S * DefItems.Length, S);
        tex.SetData(px);
        defenseSheet = tex;
        return tex;
    }

    internal void GiveDefenseKit()
    {
        foreach (var d in DefItems)
            Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)" + PId(d.Id), d.Id == "Sandbags" ? 20 : 5));
        Hud("Defense kit: 20 sandbags, 5 spike traps, 5 explosive traps.", HUDMessage.achievement_type);
    }

    // ---------- behaviour ----------
    private void TickDefense(int ticks)
    {
        if (!Context.IsWorldReady) return;
        var farm = Game1.getFarm();
        if (Game1.currentLocation != farm || farm.characters.Count == 0) return;
        if (ticks % 6 != 0) return;

        foreach (var m in farm.characters.OfType<Monster>().ToList())
        {
            if (m.Health <= 0) continue;
            var box = m.GetBoundingBox();

            // traps: triggered by the monster's centre tile
            var tile = new Vector2(box.Center.X / 64, box.Center.Y / 64);
            if (farm.objects.TryGetValue(tile, out var t) && IsTrap(t) && (!trapCooldown.TryGetValue(m.GetHashCode(), out int until) || ticks >= until))
            {
                trapCooldown[m.GetHashCode()] = ticks + 30;
                if (t.ItemId.EndsWith("ExplosiveTrap"))
                {
                    farm.objects.Remove(tile);
                    farm.explode(tile, 3, Game1.player, false, 90, false);
                    Defenses.Add("explosive trap went off");
                }
                else
                {
                    farm.damageMonster(box, 20, 30, false, 0f, 0, 0f, 1f, false, Game1.player);
                    farm.playSound("hitEnemy");
                    int uses = (int)(t.modData.TryGetValue(UsesKey, out var us) && int.TryParse(us, out var u) ? u : SpikeUses) - 1;
                    if (uses <= 0) { farm.objects.Remove(tile); farm.playSound("axchop"); }
                    else t.modData[UsesKey] = uses.ToString();
                    Defenses.Add("spike trap hit");
                }
            }

            // sandbags: monsters pressing against one chew it down
            if (ticks % 30 == 0)
                foreach (var (sTile, sb) in SandbagsNear(farm, box))
                {
                    int hp = (int)(sb.modData.TryGetValue(HpKey, out var hs) && int.TryParse(hs, out var h) ? h : SandbagHp) - 8;
                    if (hp <= 0) { farm.objects.Remove(sTile); farm.playSound("woodWhack"); Defenses.Add("sandbag destroyed"); }
                    else sb.modData[HpKey] = hp.ToString();
                }
        }
    }

    private IEnumerable<(Vector2, SObject)> SandbagsNear(GameLocation farm, Rectangle box)
    {
        var area = new Rectangle(box.X - 12, box.Y - 12, box.Width + 24, box.Height + 24);
        for (int x = area.Left / 64; x <= area.Right / 64; x++)
            for (int y = area.Top / 64; y <= area.Bottom / 64; y++)
            {
                var tile = new Vector2(x, y);
                if (farm.objects.TryGetValue(tile, out var o) && IsSandbag(o) && area.Intersects(new Rectangle(x * 64, y * 64, 64, 64))) yield return (tile, o);
            }
    }

    /// <summary>Counters the dev test reads (what the defences actually did).</summary>
    internal readonly List<string> Defenses = new();

    private string DescribeDefense(SObject o)
    {
        if (IsSandbag(o)) return $"Sandbags: {(o.modData.TryGetValue(HpKey, out var h) ? h : SandbagHp.ToString())} / {SandbagHp} HP.";
        if (o.ItemId.EndsWith("SpikeTrap")) return $"Spike trap: {(o.modData.TryGetValue(UsesKey, out var u) ? u : SpikeUses.ToString())} hits left.";
        return "Explosive trap: armed.";
    }
}

/// <summary>Traps lie flat on the ground, so everyone can walk over them.</summary>
[HarmonyPatch(typeof(SObject), nameof(SObject.isPassable))]
internal static class TrapPassablePatch
{
    private static void Postfix(SObject __instance, ref bool __result)
    {
        if (!__result && !__instance.bigCraftable.Value && __instance.ItemId is { } id && id.StartsWith("laurm.RimPower_") && id.EndsWith("Trap"))
            __result = true;
    }
}
