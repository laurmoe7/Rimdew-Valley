using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;

namespace StardewBridge;

/// <summary>Feature 2 and 6: RimWorld's real storyteller picks threats; we turn them into valley events.
/// Raid strength scales with farm wealth (computed in ComputeWealth, applied RimWorld-side).</summary>
public partial class ModEntry
{
    private int tramplePending;

    private void OnIncident(JObject msg)
    {
        string def = (string?)msg["def"] ?? "";
        string label = (string?)msg["label"] ?? def;
        int points = (int?)msg["points"] ?? 100;
        Monitor.Log($"Incident: {def} ({points} pts)", LogLevel.Info);

        switch (def)
        {
            case "Infestation":
                Hud("Grubs are crawling out of the ground on the farm!", HUDMessage.error_type);
                SpawnMonsters(Math.Clamp(points / 150, 2, 8), "grub");
                break;
            case "ToxicFallout":
                SetTomorrowWeather(Game1.weather_green_rain, "Toxic fallout: green rain is coming.");
                break;
            case "Flashstorm":
                SetTomorrowWeather(Game1.weather_lightning, "A flash storm is brewing.");
                break;
            case "ColdSnap":
            case "VolcanicWinter":
                SetTomorrowWeather(Game1.weather_snow, $"{label}: snow is coming.");
                SetTempEvent(-18);
                break;
            case "HeatWave":
                SetTempEvent(15);
                Hud($"{label}! It is {OutdoorTemp():0} C outside.", HUDMessage.error_type);
                break;
            case "Eclipse":
                Hud($"{label}!", HUDMessage.error_type);
                break;
            case "ManhunterPack":
                StartRaid("manhunter", label, points);
                break;
            case "MechCluster":
                StartRaid("mech", label, points);
                break;
            default:
                // RimWorld raids come in flavours (arrival strategies); pick one at random.
                double roll = Game1.random.NextDouble();
                StartRaid(roll < 0.5 ? "standard" : roll < 0.75 ? "sapper" : "droppod", label, points);
                break;
        }
    }

    internal const string SapperKey = "laurm.RimDew/sapper";

    /// <summary>Raid flavours: standard (10 s warning), sappers (chew sandbags, defuse traps), drop pods (land beside you),
    /// mech cluster (a tight group of golems), manhunter pack (fast, tough bats).</summary>
    internal void StartRaid(string type, string label, int points)
    {
        int n = Math.Clamp(points / 80, 2, 14);
        string kind = points < 250 ? "slime" : points < 600 ? "bat" : points < 1200 ? "brute" : "skeleton";
        int now = (int)Game1.ticks;
        switch (type)
        {
            case "sapper":
                Hud($"{label}: sappers! {n} of them will dig through your sandbags and defuse traps in 10 seconds.", HUDMessage.error_type);
                pendingSpawns.Add((now + 600, n, "sapper"));
                break;
            case "droppod":
                Hud($"{label}: drop pods incoming! {n} raiders land near you in 3 seconds.", HUDMessage.error_type);
                pendingSpawns.Add((now + 180, n, "pod" + kind));
                break;
            case "mech":
                Hud($"{label}: a mech cluster crashes down on the farm in 5 seconds!", HUDMessage.error_type);
                pendingSpawns.Add((now + 300, Math.Clamp(points / 250, 2, 6), "golem"));
                break;
            case "manhunter":
                Hud($"{label}: a pack of animals has gone manhunter! {n} are charging the farm in 5 seconds.", HUDMessage.error_type);
                pendingSpawns.Add((now + 300, n, "manhunter"));
                break;
            default:
                Hud($"{label}! {n} monsters arrive in 10 seconds. Get ready!", HUDMessage.error_type);
                pendingSpawns.Add((now + 600, n, kind));
                break;
        }
    }

    private static void SetTomorrowWeather(string weather, string message)
    {
        Game1.netWorldState.Value.GetWeatherForLocation(LocationContexts.DefaultId).WeatherForTomorrow = weather;
        Hud(message, HUDMessage.error_type);
    }

    /// <summary>Spawn in scattered groups on open ground anywhere on the farm, away from the player, so raids
    /// can't be stopped by fencing off one entrance.</summary>
    internal void SpawnMonsters(int count, string kind)
    {
        var farm = Game1.getFarm();
        var rng = Game1.random;
        int w = farm.map.Layers[0].LayerWidth, h = farm.map.Layers[0].LayerHeight;
        var player = Game1.player.Tile;

        // "pod..." raiders drop in close to the player; "golem" arrive as one tight cluster.
        bool pod = kind.StartsWith("pod");
        if (pod) kind = kind.Substring(3);
        float minDist = pod ? 4 : 10;

        bool Ok(Vector2 t) => t.X > 1 && t.Y > 1 && t.X < w - 2 && t.Y < h - 2
                              && Vector2.Distance(t, player) >= minDist && farm.CanSpawnCharacterHere(t);

        int groupSize = kind == "golem" ? count : pod ? Math.Clamp(count / 2, 1, 5) : Math.Clamp(count / 3, 1, 4), placed = 0;
        while (placed < count)
        {
            Vector2 center = Vector2.Zero;
            bool found = false;
            for (int tries = 0; tries < 200 && !found; tries++)
            {
                center = pod ? player + new Vector2(rng.Next(-9, 10), rng.Next(-9, 10)) : new Vector2(rng.Next(2, w - 2), rng.Next(2, h - 2));
                found = Ok(center);
            }
            if (!found) break; // nowhere open (very unlikely)
            for (int g = 0; g < groupSize && placed < count; g++)
            {
                var tile = center;
                for (int tries = 0; tries < 12; tries++)
                {
                    var cand = center + new Vector2(rng.Next(-3, 4), rng.Next(-3, 4));
                    if (Ok(cand)) { tile = cand; break; }
                }
                var pos = tile * 64f;
                Monster m = kind switch
                {
                    "slime" => new GreenSlime(pos, 0),
                    "bat" => new Bat(pos),
                    "brute" => new ShadowBrute(pos),
                    "skeleton" => new Skeleton(pos),
                    "sapper" => new DustSpirit(pos),
                    "golem" => new RockGolem(pos),
                    "manhunter" => new Bat(pos),
                    _ => new Grub(pos)
                };
                if (kind == "sapper") m.modData[SapperKey] = "1";
                if (kind == "golem") { m.Health *= 3; m.MaxHealth = m.Health; }   // a mech cluster should be a hard target
                if (kind == "manhunter") { m.Health = (int)(m.Health * 1.6); m.MaxHealth = m.Health; m.speed += 1; }
                farm.characters.Add(m);
                placed++;
            }
        }
        if (pod && placed > 0) farm.playSound("explosion");
    }

    /// <summary>Monsters still on the farm at day's end trample crops, then leave.</summary>
    private void TrampleCrops(Farm farm)
    {
        int monsters = farm.characters.OfType<Monster>().Count();
        if (monsters == 0) return;
        var crops = FarmCrops(farm).ToList();
        int n = Math.Min(crops.Count, monsters * 2);
        foreach (var (_, dirt) in crops.OrderBy(_ => Game1.random.Next()).Take(n))
            dirt.destroyCrop(false);
        tramplePending = n;
        farm.characters.RemoveWhere(c => c is Monster);
    }

    private void ReportTrampling()
    {
        if (tramplePending > 0)
            Hud($"Monsters trampled {tramplePending} crops overnight.", HUDMessage.error_type);
        tramplePending = 0;
    }
}
