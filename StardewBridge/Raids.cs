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
                break;
            case "Eclipse":
            case "HeatWave":
                Hud($"{label}!", HUDMessage.error_type);
                break;
            default:
                Hud($"{label}: monsters are approaching the farm!", HUDMessage.error_type);
                SpawnMonsters(Math.Clamp(points / 80, 2, 14), points < 250 ? "slime" : points < 600 ? "bat" : points < 1200 ? "brute" : "skeleton");
                break;
        }
    }

    private static void SetTomorrowWeather(string weather, string message)
    {
        Game1.netWorldState.Value.GetWeatherForLocation(LocationContexts.DefaultId).WeatherForTomorrow = weather;
        Hud(message, HUDMessage.error_type);
    }

    internal void SpawnMonsters(int count, string kind)
    {
        var farm = Game1.getFarm();
        var rng = Game1.random;
        int w = farm.map.Layers[0].LayerWidth, h = farm.map.Layers[0].LayerHeight;
        for (int i = 0; i < count; i++)
        {
            Vector2 tile = Vector2.Zero;
            for (int tries = 0; tries < 40; tries++)
            {
                // Spawn along the farm's edges so they walk in.
                tile = rng.Next(4) switch
                {
                    0 => new Vector2(rng.Next(w), 1),
                    1 => new Vector2(rng.Next(w), h - 2),
                    2 => new Vector2(1, rng.Next(h)),
                    _ => new Vector2(w - 2, rng.Next(h))
                };
                if (farm.CanSpawnCharacterHere(tile)) break;
            }
            var pos = tile * 64f;
            Monster m = kind switch
            {
                "slime" => new GreenSlime(pos, 0),
                "bat" => new Bat(pos),
                "brute" => new ShadowBrute(pos),
                "skeleton" => new Skeleton(pos),
                _ => new Grub(pos)
            };
            farm.characters.Add(m);
        }
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
