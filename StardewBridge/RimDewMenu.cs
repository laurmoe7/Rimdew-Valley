using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Menus;

namespace StardewBridge;

/// <summary>In-game control panel (F8). Immediate-mode: every frame draw() rebuilds the clickable regions.</summary>
public partial class RimDewMenu : IClickableMenu
{
    private static readonly string[] Tabs = { "Villagers", "Farmhands", "Colony", "Research", "Health", "Items", "Test" };
    private static readonly string[] Tasks = { "water", "harvest", "animals" };
    private static readonly Dictionary<string, string> TaskLabel = new() { ["water"] = "Water", ["harvest"] = "Harvest", ["animals"] = "Animals" };

    private readonly ModEntry mod;
    private readonly List<(Rectangle rect, Action click)> hits = new();
    private int tab;
    private string? moodPopup;
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
        height = Math.Min(960, Game1.uiViewport.Height - 40);
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
        if (moodPopup == null && sbTrack != Rectangle.Empty && new Rectangle(sbTrack.X - 6, sbTrack.Y, sbTrack.Width + 12, sbTrack.Height).Contains(x, y))
        {
            sbDrag = true;
            DragScrollbar(y);
            return;
        }
        if (searchBox != null && tab == 5)
        {
            bool inside = searchRect.Contains(x, y);
            searchBox.Selected = inside;
        }
        foreach (var (rect, click) in hits.ToList())
            if (rect.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                click();
                return;
            }
    }

    // Tabs 5 (Items) and 6 (Test) scroll in pixels; the list tabs scroll by row.
    private bool PixelScroll => tab >= 5;

    public override void receiveScrollWheelAction(int direction)
    {
        int step = PixelScroll ? (tab == 5 ? 36 : 60) : 1;
        scroll = Math.Max(0, scroll + (direction > 0 ? -step : step));
    }

    private Rectangle sbTrack;
    private int sbTotal, sbVisible;
    private bool sbDrag;

    /// <summary>Vertical scrollbar. <paramref name="total"/> and <paramref name="visible"/> are in the tab's scroll unit (rows or pixels).</summary>
    private void DrawScrollbar(SpriteBatch b, Rectangle track, int total, int visible)
    {
        sbTrack = Rectangle.Empty;
        if (total <= visible) { scroll = 0; return; }
        scroll = Math.Clamp(scroll, 0, total - visible);
        sbTrack = track; sbTotal = total; sbVisible = visible;
        b.Draw(Game1.staminaRect, track, Color.Black * 0.25f);
        int thumbH = Math.Max(36, track.Height * visible / total);
        int thumbY = track.Y + (track.Height - thumbH) * scroll / (total - visible);
        drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), track.X, thumbY, track.Width, thumbH, sbDrag ? Color.Wheat : Color.White, 2f, false);
    }

    private void DragScrollbar(int y)
    {
        int thumbH = Math.Max(36, sbTrack.Height * sbVisible / sbTotal);
        double frac = Math.Clamp((y - sbTrack.Y - thumbH / 2.0) / Math.Max(1, sbTrack.Height - thumbH), 0, 1);
        scroll = (int)Math.Round(frac * (sbTotal - sbVisible));
    }

    public override void leftClickHeld(int x, int y) { if (sbDrag) DragScrollbar(y); else base.leftClickHeld(x, y); }
    public override void releaseLeftClick(int x, int y) { sbDrag = false; base.releaseLeftClick(x, y); }

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
        float scale = Math.Min(1f, (r.Width - 14) / size.X);   // shrink long labels so they stay inside the button
        Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(r.X + (r.Width - size.X * scale) / 2, r.Y + (r.Height - size.Y * scale) / 2 + 2), Game1.textColor, scale);
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
        sbTrack = Rectangle.Empty;
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
            var r = new Rectangle(tx, yPositionOnScreen + 90, 124, 48);
            Button(b, r, Tabs[i], () => { tab = idx; scroll = 0; if (searchBox != null) searchBox.Selected = false; if (idx == 1) mod.RefreshRimWorld(); }, active: tab == i);
            tx += 130;
        }

        switch (tab)
        {
            case 0: DrawVillagers(b); break;
            case 1: DrawFarmhands(b); break;
            case 2: DrawColony(b); break;
            case 3: DrawResearch(b); break;
            case 4: DrawHealth(b); break;
            case 5: DrawItems(b); break;
            case 6: DrawTest(b); break;
        }

        if (moodPopup != null) DrawMoodPopup(b);

        upperRightCloseButton?.draw(b);
        drawMouse(b);
    }

    /// <summary>Why a farmhand feels the way they do: every thought RimWorld says is affecting their mood.</summary>
    private void DrawMoodPopup(SpriteBatch b)
    {
        hits.Clear();                       // everything underneath goes inert while the popup is open
        var info = mod.LastHands.FirstOrDefault(h => (string?)h["name"] == moodPopup) as JObject;
        int w = 640, ph = Math.Min(height - 60, 560);
        var r = new Rectangle(xPositionOnScreen + (width - w) / 2, yPositionOnScreen + (height - ph) / 2, w, ph);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);
        drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), r.X, r.Y, r.Width, r.Height, Color.White);

        int x = r.X + 38, y = r.Y + 32;
        if (info == null)
        {
            Text(b, $"{moodPopup}: no data yet. Open the Farmhands tab with RimWorld running.", x, y);
        }
        else
        {
            double mood = (double)info["mood"]!;
            Text(b, $"{moodPopup}: mood {mood:0.00} ({(string)info["status"]!})", x, y, big: true);
            y += 40;
            string traitLine = mod.TraitSummary(moodPopup);
            Text(b, traitLine == "" ? "No traits" : "Traits: " + Fit(traitLine, 480), x, y, Color.DarkSlateBlue);
            y += 36;
            var thoughts = info["thoughts"] as JArray ?? new JArray();
            if (thoughts.Count == 0) Text(b, "RimWorld reports no mood factors yet.", x, y, Color.Gray);
            foreach (var t in thoughts)
            {
                double off = (double)t["offset"]!;
                Text(b, Fit((string)t["label"]!, 400), x, y);
                Text(b, $"{off:+0.0;-0.0}", r.Right - 120, y, off >= 0 ? Color.ForestGreen : Color.Firebrick);
                y += 28;
                if (y > r.Bottom - 150) break;
            }
            y = r.Bottom - 130;
            Text(b, "Needs", x, y);
            if (info["needs"] is JObject needs)
            {
                string[] ks = { "food", "rest", "joy" };
                for (int i = 0; i < ks.Length; i++)
                {
                    double v = (double?)needs[ks[i]] ?? 0;
                    Text(b, char.ToUpper(ks[i][0]) + ks[i][1..], x + 90 + i * 170, y);
                    Bar(b, new Rectangle(x + 90 + i * 170, y + 28, 140, 14), v, MoodColor(v));
                }
            }
        }
        Text(b, "Click anywhere to close", r.X + 38, r.Bottom - 40, Color.Gray);
        hits.Add((new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), () => moodPopup = null));
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
        Text(b, $"Villagers ({list.Count}), lowest mood first", c.X, ActionY + 8);
        DrawScrollbar(b, new Rectangle(c.X + c.Width + 8, c.Y, 18, rows * 34), list.Count, rows);
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
        DrawScrollbar(b, new Rectangle(c.X + c.Width + 8, c.Y, 18, rows * 84), hands.Count, rows);
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
            Button(b, new Rectangle(c.X + c.Width - 208, ry + 34, 94, 38), "Mood", () => moodPopup = nm);
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
        // rows of 58 px; one row at the bottom is kept for the hint line
        var projects = mod.lastProjects;
        int rows = Math.Max(1, c.Height / 58 - 1);
        scroll = Math.Min(scroll, Math.Max(0, projects.Count - rows));
        DrawScrollbar(b, new Rectangle(c.X + c.Width + 8, c.Y, 18, rows * 58), projects.Count, rows);
        int row = 0;
        for (int i = scroll; i < projects.Count && row < rows; i++)
        {
            var p = projects[i];
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
        Button(b, new Rectangle(c.X + 480, ActionY, 340, 40), $"Heal all wounds ({mod.Config.HealKey})", mod.HealAllWounds);
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

    private void DrawColony(SpriteBatch b)
    {
        var c = Content;
        double outdoor = mod.OutdoorTemp();
        Text(b, $"Outdoors: {outdoor:0} C", c.X, c.Y - 4, outdoor < 5 || outdoor > 32 ? Color.Firebrick : Color.ForestGreen);
        var f = mod.FoodSummary();
        Text(b, $"Food in chests: {f.meals} meals, {f.snacks} snacks, {f.raw} raw", c.X + 260, c.Y - 4, f.meals + f.snacks + f.raw == 0 ? Color.Firebrick : Game1.textColor);
        var st = mod.StorageSummary();
        Text(b, $"Nutrient paste loaded: {st.paste}    Freezers: {(st.freezers == 0 ? "none" : st.freezers.ToString())}    Microwaves: {(st.microwaves == 0 ? "none" : st.microwaves.ToString())}", c.X, c.Y + 28);
        var hands = mod.Data.Hands;
        if (hands.Count == 0) { Text(b, "No farmhands hired.", c.X, c.Y + 50, Color.Gray); return; }
        Text(b, "Hunger", c.X + 230, c.Y + 62, Color.Gray);
        Text(b, "Temperature", c.X + 520, c.Y + 62, Color.Gray);
        for (int i = 0; i < hands.Count; i++)
        {
            int ry = c.Y + 96 + i * 40;
            string nm = hands[i];
            Text(b, nm, c.X, ry);
            double hunger = mod.HungerOf(nm);
            Bar(b, new Rectangle(c.X + 230, ry + 4, 220, 22), hunger / 100.0, hunger >= 70 ? Color.Firebrick : hunger >= 50 ? Color.Goldenrod : Color.ForestGreen);
            Text(b, $"{hunger:0}", c.X + 460, ry);
            var (temp, status) = mod.HandClimate(nm);
            Text(b, $"{temp:0} C {(status == "" ? "" : "(" + status + ")")}", c.X + 520, ry, status == "" ? Game1.textColor : Color.Firebrick);
        }
    }

    private void DrawTest(SpriteBatch b)
    {
        var c = Content;

        var sections = new (string title, (string label, Action act)[] buttons)[]
        {
            ("Settings", new (string, Action)[] { ($"Free crafting: {(mod.Config.FreeCrafting ? "ON" : "off")}", mod.ToggleFreeCrafting) }),
            ("Farm", new (string, Action)[] { ("Clear debris", mod.ClearDebris), ("Plant test crops", mod.PlantTestPatch),
                ("Grow crops now", mod.GrowAllCrops), ("Give test kit", mod.GiveTestKit),
                ("Place bunkhouse", mod.PlaceBunkhouse), ("Power kit", mod.GivePowerKit), ("Defense kit", mod.GiveDefenseKit) }),
            ("Player", new (string, Action)[] { ("Max tools", mod.MaxTools), ("36-slot backpack", mod.MaxBackpack), ("Max skills", mod.MaxSkills),
                ("Full heal+energy", mod.FullRestore), ("Heal all wounds", mod.HealAllWounds), ("+50,000g", () => mod.AddMoney(50000)), ("Max friendship", mod.MaxFriendship),
                ("Clear monsters", mod.ClearMonsters), ("Time: 6pm", () => mod.SetTime(1800)) }),
            ("Monsters", new (string, Action)[] { ("Slimes x3", () => mod.SpawnMonsters(3, "slime")), ("Bats x3", () => mod.SpawnMonsters(3, "bat")),
                ("Brutes x2", () => mod.SpawnMonsters(2, "brute")), ("Skeletons x2", () => mod.SpawnMonsters(2, "skeleton")),
                ("Sappers x4", () => mod.SpawnMonsters(4, "sapper")), ("Drop pod x4", () => mod.SpawnMonsters(4, "podbrute")),
                ("Mech cluster x3", () => mod.SpawnMonsters(3, "golem")), ("Manhunters x4", () => mod.SpawnMonsters(4, "manhunter")),
                ("Weapons kit", mod.GiveWeaponsKit) }),
            ("RimWorld guns", new (string, Action)[] { ("All guns", mod.GiveAllGuns),
                ("Revolver", () => mod.GiveGunById("Revolver")), ("Assault rifle", () => mod.GiveGunById("AssaultRifle")),
                ("Shotgun", () => mod.GiveGunById("Shotgun")), ("Sniper rifle", () => mod.GiveGunById("SniperRifle")),
                ("Minigun", () => mod.GiveGunById("Minigun")) }),
            ("Injury", new (string, Action)[] { ("Hurt: blunt", () => mod.HurtMe("blunt", 15)), ("Hurt: bite", () => mod.HurtMe("bite", 15)),
                ("Hurt: cut", () => mod.HurtMe("cut", 15)), ("Tend wounds", mod.Tend) }),
            ("RimWorld", new (string, Action)[] { ("Run night now", mod.RunNightNow), ("Refresh data", mod.RefreshRimWorld),
                ("Cheer everyone", () => mod.SendMoodEventToAll("festival", "birthday")), ("Upset everyone", () => mod.SendMoodEventToAll("gift_hated", "gift_hated")),
                ("+200 research", () => mod.AddResearchPoints(200)) }),
            ("Furniture", new (string, Action)[] {
                ("Bed", () => mod.GiveFurniture("bed")), ("Double bed", () => mod.GiveFurniture("doublebed")), ("Chair", () => mod.GiveFurniture("chair")),
                ("Table", () => mod.GiveFurniture("table")), ("Bookcase", () => mod.GiveFurniture("bookcase")), ("Lamp", () => mod.GiveFurniture("lamp")),
                ("TV", () => mod.GiveFurniture("tv")), ("Free catalogue", mod.OpenFurnitureCatalogue) }),
            ("Storyteller", new (string, Action)[] { ("Raid", () => mod.FireRimWorldIncident("RaidEnemy")),
                ("Infestation", () => mod.FireRimWorldIncident("Infestation")), ("Toxic fallout", () => mod.FireRimWorldIncident("ToxicFallout")),
                ("Cold snap", () => mod.FireRimWorldIncident("ColdSnap")) }),
        };

        const int cols = 5, gap = 8, bh = 34;
        int bw = (c.Width - gap * (cols - 1)) / cols;
        int viewTop = ActionY + 4, viewBottom = c.Y + c.Height;
        int total = 0;
        foreach (var (_, buttons) in sections) total += 32 + (buttons.Length + cols - 1) / cols * (bh + 4) + 4;
        DrawScrollbar(b, new Rectangle(c.X + c.Width + 8, viewTop, 18, viewBottom - viewTop), total, viewBottom - viewTop);

        int y = viewTop - scroll;
        foreach (var (title, buttons) in sections)
        {
            if (y >= viewTop && y + 28 <= viewBottom) Text(b, title, c.X, y);
            y += 32;
            for (int i = 0; i < buttons.Length; i++)
            {
                int col = i % cols, row = i / cols;
                var (label, act) = buttons[i];
                var r = new Rectangle(c.X + col * (bw + gap), y + row * (bh + 4), bw, bh);
                if (r.Y >= viewTop && r.Bottom <= viewBottom) Button(b, r, label, act);
            }
            y += (buttons.Length + cols - 1) / cols * (bh + 4) + 4;
        }
    }
}
