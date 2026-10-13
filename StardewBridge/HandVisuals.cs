using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;

namespace StardewBridge;

/// <summary>Farmhands you can see. They are drawn by this mod (never added to the game's character lists, so
/// nothing about them is written to your save). Each morning, RimWorld says how many chores each hand did and
/// they walk out and do them one by one.</summary>
public partial class ModEntry
{
    internal class HandJob
    {
        public string Kind = "";           // water | harvest | animals
        public Vector2 Tile;               // crop tile (water/harvest)
        public FarmAnimal? Animal;         // animals
    }

    internal class HandActor
    {
        public string Name = "";
        public Texture2D? Sheet;
        public Color Tint = Color.White;
        public Vector2 Pos, Home;
        public int Dir;                    // 0 down, 1 right, 2 up, 3 left (NPC sheet rows)
        public bool Moving;
        public HandJob? Current;
        public int WorkTimer;
        public readonly Queue<HandJob> Jobs = new();
        public string Activity = "resting";
    }

    private const float HandSpeed = 3f;
    private const int WorkTicks = 24;
    private readonly Dictionary<string, HandActor> actors = new();
    private readonly HashSet<Vector2> reservedTiles = new();
    private readonly HashSet<long> reservedAnimals = new();
    private int doneWater, doneHarvest, doneFed;
    private bool batchActive;

    private static readonly Color[] Tints =
    {
        Color.White, new(255, 235, 215), new(225, 240, 255), new(235, 255, 225), new(255, 225, 235), new(245, 245, 205)
    };

    // ---------- lifecycle ----------
    private Vector2 HomeFor(int i)
    {
        var door = Game1.getFarm().GetMainFarmHouseEntry();
        return new Vector2((door.X - 1 + i % 6) * 64, (door.Y + 2 + i / 6) * 64);
    }

    /// <summary>Make the on-screen hands match the hired list. reset = send everyone home and drop pending chores.</summary>
    private void SyncHands(bool reset)
    {
        foreach (var gone in actors.Keys.Where(k => !data.Hands.Contains(k)).ToList()) actors.Remove(gone);
        for (int i = 0; i < data.Hands.Count; i++)
        {
            string name = data.Hands[i];
            if (!actors.TryGetValue(name, out var a))
            {
                a = new HandActor { Name = name, Tint = Tints[i % Tints.Length] };
                a.Sheet = LoadHandSheet(i);
                a.Home = a.Pos = HomeFor(i);
                actors[name] = a;
            }
            a.Home = HomeFor(i);
            if (reset)
            {
                a.Pos = a.Home;
                a.Jobs.Clear();
                a.Current = null;
                a.Activity = "resting";
            }
        }
    }

    private Texture2D? LoadHandSheet(int i)
    {
        var names = Config.HandSprites;
        if (names.Count == 0) return null;
        try { return Helper.GameContent.Load<Texture2D>("Characters/" + names[i % names.Count]); }
        catch (Exception e) { Monitor.Log($"Could not load hand sprite '{names[i % names.Count]}': {e.Message}", LogLevel.Warn); return null; }
    }

    // ---------- assigning chores (from RimWorld's per-hand report) ----------
    private List<T> PickNearest<T>(List<T> pool, int n, Vector2 startPx, Func<T, Vector2> posPx)
    {
        var chosen = new List<T>();
        var cur = startPx;
        while (chosen.Count < n && pool.Count > 0)
        {
            var next = pool.OrderBy(x => Vector2.DistanceSquared(posPx(x), cur)).First();
            pool.Remove(next);
            chosen.Add(next);
            cur = posPx(next);
        }
        return chosen;
    }

    private void OnWorkDone(JObject msg)
    {
        LastHands = (JArray)msg["hands"]!;
        if (data.Hands.Count == 0) return;
        SyncHands(false);
        var farm = Game1.getFarm();
        reservedTiles.Clear();
        reservedAnimals.Clear();
        doneWater = doneHarvest = doneFed = 0;
        int totalJobs = 0;

        var sprinklered = SprinklerTiles(farm);
        var dry = FarmCrops(farm).Where(c => !sprinklered.Contains(c.tile) && c.dirt.state.Value != HoeDirt.watered).ToList();
        var ready = FarmCrops(farm).Where(c => c.dirt.readyForHarvest()).ToList();
        var hungry = farm.getAllFarmAnimals().Where(a => a.fullness.Value < 200).ToList();

        foreach (var h in LastHands)
        {
            string name = (string)h["name"]!;
            if (!actors.TryGetValue(name, out var actor)) continue;
            int nWater = (int?)h["water"] ?? 0, nHarvest = (int?)h["harvest"] ?? 0, nFeed = (int?)h["animals"] ?? 0;

            foreach (var c in PickNearest(dry, nWater, actor.Pos, x => x.tile * 64f))
            { actor.Jobs.Enqueue(new HandJob { Kind = "water", Tile = c.tile }); totalJobs++; }
            foreach (var c in PickNearest(ready, nHarvest, actor.Pos, x => x.tile * 64f))
            { actor.Jobs.Enqueue(new HandJob { Kind = "harvest", Tile = c.tile }); totalJobs++; }
            foreach (var a in PickNearest(hungry, nFeed, actor.Pos, x => x.Position))
            { actor.Jobs.Enqueue(new HandJob { Kind = "animals", Animal = a }); totalJobs++; }

            Monitor.Log($"  {name}: {(string)h["status"]!}, mood {(double)h["mood"]!:0.00}, Plants {(int)h["plants"]!}, " +
                        $"Animals {(int)h["animals"]!}, chores {nWater}/{nHarvest}/{nFeed}", LogLevel.Info);
        }
        batchActive = totalJobs > 0;
        if (batchActive) Hud($"Your farmhands head out ({totalJobs} chores).", HUDMessage.achievement_type);
    }

    // ---------- per-tick behaviour ----------
    private void TickHands()
    {
        foreach (var a in actors.Values)
        {
            if (a.Current == null && a.Jobs.Count > 0) { a.Current = a.Jobs.Dequeue(); a.WorkTimer = 0; }

            Vector2 target = a.Current == null ? a.Home : StandSpot(a.Current);
            var d = target - a.Pos;
            float dist = d.Length();
            if (dist > HandSpeed)
            {
                a.Pos += d / dist * HandSpeed;
                a.Moving = true;
                a.Dir = Math.Abs(d.X) > Math.Abs(d.Y) ? (d.X > 0 ? 1 : 3) : (d.Y > 0 ? 0 : 2);
                a.Activity = a.Current == null ? "heading home" : "walking";
            }
            else
            {
                a.Pos = target;
                a.Moving = false;
                if (a.Current == null) { a.Activity = "resting"; continue; }
                a.Dir = 2; // face the crop/animal
                a.Activity = a.Current.Kind switch { "water" => "watering", "harvest" => "harvesting", _ => "feeding" };
                if (++a.WorkTimer >= WorkTicks) { Perform(a.Current); a.Current = null; }
            }
        }

        if (batchActive && actors.Values.All(a => a.Current == null && a.Jobs.Count == 0))
        {
            batchActive = false;
            Hud($"Farmhands done: watered {doneWater}, harvested {doneHarvest}, fed {doneFed}.", HUDMessage.achievement_type);
        }
    }

    private static Vector2 StandSpot(HandJob j) =>
        j.Kind == "animals" && j.Animal != null ? j.Animal.Position + new Vector2(0, 40) : j.Tile * 64f + new Vector2(0, 64);

    private void Perform(HandJob j)
    {
        var farm = Game1.getFarm();
        bool nearPlayer = Game1.currentLocation is Farm;
        switch (j.Kind)
        {
            case "water":
                if (farm.terrainFeatures.TryGetValue(j.Tile, out var tf) && tf is HoeDirt { crop: not null } dirt)
                {
                    dirt.state.Value = HoeDirt.watered;
                    doneWater++;
                    if (nearPlayer) Game1.playSound("slosh");
                }
                break;
            case "harvest":
                if (farm.terrainFeatures.TryGetValue(j.Tile, out var tf2) && tf2 is HoeDirt h && h.readyForHarvest() && HarvestToBin(farm, h))
                {
                    doneHarvest++;
                    if (nearPlayer) Game1.playSound("harvest");
                }
                break;
            case "animals":
                if (j.Animal != null && j.Animal.fullness.Value < 200)
                {
                    j.Animal.fullness.Value = 255;
                    doneFed++;
                }
                break;
        }
    }

    /// <summary>Harvest one crop straight into the shipping bin; regrowing crops reset, others are removed.</summary>
    private bool HarvestToBin(Farm farm, HoeDirt dirt)
    {
        var crop = dirt.crop;
        var produce = ItemRegistry.Create(crop.indexOfHarvest.Value, 1, allowNull: true);
        if (produce == null) return false;
        farm.getShippingBin(Game1.player).Add(produce);
        var cropData = crop.GetData();
        if (cropData != null && cropData.RegrowDays > 0)
        {
            crop.fullyGrown.Value = true;
            crop.dayOfCurrentPhase.Value = cropData.RegrowDays;
        }
        else dirt.destroyCrop(false);
        return true;
    }

    /// <summary>Finish every pending chore instantly (used when you go to sleep mid-work).</summary>
    private void FlushHands()
    {
        foreach (var a in actors.Values)
        {
            if (a.Current != null) { Perform(a.Current); a.Current = null; }
            while (a.Jobs.Count > 0) Perform(a.Jobs.Dequeue());
        }
        batchActive = false;
    }

    // ---------- drawing and clicking ----------
    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is not Farm) return;
        var b = e.SpriteBatch;
        foreach (var a in actors.Values)
        {
            var local = Game1.GlobalToLocal(Game1.viewport, a.Pos);
            float depth = (a.Pos.Y + 64f) / 10000f;
            b.Draw(Game1.shadowTexture, local + new Vector2(32, 60), null, Color.White, 0f,
                new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y), 4f, SpriteEffects.None, depth - 0.0001f);

            if (a.Sheet != null)
            {
                int col = a.Moving ? (int)(Game1.ticks / 8 % 4) : 0;
                float bob = a.Current != null && !a.Moving ? (float)Math.Sin(Game1.ticks / 4.0) * 3f : 0f;
                var src = new Rectangle(col * 16, Math.Min(a.Dir * 32, a.Sheet.Height - 32), 16, 32);
                b.Draw(a.Sheet, new Rectangle((int)local.X, (int)(local.Y - 64 + bob), 64, 128), src, a.Tint, 0f, Vector2.Zero, SpriteEffects.None, depth);
            }

            var info = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
            double mood = info != null ? (double)info["mood"]! : 0.5;
            var color = mood >= 0.65 ? Color.LimeGreen : mood <= 0.25 ? Color.OrangeRed : Color.White;
            string label = $"{a.Name} ({a.Activity})";
            var size = Game1.smallFont.MeasureString(label);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(local.X + 32 - size.X / 2, local.Y - 100), color, 1f, depth + 0.001f);
        }
    }

    private HandActor? HandAt(Vector2 absolutePixels)
    {
        foreach (var a in actors.Values)
            if (new Rectangle((int)a.Pos.X, (int)a.Pos.Y - 64, 64, 128).Contains(absolutePixels)) return a;
        return null;
    }
}
