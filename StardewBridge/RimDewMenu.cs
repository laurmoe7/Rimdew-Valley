using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Menus;

namespace StardewBridge;

/// <summary>In-game control panel (F8). Immediate-mode: every frame draw() rebuilds the clickable regions.</summary>
public class RimDewMenu : IClickableMenu
{
    private static readonly string[] Tabs = { "Villagers", "Farmhands", "Research", "Health", "Test" };
    private static readonly string[] Tasks = { "water", "harvest", "animals" };
    private static readonly Dictionary<string, string> TaskLabel = new() { ["water"] = "Water", ["harvest"] = "Harvest", ["animals"] = "Animals" };

    private readonly ModEntry mod;
    private readonly List<(Rectangle rect, Action click)> hits = new();
    private int tab;
    private int scroll;

    public RimDewMenu(ModEntry mod, int startTab = 0)
        : base(0, 0, 1000, 720, showUpperRightCloseButton: true)
    {
        this.mod = mod;
        tab = startTab;
        if (tab == 1) mod.RefreshRimWorld();
        Reposition();
    }

    private void Reposition()
    {
        width = Math.Min(1000, Game1.uiViewport.Width - 64);
        height = Math.Min(880, Game1.uiViewport.Height - 40);
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
        initializeUpperRightCloseButton();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => Reposition();

    // ---------- input ----------
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
        {
            exitThisMenu();
            return;
        }
        foreach (var (rect, click) in hits.ToList())
            if (rect.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                click();
                return;
            }
    }

    public override void receiveScrollWheelAction(int direction) => scroll = Math.Max(0, scroll + (direction > 0 ? -1 : 1));

    // ---------- drawing helpers ----------
    // Layout, top to bottom: title, tab row (+90), action row (+150), content (+210).
    private int ActionY => yPositionOnScreen + 150;
    private Rectangle Content => new(xPositionOnScreen + 40, yPositionOnScreen + 210, width - 80, height - 250);

    private void Text(SpriteBatch b, string text, int x, int y, Color? color = null, bool big = false) =>
        Utility.drawTextWithShadow(b, text, big ? Game1.dialogueFont : Game1.smallFont, new Vector2(x, y), color ?? Game1.textColor);

    private static string Fit(string s, int maxWidth)
    {
        if (Game1.smallFont.MeasureString(s).X <= maxWidth) return s;
        while (s.Length > 1 && Game1.smallFont.MeasureString(s + "...").X > maxWidth) s = s[..^1];
        return s + "...";
    }

    private void Button(SpriteBatch b, Rectangle r, string label, Action click, bool active = false)
    {
        bool hover = r.Contains(Game1.getMouseX(), Game1.getMouseY());
        drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height,
            active ? Color.LightGreen : hover ? Color.Wheat : Color.White, 4f, false);
        var size = Game1.smallFont.MeasureString(label);
        Text(b, label, r.X + (int)((r.Width - size.X) / 2), r.Y + (int)((r.Height - size.Y) / 2) + 2);
        hits.Add((r, click));
    }

    private static void Bar(SpriteBatch b, Rectangle r, double frac, Color fill)
    {
        b.Draw(Game1.staminaRect, r, Color.Black * 0.35f);
        b.Draw(Game1.staminaRect, new Rectangle(r.X, r.Y, (int)(r.Width * Math.Clamp(frac, 0, 1)), r.Height), fill);
    }

    private static Color MoodColor(double m) => m >= 0.65 ? Color.ForestGreen : m <= 0.25 ? Color.Firebrick : Color.Goldenrod;

    // ---------- main draw ----------
    public override void draw(SpriteBatch b)
    {
        hits.Clear();
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

        int x = xPositionOnScreen + 40, y = yPositionOnScreen + 36;
        Text(b, "RimDew Valley", x, y, big: true);
        string status = mod.Hub.Connected ? (mod.RimWorldOnline ? "RimWorld: online" : "Hub: connected, RimWorld offline") : "Hub: offline";
        var statusColor = mod.Hub.Connected && mod.RimWorldOnline ? Color.ForestGreen : Color.Firebrick;
        Text(b, status, xPositionOnScreen + width - 70 - (int)Game1.smallFont.MeasureString(status).X, y + 12, statusColor);

        // tabs
        int tx = x;
        for (int i = 0; i < Tabs.Length; i++)
        {
            int idx = i;
            var r = new Rectangle(tx, yPositionOnScreen + 90, 160, 48);
            Button(b, r, Tabs[i], () => { tab = idx; scroll = 0; if (idx == 1) mod.RefreshRimWorld(); }, active: tab == i);
            tx += 170;
        }

        switch (tab)
        {
            case 0: DrawVillagers(b); break;
            case 1: DrawFarmhands(b); break;
            case 2: DrawResearch(b); break;
            case 3: DrawHealth(b); break;
            case 4: DrawTest(b); break;
        }

        upperRightCloseButton?.draw(b);
        drawMouse(b);
    }

    // ---------- tabs ----------
    private void DrawVillagers(SpriteBatch b)
    {
        var c = Content;
        if (mod.npcMood.Count == 0)
        {
            Text(b, "No mood data yet. Sleep a night with RimWorld running.", c.X, c.Y);
            return;
        }
        var list = mod.npcMood.OrderBy(kv => kv.Value).ToList();
        int rows = Math.Max(1, c.Height / 34);
        scroll = Math.Min(scroll, Math.Max(0, list.Count - rows));
        Text(b, $"Villagers ({list.Count}), lowest mood first. Scroll for more.", c.X, ActionY + 8);
        for (int i = 0; i < rows && i + scroll < list.Count; i++)
        {
            var (name, mood) = list[i + scroll];
            int ry = c.Y + i * 34;
            Text(b, name, c.X, ry);
            Bar(b, new Rectangle(c.X + 180, ry + 4, 300, 20), mood, MoodColor(mood));
            Text(b, $"{mood:0.00}", c.X + 495, ry);
            string label = mod.NpcLabel.TryGetValue(name, out var l) && l != "" ? l : mood >= 0.65 ? "happy" : mood <= 0.25 ? "unhappy" : "okay";
            Text(b, Fit(label, c.Width - 570), c.X + 560, ry, mod.npcBroken.Contains(name) ? Color.Firebrick : Game1.textColor);
        }
    }

    private void DrawFarmhands(SpriteBatch b)
    {
        var c = Content;
        Button(b, new Rectangle(c.X, ActionY, 340, 40), $"Hire a farmhand ({ModEntry.DailyWage}g/day)", mod.HireNext);
        Text(b, $"{mod.Data.Hands.Count} hired", c.X + 360, ActionY + 8);

        if (mod.Data.Hands.Count == 0)
        {
            Text(b, "Hands water, harvest and feed animals overnight. Their skills and moods come from RimWorld.", c.X, c.Y + 10);
            Text(b, "Click a chore to cycle its priority: 1 first ... 4 last, 0 = off.", c.X, c.Y + 44);
            Text(b, "Build a Bunkhouse at Robin's carpenter shop for them to stay in (4 per house).", c.X, c.Y + 78);
            return;
        }

        int rows = Math.Max(1, c.Height / 84);
        var hands = mod.Data.Hands;
        scroll = Math.Min(scroll, Math.Max(0, hands.Count - rows));
        for (int i = 0; i < rows && i + scroll < hands.Count; i++)
        {
            string name = hands[i + scroll];
            int ry = c.Y + i * 84;
            var info = mod.LastHands.FirstOrDefault(h => (string?)h["name"] == name);
            Text(b, name, c.X, ry);
            if (info != null)
            {
                double mood = (double)info["mood"]!;
                Bar(b, new Rectangle(c.X + 110, ry + 4, 150, 20), mood, MoodColor(mood));
                Text(b, $"{(string)info["status"]!}  Plants {(int)info["plants"]!}  Animals {(int)info["animals"]!}  worked {(double)info["hours"]!}h", c.X + 275, ry);
            }
            else Text(b, "waiting for RimWorld (sleep a night)", c.X + 110, ry, Color.Gray);

            for (int t = 0; t < Tasks.Length; t++)
            {
                string task = Tasks[t];
                mod.Data.Priorities.TryGetValue(name + "|" + task, out int prio);
                if (!mod.Data.Priorities.ContainsKey(name + "|" + task)) prio = t + 1; // RimWorld default
                string n = name;
                Button(b, new Rectangle(c.X + 110 + t * 190, ry + 34, 180, 38), $"{TaskLabel[task]}: {(prio == 0 ? "off" : prio.ToString())}",
                    () => mod.SetPriority(n, task, (prio + 1) % 5), active: prio == 1);
            }
            string nm = name;
            Button(b, new Rectangle(c.X + c.Width - 110, ry + 34, 100, 38), "Fire", () => mod.FireHand(nm));
        }
    }

    private void DrawResearch(SpriteBatch b)
    {
        var c = Content;
        Button(b, new Rectangle(c.X, ActionY, 400, 40), $"Gate machine recipes: {(mod.Data.GateRecipes ? "ON" : "off")}", mod.ToggleGate, active: mod.Data.GateRecipes);
        Text(b, "1 research point per 25g earned", c.X + 420, ActionY + 8);

        if (mod.lastProjects.Count == 0)
        {
            Text(b, "No research data yet. Sleep a night with RimWorld running.", c.X, c.Y + 10);
            return;
        }
        int row = 0;
        foreach (var p in mod.lastProjects)
        {
            string id = (string)p["id"]!, label = (string)p["label"]!;
            double prog = (double)p["progress"]!, cost = (double)p["cost"]!;
            bool done = (bool)p["done"]!, target = mod.Data.ResearchTarget == id;
            int ry = c.Y + row++ * 58;
            var rowRect = new Rectangle(c.X, ry, c.Width, 52);
            if (!done) hits.Add((rowRect, () => mod.SetResearchTarget(id)));
            if (target) b.Draw(Game1.staminaRect, rowRect, Color.Gold * 0.25f);
            Text(b, (done ? "[done] " : target ? "[target] " : "") + char.ToUpper(label[0]) + label[1..], c.X + 8, ry + 4);
            Bar(b, new Rectangle(c.X + 8, ry + 30, c.Width - 220, 16), cost > 0 ? prog / cost : 0, done ? Color.ForestGreen : Color.SteelBlue);
            Text(b, done ? "unlocked" : $"{prog:0}/{cost:0}", c.X + c.Width - 190, ry + 24);
        }
        Text(b, "Click a project to make it the target; otherwise the cheapest unfinished one gets the points.", c.X, c.Y + row * 58 + 6, Color.Gray);
    }

    private void DrawHealth(SpriteBatch b)
    {
        var c = Content;
        var h = mod.LastHealth;
        Button(b, new Rectangle(c.X, ActionY, 460, 40), "Tend wounds (Muscle Remedy or Fiber)", mod.Tend);
        if (h == null)
        {
            Text(b, "No health data yet. Get hurt, or sleep a night, with RimWorld running.", c.X, c.Y + 10);
            return;
        }
        double pct = (double)h["health_pct"]!;
        Text(b, "Body health", c.X, c.Y);
        Bar(b, new Rectangle(c.X + 200, c.Y + 4, 400, 22), pct, MoodColor(pct));
        Text(b, $"{pct:P0}", c.X + 615, c.Y);
        string[] keys = { "moving", "manipulation", "consciousness" };
        string[] names = { "Mobility", "Hands", "Consciousness" };
        for (int i = 0; i < keys.Length; i++)
        {
            double v = (double)h[keys[i]]!;
            int ry = c.Y + 40 + i * 34;
            Text(b, names[i], c.X, ry);
            Bar(b, new Rectangle(c.X + 200, ry + 4, 400, 22), v, MoodColor(v));
            Text(b, $"{v:P0}", c.X + 615, ry);
        }
        Text(b, $"Pain {(double)h["pain"]!:P0}    Bleeding {(double)h["bleed"]!:0.00}/day", c.X, c.Y + 148);
        Text(b, "Injuries:", c.X, c.Y + 188);
        var injuries = ((JArray)h["injuries"]!).Select(x => (string)x!).ToList();
        if (injuries.Count == 0) Text(b, "None", c.X + 20, c.Y + 222, Color.ForestGreen);
        for (int i = 0; i < injuries.Count; i++) Text(b, "- " + Fit(injuries[i], c.Width - 40), c.X + 20, c.Y + 222 + i * 30);
    }

    private void DrawTest(SpriteBatch b)
    {
        var c = Content;
        Text(b, "Test tools. Nothing is saved until you sleep, so quit without sleeping to undo.", c.X, ActionY + 8, Color.Gray);

        var sections = new (string title, (string label, Action act)[] buttons)[]
        {
            ("Farm", new (string, Action)[] { ("Clear debris", mod.ClearDebris), ("Plant test crops", mod.PlantTestPatch),
                ("Grow crops now", mod.GrowAllCrops), ("Give test kit", mod.GiveTestKit),
                ("Free bunkhouse", mod.PlaceFreeBunkhouse) }),
            ("Monsters (local)", new (string, Action)[] { ("Slimes x3", () => mod.SpawnMonsters(3, "slime")), ("Bats x3", () => mod.SpawnMonsters(3, "bat")),
                ("Brutes x2", () => mod.SpawnMonsters(2, "brute")), ("Skeletons x2", () => mod.SpawnMonsters(2, "skeleton")),
                ("Weapons kit", mod.GiveWeaponsKit) }),
            ("RimWorld guns (hold left click to fire)", new (string, Action)[] { ("All guns", mod.GiveAllGuns),
                ("Revolver", () => mod.GiveGunById("Revolver")), ("Assault rifle", () => mod.GiveGunById("AssaultRifle")),
                ("Shotgun", () => mod.GiveGunById("Shotgun")), ("Sniper rifle", () => mod.GiveGunById("SniperRifle")),
                ("Minigun", () => mod.GiveGunById("Minigun")) }),
            ("Injury (-15 HP)", new (string, Action)[] { ("Hurt: blunt", () => mod.HurtMe("blunt", 15)), ("Hurt: bite", () => mod.HurtMe("bite", 15)),
                ("Hurt: cut", () => mod.HurtMe("cut", 15)), ("Tend wounds", mod.Tend) }),
            ("RimWorld", new (string, Action)[] { ("Run night now", mod.RunNightNow), ("Refresh data", mod.RefreshRimWorld),
                ("Cheer everyone", () => mod.SendMoodEventToAll("festival", "birthday")), ("Upset everyone", () => mod.SendMoodEventToAll("gift_hated", "gift_hated")),
                ("+200 research", () => mod.AddResearchPoints(200)) }),
            ("Storyteller (real incidents)", new (string, Action)[] { ("Raid", () => mod.FireRimWorldIncident("RaidEnemy")),
                ("Infestation", () => mod.FireRimWorldIncident("Infestation")), ("Toxic fallout", () => mod.FireRimWorldIncident("ToxicFallout")),
                ("Cold snap", () => mod.FireRimWorldIncident("ColdSnap")) }),
        };

        const int cols = 4, gap = 10, bh = 38;
        int bw = (c.Width - gap * (cols - 1)) / cols;
        int y = c.Y;
        foreach (var (title, buttons) in sections)
        {
            Text(b, title, c.X, y);
            y += 26;
            for (int i = 0; i < buttons.Length; i++)
            {
                int col = i % cols, row = i / cols;
                var (label, act) = buttons[i];
                Button(b, new Rectangle(c.X + col * (bw + gap), y + row * (bh + 4), bw, bh), label, act);
            }
            y += (buttons.Length + cols - 1) / cols * (bh + 4) + 6;
        }
    }
}
