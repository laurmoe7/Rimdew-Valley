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
        public string Owner = "";          // hand who does it (for skill XP)
        public Vector2 Tile;               // crop tile (water/harvest)
        public FarmAnimal? Animal;         // animals
        public Vector2? Via;               // refuel/reload: chest tile to fetch supplies from first
        public string Item = "";           // refuel/reload: qualified id fetched (wood or copper ore)
        public int Count, Carry;           // how many to fetch / how many are in hand
        public bool Fetched;
        public List<Item> Held = new();    // items taken from the chest and not yet used
        public Vector2? Stand;             // chosen stand spot for the current phase (cleared when the phase changes)
        public bool StandPhase;
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
        public StardewValley.Buildings.Building? Bunk;   // the bunkhouse building they live in
        public Vector2? Door;   // bunkhouse door tile (px) if they have one
        public bool Inside;     // currently in their bunkhouse (Pos is then in the bunkhouse's own coordinates)
        public bool Hidden;     // asleep in bed: not drawn
        public bool AtUse;
        public StardewValley.Objects.Furniture? UseTarget;
        public Vector2 UseSpot;
        public int UseDir, UseUntil, NextIdlePick;
        public List<Vector2>? Path;
        public Point PathGoal = new(-99999, -99999);
        public int PathIdx;
        public bool Unreachable;
        public bool PathPartial;
        public int RepathTick;
        public Vector2 LastPos;
        public int StuckTicks;
        public HashSet<Point>? Reach;
        public Point ReachOrigin;
        public int ReachTick;
        public int XpPlants, XpAnimals, XpShooting;   // earned since the last night report     // currently indoors: not drawn, not clickable
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
    private StardewValley.Buildings.Building? BunkFor(int i)
    {
        var bunks = Game1.getFarm().buildings.Where(b => (b.buildingType.Value == BunkId || b.buildingType.Value == BunkFreeId) && b.daysOfConstructionLeft.Value <= 0).ToList();
        int idx = i / BunkCapacity;
        return idx < bunks.Count ? bunks[idx] : null;
    }

    private Vector2? DoorFor(int i)
    {
        var b = BunkFor(i);
        return b == null ? null : new Vector2((b.tileX.Value + b.humanDoor.Value.X) * 64, (b.tileY.Value + b.humanDoor.Value.Y) * 64);
    }

    /// <summary>Re-evaluate homes (a bunkhouse may have just been finished).</summary>
    private void RefreshHomes()
    {
        for (int i = 0; i < data.Hands.Count; i++)
            if (actors.TryGetValue(data.Hands[i], out var a)) { a.Home = HomeFor(i); a.Door = DoorFor(i); a.Bunk = BunkFor(i); if (a.Door == null) a.Inside = false; }
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
                a.Bunk = BunkFor(i);
                actors[name] = a;
            }
            a.Home = HomeFor(i);
            a.Door = DoorFor(i);
            a.Bunk = BunkFor(i);
            if (reset)
            {
                a.Jobs.Clear();
                a.Current = null;
                a.Activity = "resting";
                a.Inside = false;
                a.Pos = a.Home;
                if (a.Door.HasValue && a.Bunk?.GetIndoors() != null) EnterBunk(a);   // they spend the night indoors
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
            if (existing == null) { var copy = (JObject)f.DeepClone(); copy["hours"] = 0.0; LastHands.Add(copy); }
            else foreach (var key in new[] { "mood", "plants", "animals", "shooting", "status", "thoughts", "needs" }) existing[key] = f[key];
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
            { actor.Jobs.Enqueue(new HandJob { Kind = "water", Tile = c.tile, Owner = name }); totalJobs++; }
            foreach (var c in PickNearest(ready, nHarvest, actor.Pos, x => x.tile * 64f))
            { actor.Jobs.Enqueue(new HandJob { Kind = "harvest", Tile = c.tile, Owner = name }); totalJobs++; }
            foreach (var a in PickNearest(hungry, nFeed, actor.Pos, x => x.Position))
            { actor.Jobs.Enqueue(new HandJob { Kind = "animals", Animal = a, Owner = name }); totalJobs++; }

            Monitor.Log($"  {name}: {(string)h["status"]!}, mood {(double)h["mood"]!:0.00}, Plants {(int)h["plants"]!}, " +
                        $"Animals {(int)h["animals"]!}, chores {nWater}/{nHarvest}/{nFeed}", LogLevel.Info);
        }
        batchActive = totalJobs > 0;
        if (batchActive) Hud($"Your farmhands head out ({totalJobs} chores).", HUDMessage.achievement_type);
    }

    // ---------- per-tick behaviour ----------
    /// <summary>Is there something this hand could actually shoot at? Indoor hands are judged from their door.</summary>
    private bool HandCanEngage(HandActor a, List<StardewValley.Monsters.Monster> monsters)
    {
        if (!a.Inside) return TryPickTargets(a, monsters, out _, out _);
        var saved = a.Pos;
        a.Pos = a.Door ?? a.Home;
        bool ok = TryPickTargets(a, monsters, out _, out _);
        a.Pos = saved;
        return ok;
    }

    private bool HandCanFight(HandActor a)
    {
        var info = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
        // Only a mental break or being downed keeps a hand out of the fight; unhappy hands still fight, just worse (see Fight).
        string? status = (string?)info?["status"];
        return status != "mental break" && status != "downed";
    }

    /// <summary>Defend the farm: walk into range of the nearest monster and shoot with a gun matched to the hand's
    /// RimWorld Shooting skill.</summary>
    private void Fight(HandActor a, List<StardewValley.Monsters.Monster> monsters, bool hold)
    {
        var info = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
        int skill = (int?)info?["shooting"] ?? 3;
        var gun = GunForSkill(skill);
        a.Weapon = gun;
        a.Armed = true;
        a.Activity = "defending";

        var feet = a.Pos + new Vector2(32, 32);   // a character's standing point is the middle of its 64px box
        var target = monsters.OrderBy(m => Vector2.DistanceSquared(m.Position, feet)).First();
        var to = target.Position + new Vector2(32, 32) - feet;
        float dist = to.Length();
        a.Aim = (float)Math.Atan2(to.Y, to.X);
        a.Dir = Math.Abs(to.X) > Math.Abs(to.Y) ? (to.X > 0 ? 1 : 3) : (to.Y > 0 ? 0 : 2);

        var monsterTile = ToTile(target.Position);
        int slot = Math.Abs(StableSeed(a.Name)) % 5;                          // 0..4: personal stand-off distance
        bool clear = dist <= 360 + slot * 55 && HasLineOfSight(ToTile(a.Pos), monsterTile);
        if (clear || hold) a.Moving = false;
        else
        {
            // No clear shot: walk (around terrain) toward the monster until there is one.
            // Each hand aims for its own tile around the target (when that tile is open ground), so a group fans out.
            var offsets = new[] { new Point(-2, -1), new Point(2, -1), new Point(0, 2), new Point(-3, 1), new Point(3, 1) };
            var goalTile = new Point(monsterTile.X + offsets[slot].X, monsterTile.Y + offsets[slot].Y);
            if (!Walkable(goalTile)) goalTile = monsterTile;
            StepTo(a, new Vector2(goalTile.X * 64, goalTile.Y * 64), HandSpeed * 1.2f, allowPartial: true);
        }

        // No progress for ~1.5s while out of position: log why, rescue the hand, and if still stuck shoot from here.
        a.StuckTicks = Vector2.DistanceSquared(a.Pos, a.LastPos) < 0.25f ? a.StuckTicks + 1 : 0;
        a.LastPos = a.Pos;
        if (!clear && a.StuckTicks == 90) { LogStuck(a, monsterTile, dist); RescueHand(a); }
        if (!clear && a.StuckTicks > 150 && dist <= 520) clear = true;

        if (clear && Game1.ticks >= a.NextShot)
        {
            a.NextShot = (int)Game1.ticks + gun.Cooldown * 2 + Game1.random.Next(0, 12);
            a.LastShot = (int)Game1.ticks;
            a.XpShooting++;
            int facing = a.Dir switch { 0 => 2, 1 => 1, 2 => 0, _ => 3 };
            var muzzle = HandPosFor(feet, facing, a.Dir is 1 or 3) + new Vector2((float)Math.Cos(a.Aim), (float)Math.Sin(a.Aim)) * 40f;
            PlayGunShot(gun, muzzle);
            float spread = (float)((Game1.random.NextDouble() - 0.5) * 0.25 / (1 + skill * 0.15));
            int pellets = gun.Pellets;
            for (int p = 0; p < pellets; p++)
            {
                float ang = a.Aim + spread + (pellets > 1 ? (float)((Game1.random.NextDouble() - 0.5) * gun.Spread * 2) : 0f);
                double mood = (double?)info?["mood"] ?? 0.5;
                double moodFactor = mood < 0.3 ? 0.7 : 1.0;   // unhappy hands shoot at 70% damage
                int dmg = (int)(Game1.random.Next(gun.MinDmg, gun.MaxDmg + 1) * (0.5 + skill * 0.07) * moodFactor);
                SpawnBullet(Game1.getFarm(), gun, muzzle, ang, Math.Max(1, dmg), p == 0);
            }
        }
    }

    /// <summary>Keep hands from piling onto one tile: push overlapping hands apart, but only onto walkable ground.</summary>
    private void SeparateHands()
    {
        var list = actors.Values.Where(a => !a.Inside).ToList();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                var d = list[j].Pos - list[i].Pos;
                float dist = d.Length();
                if (dist >= 56) continue;
                var push = dist < 0.5f ? new Vector2(1, 0) : d / dist;
                float amount = (56 - dist) * 0.5f;
                // A hand that is walking somewhere barely gets shoved, so two hands heading opposite ways in a narrow gap can pass each other.
                // A hand in the middle of a job never gets shoved off its spot (that restarted the job timer forever).
                TryNudge(list[i], -push * amount * PushFactor(list[i], list[j]));
                TryNudge(list[j], push * amount * PushFactor(list[j], list[i]));
            }
    }

    private static bool Working(HandActor a) => a.Current != null && a.WorkTimer > 0 && !a.Moving;

    private static float PushFactor(HandActor a, HandActor other) => Working(a) ? 0f : Working(other) ? 1.5f : a.Moving ? 0.15f : 1f;

    private void TryNudge(HandActor a, Vector2 by)
    {
        var np = a.Pos + by;
        var t = ToTile(np);
        if (t == ToTile(a.Pos) || Walkable(t)) a.Pos = np;
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
        TickMaintenance();
        var monsters = Game1.getFarm().characters.OfType<StardewValley.Monsters.Monster>().Where(m => m.Health > 0).ToList();
        foreach (var a in actors.Values)
        {
            // Hands always carry a gun matched to their RimWorld Shooting skill, held low when not fighting.
            var skillInfo = LastHands.FirstOrDefault(h => (string?)h["name"] == a.Name);
            a.Weapon = GunForSkill((int?)skillInfo?["shooting"] ?? 3);
            a.Armed = !a.Inside;
            a.Aim = a.Dir switch { 0 => 1.25f, 1 => 0.35f, 2 => -1.25f, _ => MathHelper.Pi - 0.35f };

            bool busy = a.Current != null || a.Jobs.Count > 0;
            bool fighting = monsters.Count > 0 && HandCanFight(a) && HandCanEngage(a, monsters);
            if (a.Inside) { TickIndoor(a, busy, fighting); continue; }
            if (fighting && TryPickTargets(a, monsters, out var pool, out bool hold)) { Fight(a, pool, hold); continue; }

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
            if (!arrived && a.Current != null)
            {
                // Watchdog: two hands crowding the same spot can lock each other in place. If this hand has not moved for ~2 seconds
                // and is already close to its spot, put it on the spot (a walkable tile) so the job can proceed.
                a.StuckTicks = Vector2.DistanceSquared(a.Pos, a.LastPos) < 0.25f ? a.StuckTicks + 1 : 0;
                a.LastPos = a.Pos;
                if (a.StuckTicks > 120 && Vector2.Distance(a.Pos, target) < 192f) { a.Pos = target; a.StuckTicks = 0; a.PathGoal = new Point(-99999, -99999); arrived = true; }
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
                    if (a.Door.HasValue && a.Bunk?.GetIndoors() != null) EnterBunk(a);   // walked up to the door: go in
                    else a.Activity = "resting";
                    continue;
                }
                a.Dir = 2; // face the crop/animal
                a.Activity = a.Current.Kind switch
                {
                    "water" => "watering", "harvest" => "harvesting",
                    "refuel" => a.Current.Fetched ? "refueling" : "fetching wood",
                    "reload" => a.Current.Fetched ? "reloading" : "fetching ore",
                    "repair" => a.Current.Fetched ? "repairing" : "fetching fiber",
                    "cook" => a.Current.Fetched ? "cooking" : "fetching food",
                    "eat" => "eating",
                    _ => "feeding"
                };
                if (++a.WorkTimer >= WorkTicks)
                {
                    a.WorkTimer = 0;
                    if (IsMaintJob(a.Current) && a.Current.Via.HasValue && !a.Current.Fetched) { if (!FetchSupplies(a.Current)) a.Current = null; }
                    else { Perform(a.Current); a.Current = null; }
                }
            }
        }

        SeparateHands();

        if (batchActive && actors.Values.All(a => a.Current == null && a.Jobs.Count == 0))
        {
            batchActive = false;
            Hud($"Farmhands done: watered {doneWater}, harvested {doneHarvest}, fed {doneFed}.", HUDMessage.achievement_type);
        }
    }

    private Vector2 StandSpot(HandJob j)
    {
        if (IsMaintJob(j))
        {
            if (j.Stand.HasValue && j.StandPhase == j.Fetched) return j.Stand.Value;
            // Pick an open tile around the target that no other hand is standing on or heading to, so two hands never fight over a spot.
            var target = j.Via.HasValue && !j.Fetched ? j.Via.Value : j.Tile;
            var taken = new List<Vector2>();
            foreach (var other in actors.Values)
            {
                if (other.Name == j.Owner) continue;
                taken.Add(other.Pos);
                if (other.Current is { Stand: { } st } oc && oc != j) taken.Add(st);
            }
            var me = actors.TryGetValue(j.Owner, out var self) ? self.Pos : target * 64f;
            var offsets = new[] { (0, 1), (-1, 1), (1, 1), (-1, 0), (1, 0), (-1, -1), (1, -1), (0, -1), (0, 2) };
            Vector2? pick = null;
            foreach (var (dx, dy) in offsets.OrderBy(o => Vector2.DistanceSquared(me, (target + new Vector2(o.Item1, o.Item2)) * 64f)))
            {
                var cand = target + new Vector2(dx, dy);
                if (!Walkable(new Point((int)cand.X, (int)cand.Y))) continue;
                if (taken.Any(t => Vector2.Distance(t, cand * 64f) < 70f)) continue;
                pick = cand * 64f; break;
            }
            j.Stand = pick ?? (target + new Vector2(0, 1)) * 64f;
            j.StandPhase = j.Fetched;
            return j.Stand.Value;
        }
        return j.Kind == "animals" && j.Animal != null ? j.Animal.Position + new Vector2(0, 40) : j.Tile * 64f + new Vector2(0, 64);
    }

    /// <summary>Record skill XP for the hand who did a chore.</summary>
    private void Credit(HandJob j, bool plants)
    {
        if (!actors.TryGetValue(j.Owner, out var o)) return;
        if (plants) o.XpPlants++; else o.XpAnimals++;
    }

    /// <summary>Tell RimWorld what each hand practised since the last report, then reset the counters.</summary>
    private void SendHandXp()
    {
        var list = new JArray();
        foreach (var a in actors.Values)
        {
            if (a.XpPlants + a.XpAnimals + a.XpShooting == 0) continue;
            list.Add(new JObject { ["name"] = a.Name, ["plants"] = a.XpPlants, ["animals"] = a.XpAnimals, ["shooting"] = a.XpShooting });
            a.XpPlants = a.XpAnimals = a.XpShooting = 0;
        }
        if (list.Count > 0) Send(new JObject { ["type"] = "hand_xp", ["hands"] = list });
    }

    /// <summary>Hands who have a bed: each finished bunkhouse houses as many of its (up to 4) residents as it has bed slots
    /// (single bed 1, double bed 2).</summary>
    private List<string> HousedNames()
    {
        var housed = new List<string>();
        var bunks = Game1.getFarm().buildings.Where(b => (b.buildingType.Value == BunkId || b.buildingType.Value == BunkFreeId) && b.daysOfConstructionLeft.Value <= 0).ToList();
        for (int idx = 0; idx < bunks.Count; idx++)
        {
            var indoors = bunks[idx].GetIndoors();
            if (indoors == null) continue;
            int slots = indoors.furniture.OfType<StardewValley.Objects.BedFurniture>().Sum(bed => bed.bedType == StardewValley.Objects.BedFurniture.BedType.Double ? 2 : 1);
            housed.AddRange(data.Hands.Skip(idx * BunkCapacity).Take(BunkCapacity).Take(slots));
        }
        return housed;
    }

    private bool HandsHoused() => HousedNames().Count >= data.Hands.Count;

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
                    Credit(j, true);
                    if (nearPlayer) Game1.playSound("slosh");
                }
                break;
            case "harvest":
                if (farm.terrainFeatures.TryGetValue(j.Tile, out var tf2) && tf2 is HoeDirt h && h.readyForHarvest() && HarvestToBin(farm, h))
                {
                    doneHarvest++;
                    Credit(j, true);
                    if (nearPlayer) Game1.playSound("harvest");
                }
                break;
            case "refuel":
            case "reload":
            case "repair":
            case "cook":
            case "eat":
                DoMaintenance(j);
                break;
            case "animals":
                if (j.Animal != null && j.Animal.fullness.Value < 200)
                {
                    j.Animal.fullness.Value = 255;
                    doneFed++;
                    Credit(j, false);
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
    private bool InBunkInterior() => Game1.currentLocation != null && actors.Values.Any(a => a.Bunk != null && a.Bunk.GetIndoors() == Game1.currentLocation);

    /// <summary>Called from a Harmony postfix on GameLocation.draw, i.e. inside the world's depth-sorted sprite pass, so farmhands
    /// are correctly hidden behind trees, buildings and other objects.</summary>
    internal void DrawHandsInWorld(GameLocation loc, SpriteBatch b)
    {
        if (!Context.IsWorldReady || loc != Game1.currentLocation) return;
        if (InBunkInterior()) { DrawInteriorHands(b); return; }
        if (loc is not Farm) return;
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
                    DrawGunSprite(b, a.Weapon, HandPosFor(a.Pos + new Vector2(32, 32), facing, !a.Moving && a.Dir is 1 or 3), a.Aim, facing, a.Pos.Y + 64, a.LastShot);
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
        bool aimPose = a.Armed && !a.Moving && (facing == 1 || facing == 3);
        f.FarmerSprite.setCurrentSingleFrame(aimPose ? AimPoseFrame : frame, 32000, false, facing == 3);
        f.draw(b);
    }

    private HandActor? HandAt(Vector2 absolutePixels)
    {
        foreach (var a in actors.Values)
            if (a.Inside && !a.Hidden && a.Bunk?.GetIndoors() == Game1.currentLocation && new Rectangle((int)a.Pos.X, (int)a.Pos.Y - 64, 64, 128).Contains(absolutePixels)) return a;
        foreach (var a in actors.Values)
            if (!a.Inside && Game1.currentLocation is Farm && new Rectangle((int)a.Pos.X, (int)a.Pos.Y - 64, 64, 128).Contains(absolutePixels)) return a;
        return null;
    }
}
