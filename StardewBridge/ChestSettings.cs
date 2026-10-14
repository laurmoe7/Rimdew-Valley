using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewBridge;

/// <summary>RimWorld-style storage settings for every chest: a priority and a tree of item categories that can be switched on or off.
/// Farmhands only store things in chests that allow them. The panel opens from a button on the chest's own menu, which finds a free spot
/// (never over the recolor picker or any other control).</summary>
public partial class ModEntry
{
    private const string FilterKey = "laurm.RimDew/storageBlocked";

    /// <summary>One row of the storage tree. Leaves hold the id of the kind of item they cover; groups only contain leaves.</summary>
    internal class StorageNode
    {
        public string Id = "", Name = "";
        public List<StorageNode> Kids = new();
        public bool IsLeaf => Kids.Count == 0;
        public bool Open;
    }

    private static StorageNode G(string id, string name, params StorageNode[] kids) => new() { Id = id, Name = name, Kids = kids.ToList() };
    private static StorageNode L(string id, string name) => new() { Id = id, Name = name };

    internal static readonly List<StorageNode> StorageTree = new()
    {
        G("food", "Foods",
            L("meals", "Meals"),
            G("raw", "Raw food", L("veg", "Vegetables"), L("fruit", "Fruit"), L("animal", "Animal products"), L("fish", "Fish"), L("forage", "Forage")),
            L("artisan", "Artisan goods")),
        G("res", "Raw resources", L("wood", "Wood and stone"), L("metal", "Ores and bars"), L("gems", "Gems and minerals"), L("craft", "Crafting materials"), L("loot", "Monster loot")),
        G("farm", "Farming", L("seeds", "Seeds"), L("fert", "Fertilizer"), L("flowers", "Flowers")),
        G("gear", "Equipment", L("guns", "Guns"), L("weapons", "Other weapons"), L("tools", "Tools")),
        L("other", "Everything else")
    };

    /// <summary>Which leaf of the tree an item belongs to.</summary>
    internal static string StorageLeaf(Item i)
    {
        if (i is StardewValley.Tool and not StardewValley.Tools.MeleeWeapon) return "tools";
        if (i is StardewValley.Tools.MeleeWeapon) return "weapons";
        if (i.ItemId?.StartsWith("laurm.RimDew_") == true && i.QualifiedItemId.StartsWith("(O)")) return "guns";   // our guns are objects
        if (i.ItemId == PId("SimpleMeal")) return "meals";
        return i.Category switch
        {
            -7 => "meals", -75 => "veg", -79 => "fruit", -5 or -6 or -18 => "animal", -4 => "fish", -81 or -23 => "forage",
            -26 => "artisan", -16 => "wood", -15 => "metal", -2 or -12 => "gems", -8 => "craft", -28 => "loot",
            -74 => "seeds", -19 => "fert", -80 => "flowers", _ => "other"
        };
    }

    internal static HashSet<string> BlockedLeaves(Chest c) =>
        c.modData.TryGetValue(FilterKey, out var s) && s != "" ? s.Split(',').ToHashSet() : new HashSet<string>();

    internal static bool ChestAllows(Chest c, Item item) => !BlockedLeaves(c).Contains(StorageLeaf(item));

    private static void SaveBlocked(Chest c, HashSet<string> blocked)
    {
        if (blocked.Count == 0) c.modData.Remove(FilterKey); else c.modData[FilterKey] = string.Join(",", blocked);
    }

    internal static IEnumerable<string> LeafIds(StorageNode n) => n.IsLeaf ? new[] { n.Id } : n.Kids.SelectMany(LeafIds);

    // ---------- the panel beside the chest menu ----------
    private StoragePanel? storagePanel;
    private ItemGrabMenu? storagePanelMenu;

    /// <summary>Keep the storage panel in step with the open chest menu: create it (shifting the chest menu right to make room), draw it, drop it when the menu closes.</summary>
    private void DrawChestPriority(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (Game1.activeClickableMenu is not ItemGrabMenu { context: Chest chest } menu) { storagePanel = null; storagePanelMenu = null; return; }
        if (storagePanel == null || storagePanelMenu != menu)
        {
            int panelW = 400, margin = 16;
            int free = Game1.uiViewport.Width - (menu.xPositionOnScreen + menu.width) - 120;   // room still unused on the right (keeps the side buttons on screen)
            int need = panelW + margin * 2 - menu.xPositionOnScreen;
            int shift = Math.Max(0, Math.Min(need, free));
            if (shift > 0) menu.movePosition(shift, 0);
            // beside the chest if there is room on its left (the panel then fills that space, up to 400 wide); otherwise a collapsed tab that opens over the left of the screen
            int space = menu.ItemsToGrabMenu.xPositionOnScreen - margin * 2 - 8;
            bool fits = space >= 200;
            Monitor.Log($"[storage panel] ui {Game1.uiViewport.Width}x{Game1.uiViewport.Height}, menu x={menu.xPositionOnScreen} w={menu.width}, chest area x={menu.ItemsToGrabMenu.xPositionOnScreen} w={menu.ItemsToGrabMenu.width}, shift={shift}, space={space}, fits={fits}", LogLevel.Alert);
            int room = fits ? Math.Min(panelW, space) : Math.Min(panelW, Math.Max(260, Game1.uiViewport.Width * 45 / 100));
            storagePanel = new StoragePanel(this, chest, new Rectangle(margin, 24, room, Game1.uiViewport.Height - 48), fits);
            storagePanelMenu = menu;
        }
        storagePanel.Draw(e.SpriteBatch);
        menu.drawMouse(e.SpriteBatch);
    }

    private bool TryChestPriorityClick(ButtonPressedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft || storagePanel == null || Game1.activeClickableMenu is not ItemGrabMenu) return false;
        int x = Game1.getMouseX(), y = Game1.getMouseY();
        if (!storagePanel.Bounds.Contains(x, y)) return false;
        Helper.Input.Suppress(e.Button);
        storagePanel.Click(x, y);
        return true;
    }

    private void TickStoragePanel()
    {
        if (storagePanel == null) return;
        if (Helper.Input.IsDown(SButton.MouseLeft)) storagePanel.Drag(Game1.getMouseY()); else storagePanel.EndDrag();
    }

    private void OnStorageWheel(object? sender, MouseWheelScrolledEventArgs e)
    {
        if (storagePanel != null && Game1.activeClickableMenu is ItemGrabMenu && storagePanel.Bounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
            storagePanel.Wheel(e.Delta);
    }

    /// <summary>The storage settings, drawn beside the chest menu: priority, Clear all / Allow all, and the category tree with check boxes.</summary>
    internal class StoragePanel
    {
        private readonly ModEntry mod;
        private readonly Chest chest;
        public Rectangle Bounds;
        private readonly List<(Rectangle box, Action act)> hits = new();
        private int scroll;
        private bool dragging;
        private const int RowH = 38;
        private Rectangle list, track;

        private readonly Rectangle full;
        private Rectangle tab;
        public bool Open;

        public StoragePanel(ModEntry mod, Chest chest, Rectangle bounds, bool open)
        {
            this.mod = mod; this.chest = chest; full = bounds; Open = open;
            tab = new Rectangle(0, Game1.uiViewport.Height / 2 - 24, 130, 48);
            Bounds = open ? full : tab;
        }

        private void SetOpen(bool open) { Open = open; Bounds = open ? full : tab; dragging = false; }

        private int Total => Rows().Count * RowH;
        private int MaxScroll => Math.Max(0, Total - list.Height);

        public void Click(int x, int y)
        {
            if (track.Contains(x, y)) { dragging = true; Drag(y); return; }
            foreach (var (box, act) in hits.ToList())
                if (box.Contains(x, y)) { Game1.playSound("smallSelect"); act(); return; }
        }

        public void Drag(int y)
        {
            if (!dragging) return;
            double frac = Math.Clamp((y - track.Y) / (double)Math.Max(1, track.Height), 0, 1);
            scroll = (int)(frac * MaxScroll);
        }

        public void EndDrag() => dragging = false;
        public void Wheel(int delta) => scroll = Math.Clamp(scroll + (delta > 0 ? -RowH : RowH), 0, MaxScroll);

        private List<(StorageNode node, int depth)> Rows()
        {
            var rows = new List<(StorageNode, int)>();
            void Walk(StorageNode n, int d)
            {
                rows.Add((n, d));
                if (!n.IsLeaf && n.Open) foreach (var k in n.Kids) Walk(k, d + 1);
            }
            foreach (var n in StorageTree) Walk(n, 0);
            return rows;
        }

        /// <summary>0 = nothing allowed, 1 = some, 2 = everything allowed.</summary>
        private static int State(StorageNode n, HashSet<string> blocked)
        {
            var ids = LeafIds(n).ToList();
            int open = ids.Count(i => !blocked.Contains(i));
            return open == 0 ? 0 : open == ids.Count ? 2 : 1;
        }

        private void Toggle(StorageNode n)
        {
            var blocked = BlockedLeaves(chest);
            var ids = LeafIds(n).ToList();
            if (State(n, blocked) == 2) foreach (var i in ids) blocked.Add(i);
            else foreach (var i in ids) blocked.Remove(i);
            SaveBlocked(chest, blocked);
        }

        public void Draw(SpriteBatch b)
        {
            hits.Clear();
            if (!Open)
            {
                Btn(b, tab, "Storage >", () => SetOpen(true));
                return;
            }
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Color.White);
            Btn(b, new Rectangle(Bounds.Right - 46, Bounds.Y + 2, 40, 26), "<", () => SetOpen(false));

            int x = Bounds.X + 30, y = Bounds.Y + 34, w = Bounds.Width - 60;
            Btn(b, new Rectangle(x, y, w, 44), $"Priority: {PriorityNames[ChestPriority(chest)]}", () =>
                chest.modData[PriorityKey] = ((ChestPriority(chest) + 1) % PriorityNames.Length).ToString());
            y += 52;
            Btn(b, new Rectangle(x, y, w / 2 - 6, 40), "Clear all", () => SaveBlocked(chest, StorageTree.SelectMany(LeafIds).ToHashSet()));
            Btn(b, new Rectangle(x + w / 2 + 6, y, w / 2 - 6, 40), "Allow all", () => SaveBlocked(chest, new HashSet<string>()));
            y += 50;

            list = new Rectangle(x, y, w - 30, Bounds.Bottom - y - 54);
            var blocked = BlockedLeaves(chest);
            var rows = Rows();
            scroll = Math.Clamp(scroll, 0, MaxScroll);
            for (int i = scroll / RowH; i < rows.Count; i++)
            {
                int ry = list.Y + i * RowH - scroll;
                if (ry + RowH > list.Bottom + 2) break;
                var (n, depth) = rows[i];
                int rx = list.X + depth * 24;
                if (!n.IsLeaf)
                {
                    Utility.drawTextWithShadow(b, n.Open ? "v" : ">", Game1.smallFont, new Vector2(rx + 6, ry + 6), Color.SaddleBrown);
                    var node = n;
                    hits.Add((new Rectangle(rx, ry, list.Right - rx - 60, RowH), () => node.Open = !node.Open));
                }
                Label(b, n.Name, new Vector2(rx + 30, ry + 6), list.Right - 64 - (rx + 30), Game1.textColor);

                int st = State(n, blocked);
                var cb = new Rectangle(list.Right - 44, ry + 5, 28, 28);
                b.Draw(Game1.mouseCursors, new Vector2(cb.X, cb.Y), st == 2 ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0.9f);
                if (st == 1) b.Draw(Game1.staminaRect, new Rectangle(cb.X + 6, cb.Y + 11, 16, 6), Color.DarkOrange);
                var toggled = n;
                hits.Add((new Rectangle(list.Right - 60, ry, 60, RowH), () => Toggle(toggled)));
            }

            track = new Rectangle(list.Right + 4, list.Y, 22, list.Height);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), track.X, track.Y, track.Width, track.Height, Color.White, 4f, false);
            if (Total > list.Height)
            {
                int thumbH = Math.Max(30, track.Height * list.Height / Total);
                int thumbY = track.Y + (track.Height - thumbH) * scroll / Math.Max(1, Total - list.Height);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(435, 463, 6, 10), track.X, thumbY, track.Width, thumbH, Color.White, 4f, false);
            }
            Label(b, "Hands only store items in chests that allow them.", new Vector2(x, Bounds.Bottom - 40), w, Color.Gray);
        }

        private void Btn(SpriteBatch b, Rectangle r, string label, Action act)
        {
            bool hover = r.Contains(Game1.getMouseX(), Game1.getMouseY());
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, hover ? Color.Wheat : Color.White, 4f, false);
            var size = Game1.smallFont.MeasureString(label);
            float scale = Math.Min(1f, (r.Width - 12) / size.X);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(r.X + (r.Width - size.X * scale) / 2, r.Y + (r.Height - size.Y * scale) / 2 + 2), Game1.textColor, scale);
            hits.Add((r, act));
        }

        private static void Label(SpriteBatch b, string text, Vector2 pos, int maxWidth, Color color)
        {
            var size = Game1.smallFont.MeasureString(text);
            float scale = Math.Min(1f, Math.Max(40, maxWidth) / size.X);
            Utility.drawTextWithShadow(b, text, Game1.smallFont, pos, color, scale);
        }
    }
}
