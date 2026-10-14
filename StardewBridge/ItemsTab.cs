using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewBridge;

/// <summary>Items tab: spawn any item into your inventory. Quick buttons for common materials, plus a search over every item in the game.</summary>
public partial class RimDewMenu
{
    private TextBox? searchBox;
    private Rectangle searchRect;
    private int itemQty = 1;
    private string lastQuery = "\u0001";
    private List<(string qid, string name)> results = new();

    private record CatalogueEntry(string Qid, string Name, string Lower);
    private static List<CatalogueEntry>? catalogue;

    private static readonly (string label, string qid)[] QuickItems =
    {
        ("Iron bar", "(O)335"), ("Copper bar", "(O)334"), ("Gold bar", "(O)336"), ("Iridium bar", "(O)337"), ("Refined quartz", "(O)338"),
        ("Wood", "(O)388"), ("Stone", "(O)390"), ("Coal", "(O)382"), ("Copper ore", "(O)378"), ("Iron ore", "(O)380"),
        ("Fiber", "(O)771"), ("Clay", "(O)330"), ("Hardwood", "(O)709"), ("Cloth", "(O)428"), ("Battery pack", "(O)787"),
        ("Cherry bomb", "(O)286"), ("Sap", "(O)92"), ("Gold ore", "(O)384"), ("Parsnip", "(O)24"), ("Egg", "(O)176"),
    };

    private static readonly int[] Quantities = { 1, 10, 50, 999 };

    private static List<CatalogueEntry> Catalogue()
    {
        if (catalogue != null) return catalogue;
        var list = new List<CatalogueEntry>();
        foreach (var type in ItemRegistry.ItemTypes)
            foreach (var id in type.GetAllIds())
            {
                string qid = type.Identifier + id;
                var d = ItemRegistry.GetData(qid);
                if (d == null || d.IsErrorItem || string.IsNullOrWhiteSpace(d.DisplayName)) continue;
                list.Add(new CatalogueEntry(qid, d.DisplayName, (d.DisplayName + " " + id).ToLowerInvariant()));
            }
        return catalogue = list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void EnsureSearchBox()
    {
        if (searchBox != null) return;
        searchBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor) { Width = 360, Height = 48, Text = "" };
        searchBox.Selected = true;
    }

    private void UpdateResults()
    {
        string q = (searchBox?.Text ?? "").Trim().ToLowerInvariant();
        if (q == lastQuery) return;
        lastQuery = q;
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        results = Catalogue()
            .Where(e => tokens.All(t => e.Lower.Contains(t)))
            .OrderByDescending(e => tokens.Length > 0 && e.Name.StartsWith(tokens[0], StringComparison.OrdinalIgnoreCase))
            .Take(500)
            .Select(e => (e.Qid, e.Name))
            .ToList();
        scroll = 0;
    }

    internal static void SpawnItem(string qid, int qty)
    {
        var probe = ItemRegistry.Create(qid, 1, allowNull: true);
        if (probe == null) return;
        int n = Math.Max(1, Math.Min(qty, probe.maximumStackSize()));
        var item = ItemRegistry.Create(qid, n);
        if (!Game1.player.addItemToInventoryBool(item)) Game1.createItemDebris(item, Game1.player.getStandingPosition(), 2);
        Game1.playSound("coin");
        Game1.addHUDMessage(new HUDMessage($"+{n} {item.DisplayName}", HUDMessage.achievement_type));
    }

    private void DrawItems(SpriteBatch b)
    {
        var c = Content;
        EnsureSearchBox();
        UpdateResults();

        // search box + quantity picker
        searchRect = new Rectangle(c.X, ActionY, 360, 48);
        searchBox!.X = searchRect.X; searchBox.Y = searchRect.Y;
        searchBox.Draw(b, false);
        if (searchBox.Text.Length == 0 && !searchBox.Selected) Text(b, "search all items...", searchRect.X + 22, searchRect.Y + 12, Color.Gray);
        for (int i = 0; i < Quantities.Length; i++)
        {
            int q = Quantities[i];
            Button(b, new Rectangle(c.X + 380 + i * 96, ActionY + 2, 88, 44), "x" + q, () => itemQty = q, active: itemQty == q);
        }
        Text(b, "click to add", c.X + 380 + Quantities.Length * 96 + 4, ActionY + 12, Color.Gray);

        // quick picks
        const int cols = 5, gap = 8, bh = 36;
        int bw = (c.Width - gap * (cols - 1)) / cols;
        int qy = ActionY + 62;
        for (int i = 0; i < QuickItems.Length; i++)
        {
            var (label, qid) = QuickItems[i];
            Button(b, new Rectangle(c.X + i % cols * (bw + gap), qy + i / cols * (bh + 4), bw, bh), label, () => SpawnItem(qid, itemQty));
        }

        // search results
        int listTop = qy + ((QuickItems.Length + cols - 1) / cols) * (bh + 4) + 34, viewBottom = c.Y + c.Height;
        Text(b, $"{results.Count}{(results.Count >= 500 ? "+" : "")} matches", c.X, listTop - 30, Color.Gray);
        const int rowH = 36;
        int rows = Math.Max(1, (viewBottom - listTop) / rowH);
        DrawScrollbar(b, new Rectangle(c.X + c.Width + 8, listTop, 18, rows * rowH), results.Count * rowH, rows * rowH);
        int first = scroll / rowH;
        for (int i = 0; i < rows + 1 && first + i < results.Count; i++)
        {
            var (qid, name) = results[first + i];
            var r = new Rectangle(c.X, listTop + i * rowH - scroll % rowH, c.Width, rowH - 2);
            if (r.Y < listTop - 1 || r.Bottom > viewBottom) continue;
            bool hover = r.Contains(Game1.getMouseX(), Game1.getMouseY());
            b.Draw(Game1.staminaRect, r, hover ? Color.Wheat * 0.9f : Color.White * 0.25f);
            var d = ItemRegistry.GetDataOrErrorItem(qid);
            var src = d.GetSourceRect();
            float s = 28f / Math.Max(src.Width, src.Height);
            b.Draw(d.GetTexture(), new Rectangle(r.X + 6, r.Y + 2, (int)(src.Width * s), (int)(src.Height * s)), src, Color.White);
            Text(b, Fit(name, c.Width - 260), r.X + 44, r.Y + 3);
            string idText = qid;
            Text(b, idText, r.Right - 12 - (int)Game1.smallFont.MeasureString(idText).X, r.Y + 3, Color.Gray);
            string id = qid;
            hits.Add((r, () => SpawnItem(id, itemQty)));
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        // While typing in the search box, letters (including the menu key, E) must not close the menu.
        if (tab == 5 && searchBox?.Selected == true)
        {
            if (key == Keys.Escape) exitThisMenu();
            return;
        }
        base.receiveKeyPress(key);
    }

    protected override void cleanupBeforeExit()
    {
        if (searchBox != null) searchBox.Selected = false;
        base.cleanupBeforeExit();
    }
}
