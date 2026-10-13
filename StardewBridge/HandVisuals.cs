using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Shirts;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace StardewBridge;

/// <summary>Farmhands you can see. They are drawn by this mod (never added to the game's character lists, so
/// nothing about them is written to your save). Each morning, RimWorld says how many chores each hand did and
/// they walk out and do them one by one.</summary>
public partial class ModEntry
{
    private class HandJob
    {
        public string Kind = "";           // water | harvest | animals
        public Vector2 Tile;               // crop tile (water/harvest)
        public FarmAnimal? Animal;         // animals
    }

    private class HandActor
    {
        public string Name = "";
        public Texture2D? Sheet;          // fallback look
        public Farmer? Body;              // farmer character used purely for drawing (not added to the game)
        public Color Tint = Color.White;
        public Vector2 Pos, Home;
        public int Dir;                    // 0 down, 1 right, 2 up, 3 left (NPC sheet rows)
        public bool Moving;
        public HandJob? Current;
        public int WorkTimer;
        public readonly Queue<HandJob> Jobs = new();
        public string Activity = "resting";
        public bool Armed;
        public Vector2? Door;   // bunkhouse door tile (px) if they have one
        public bool Inside;
        public List<Vector2>? Path;
        public Point PathGoal = new(-99999, -99999);
        public int PathIdx;
        public bool Unreachable;     // currently indoors: not drawn, not clickable
        public float Aim;
        public Gun? Weapon;
        public int NextShot, LastShot = -100;
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
    private const int BunkCapacity = 4;

    private Vector2 HomeFor(int i)
    {
        var farm = Game1.getFarm();
        var bunks = farm.buildings.Where(b => (b.buildingType.Value == BunkId || b.buildingType.Value == BunkFreeId) && b.daysOfConstructionLeft.Value <= 0).ToList();
        int bunkIndex = i / BunkCapacity;
        if (bunkIndex < bunks.Count)
        {
            var b = bunks[bunkIndex];
            int slot = i % BunkCapacity;
            int doorX = b.tileX.Value + b.humanDoor.Value.X, doorY = b.tileY.Value + b.humanDoor.Value.Y;
            return new Vector2((doorX - 1 + slot) * 64, (doorY + 1 + slot % 2) * 64);
        }
        // No bunkhouse (or it is full): gather to the side of the farmhouse, not in front of the door.
        var door = farm.GetMainFarmHouseEntry();
        int j = i - bunks.Count * BunkCapacity;
        return new Vector2((door.X + 6 + j % 4) * 64, (door.Y + 1 + j / 4) * 64);
    }

    /// <summary>The bunkhouse door (px) hand i lives behind, or null if there is no bunkhouse for them.</summary>
    private Vector2? DoorFor(int i)
    {
        var bunks = Game1.getFarm().buildings.Where(b => (b.buildingType.Value == BunkId || b.buildingType.Value == BunkFreeId) && b.daysOfConstructionLeft.Value <= 0).ToList();
        int idx = i / BunkCapacity;
        if (idx >= bunks.Count) return null;
        var b = bunks[idx];
        return new Vector2((b.tileX.Value + b.humanDoor.Value.X) * 64, (b.tileY.Value + b.humanDoor.Value.Y) * 64);
    }

    /// <summary>Re-evaluate homes (a bunkhouse may have just been finished).</summary>
    private void RefreshHomes()
    {
        for (int i = 0; i < data.Hands.Count; i++)
            if (actors.TryGetValue(data.Hands[i], out var a)) { a.Home = HomeFor(i); a.Door = DoorFor(i); if (a.Door == null) a.Inside = false; }
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
                a.Body = MakeBody(name);
                if (a.Body == null) a.Sheet = LoadHandSheet(i);
                a.Home = a.Pos = HomeFor(i);
                a.Door = DoorFor(i);
                actors[name] = a;
            }
            a.Home = HomeFor(i);
            a.Door = DoorFor(i);
            if (reset)
            {
                a.Pos = a.Door ?? a.Home;
                a.Inside = a.Door.HasValue;   // they spend the night indoors
                a.Jobs.Clear();
                a.Current = null;
                a.Activity = "resting";
            }
        }
    }

    private static readonly int[] HairStyles = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    // Natural hair colors only.
    private static readonly Color[] HairColors =
    {
        new(30, 25, 25), new(70, 45, 30), new(110, 70, 40), new(140, 85, 45), new(120, 50, 30),
        new(210, 175, 100), new(230, 205, 140), new(170, 70, 40), new(150, 150, 150)
    };

    // (shirt, pants) pairs that look like one outfit.
    private static readonly (Color shirt, Color pants)[] Outfits =
    {
        (new Color(190, 160, 110), new Color(60, 80, 120)),   // tan + denim
        (new Color(170, 60, 50),   new Color(80, 60, 45)),    // red + brown
        (new Color(80, 130, 80),   new Color(160, 140, 100)), // green + khaki
        (new Color(70, 110, 170),  new Color(45, 60, 95)),    // blue + dark denim
        (new Color(225, 215, 190), new Color(95, 70, 50)),    // cream + brown
        (new Color(110, 130, 150), new Color(60, 60, 65)),    // gray-blue + charcoal
        (new Color(200, 160, 60),  new Color(45, 55, 90)),    // mustard + navy
    };

    private static readonly string[] HatNames = { "Straw Hat", "Bucket Hat", "Cowboy Hat" };

    /// <summary>Same name => same look, on every launch (string.GetHashCode isn't stable, so hash by hand).</summary>
    private static int StableSeed(string s)
    {
        int h = 17;
        foreach (char c in s) h = h * 31 + c;
        return h;
    }

    private Farmer? MakeBody(string name)
    {
        try
        {
            var rng = new Random(StableSeed(name));
            bool male = rng.Next(2) == 0;
            var f = new Farmer(new FarmerSprite("Characters/Farmer/farmer_base"), Vector2.Zero, 1, name, new List<Item>(), male);
            f.skin.Value = rng.Next(0, 4);
            f.hair.Value = HairStyles[rng.Next(HairStyles.Length)];
            f.hairstyleColor.Value = HairColors[rng.Next(HairColors.Length)];

            var outfit = Outfits[rng.Next(Outfits.Length)];
            f.pantsColor.Value = outfit.pants;

            // Plain shirts only: prefer dyeable ones and tint them to the outfit colour.
            var shirtData = Helper.GameContent.Load<Dictionary<string, ShirtData>>("Data/Shirts");
            var plain = shirtData.Where(kv => kv.Value.CanBeDyed).Select(kv => kv.Key).ToList();
            if (plain.Count == 0) plain = shirtData.Where(kv => kv.Value.CanChooseDuringCharacterCustomization).Select(kv => kv.Key).ToList();
            if (plain.Count > 0)
            {
                f.changeShirt(plain[rng.Next(plain.Count)]);
                if (f.shirtItem.Value != null) f.shirtItem.Value.clothesColor.Value = outfit.shirt;
            }

            if (rng.Next(2) == 0) f.hat.Value = FindHat(HatNames[rng.Next(HatNames.Length)]);
            f.FarmerRenderer.MarkSpriteDirty();
            return f;
        }
        catch (Exception e)
        {
            Monitor.Log($"Could not build a farmer body for hand '{name}' ({e.Message}); using a villager sprite.", LogLevel.Warn);
            return null;
        }
    }

    /// <summary>Look a hat up by display name instead of guessing its id.</summary>
    private Hat? FindHat(string wanted)
    {
        try
        {
            var hats = Helper.GameContent.Load<Dictionary<string, string>>("Data/Hats");
            var entry = hats.FirstOrDefault(kv => kv.Value.Split('/')[0] == wanted);
            return entry.Key == null ? null : ItemRegistry.Create("(H)" + entry.Key, 1, allowNull: true) as Hat;
        }
        catch { return null; }
    }

    private Texture2D? LoadHandSheet(int i)
    {
        var names = Config.HandSprites;
        if (names.Count == 0) return null;
        try { return Helper.GameContent.Load<Texture2D>("Characters/" + names[i % names.Count]); }
        catch (Exception e) { Monitor.Log($"Could not load hand sprite '{names[i % names.Count]}': {e.Message}", LogLevel.Warn); return null; }
    }

    /// <summary>Update mood/skills/status from a live report, keeping last night's chore counts and hours.</summary>
    private void MergeHandsInfo(JArray fresh)
    {
        foreach (var f in fresh)
        {
            string name = (string)f["name"]!;
            var existing = LastHands.FirstOrDefault(h => (string?)h["name"] == name) as JObject;
            if (existing == null) LastHands.Add(new JObject(f) { ["hours"] = 0.0 });
            else foreach (var key in new[] { "mood", "plants", "animals", "shooting", "status" }) existing[key] = f[key];
        }
        foreach (var gone in LastHands.Where(h => !data.Hands.Contains((string)h["name"]!)).ToList()) gone.Remove();
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
            int nWater = (int?)h["did_water"] ?? 0, nHarvest = (int?)h["did_harvest"] ?? 0, nFeed = (int?)h["did_feed"] ?? 0;

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
    private bool HandCanFight(HandActor a)
    {
        var info = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
        return info == null || ((string?)info["status"] == "ok" && (double)info["mood"]! > 0.25);
    }

    /// <summary>Defend the farm: walk into range of the nearest monster and shoot with a gun matched to the hand's
    /// RimWorld Shooting skill.</summary>
    private void Fight(HandActor a, List<StardewValley.Monsters.Monster> monsters)
    {
        var info = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
        int skill = (int?)info?["shooting"] ?? 3;
        var gun = GunForSkill(skill);
        a.Weapon = gun;
        a.Armed = true;
        a.Activity = "defending";

        var feet = a.Pos + new Vector2(32, 64);
        var target = monsters.OrderBy(m => Vector2.DistanceSquared(m.Position, feet)).First();
        var to = target.Position + new Vector2(32, 32) - feet;
        float dist = to.Length();
        a.Aim = (float)Math.Atan2(to.Y, to.X);
        a.Dir = Math.Abs(to.X) > Math.Abs(to.Y) ? (to.X > 0 ? 1 : 3) : (to.Y > 0 ? 0 : 2);

        var monsterTile = ToTile(target.Position);
        bool clear = dist <= 520 && HasLineOfSight(ToTile(a.Pos), monsterTile);
        if (clear) a.Moving = false;
        else
        {
            // No clear shot: walk (around terrain) toward the monster until there is one.
            StepTo(a, new Vector2(monsterTile.X * 64, monsterTile.Y * 64), HandSpeed * 1.2f);
            if (a.Unreachable) { a.Unreachable = false; a.PathGoal = new Point(-99999, -99999); a.Moving = false; }
        }

        if (clear && Game1.ticks >= a.NextShot)
        {
            a.NextShot = (int)Game1.ticks + gun.Cooldown * 2 + Game1.random.Next(0, 12);
            a.LastShot = (int)Game1.ticks;
            int facing = a.Dir switch { 0 => 2, 1 => 1, 2 => 0, _ => 3 };
            var muzzle = HandPosFor(feet, facing) + new Vector2((float)Math.Cos(a.Aim), (float)Math.Sin(a.Aim)) * 40f;
            float spread = (float)((Game1.random.NextDouble() - 0.5) * 0.25 / (1 + skill * 0.15));
            int pellets = gun.Pellets;
            for (int p = 0; p < pellets; p++)
            {
                float ang = a.Aim + spread + (pellets > 1 ? (float)((Game1.random.NextDouble() - 0.5) * gun.Spread * 2) : 0f);
                int dmg = (int)(Game1.random.Next(gun.MinDmg, gun.MaxDmg + 1) * (0.5 + skill * 0.07));
                SpawnBullet(Game1.getFarm(), gun, muzzle, ang, Math.Max(1, dmg), p == 0);
            }
        }
    }

    private readonly List<(int tick, int count, string kind)> pendingSpawns = new();

    private void TickHands()
    {
        // Delayed raid arrivals (the warning pop-up fires when they are scheduled).
        for (int i = pendingSpawns.Count - 1; i >= 0; i--)
            if (Game1.ticks >= pendingSpawns[i].tick)
            {
                SpawnMonsters(pendingSpawns[i].count, pendingSpawns[i].kind);
                pendingSpawns.RemoveAt(i);
            }

        if (Game1.ticks % 300 == 0) { walkCache.Clear(); RefreshHomes(); }
        var monsters = Game1.getFarm().characters.OfType<StardewValley.Monsters.Monster>().Where(m => m.Health > 0).ToList();
        foreach (var a in actors.Values)
        {
            // Hands always carry a gun matched to their RimWorld Shooting skill, held low when not fighting.
            var skillInfo = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
            a.Weapon = GunForSkill((int?)skillInfo?["shooting"] ?? 3);
            a.Armed = !a.Inside;
            a.Aim = a.Dir switch { 0 => 1.25f, 1 => 0.35f, 2 => -1.25f, _ => MathHelper.Pi - 0.35f };

            bool busy = a.Current != null || a.Jobs.Count > 0;
            bool fighting = monsters.Count > 0 && HandCanFight(a);
            if (a.Inside)
            {
                if (!busy && !fighting) { a.Moving = false; a.Activity = "inside"; continue; }
                a.Inside = false;                         // step out of the door
                if (a.Door.HasValue) a.Pos = a.Door.Value;
            }
            if (fighting) { Fight(a, monsters); continue; }

            if (a.Current == null && a.Jobs.Count > 0) { a.Current = a.Jobs.Dequeue(); a.WorkTimer = 0; }

            Vector2 target = a.Current == null ? (a.Door ?? a.Home) : StandSpot(a.Current);
            bool arrived = StepTo(a, target, HandSpeed);
            if (a.Unreachable)
            {
                // No walkable route (fenced in, blocked...). Never clip through terrain: do the chore from here,
                // or if heading home just snap there as a last resort.
                a.Unreachable = false;
                a.PathGoal = new Point(-99999, -99999);
                if (a.Current != null) { Perform(a.Current); a.Current = null; continue; }
                a.Pos = target;
                arrived = true;
            }
            if (!arrived)
            {
                a.Activity = a.Current == null ? "heading home" : "walking";
            }
            else
            {
                a.Pos = target;
                a.Moving = false;
                if (a.Current == null)
                {
                    if (a.Door.HasValue) { a.Inside = true; a.Activity = "inside"; }   // walked up to the door: go in
                    else a.Activity = "resting";
                    continue;
                }
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
            if (a.Inside) continue;
            var local = Game1.GlobalToLocal(Game1.viewport, a.Pos);
            float depth = (a.Pos.Y + 64f) / 10000f;
            float bobY = a.Current != null && !a.Moving ? (float)Math.Sin(Game1.ticks / 4.0) * 3f : 0f;

            if (a.Body != null)
            {
                DrawBody(b, a, bobY);
                if (a.Armed && a.Weapon != null)
                {
                    int facing = a.Dir switch { 0 => 2, 1 => 1, 2 => 0, _ => 3 };
                    DrawGunSprite(b, a.Weapon, HandPosFor(a.Pos + new Vector2(32, 64), facing), a.Aim, facing, a.Pos.Y + 64, a.LastShot);
                }
            }
            else b.Draw(Game1.shadowTexture, local + new Vector2(32, 60), null, Color.White, 0f,
                new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y), 4f, SpriteEffects.None, depth - 0.0001f);

            if (a.Body == null && a.Sheet != null)
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

    /// <summary>Pose and draw the farmer body: facing and walk frames come from the game's own walk cycles.</summary>
    private void DrawBody(SpriteBatch b, HandActor a, float bobY)
    {
        var f = a.Body!;
        int facing = a.Dir switch { 0 => 2, 1 => 1, 2 => 0, _ => 3 }; // sheet row -> Stardew facing (0 up, 1 right, 2 down, 3 left)
        // Farmer sheet: 6 frames per row. Walk cycle = stand, step, stand, step2. Left reuses right, flipped.
        int[] cycle = facing switch { 0 => new[] { 12, 13, 12, 14 }, 1 or 3 => new[] { 6, 7, 6, 8 }, _ => new[] { 0, 1, 0, 2 } };
        int frame = cycle[a.Moving ? (int)(Game1.ticks / 6 % cycle.Length) : 0];

        f.currentLocation = Game1.currentLocation;
        f.Position = a.Pos + new Vector2(0, bobY);
        f.FacingDirection = facing;
        f.FarmerSprite.setCurrentSingleFrame(frame, 32000, false, facing == 3);
        f.draw(b);
    }

    private HandActor? HandAt(Vector2 absolutePixels)
    {
        foreach (var a in actors.Values)
            if (!a.Inside && new Rectangle((int)a.Pos.X, (int)a.Pos.Y - 64, 64, 128).Contains(absolutePixels)) return a;
        return null;
    }
}
