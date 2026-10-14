using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Monsters;

namespace StardewBridge;

/// <summary>Human raiders fall instead of dying. A downed raider can be captured (right-click while close), which holds them as a captive;
/// anyone left on the ground crawls away overnight.</summary>
public partial class ModEntry
{
    /// <summary>A monster the farmhands, turrets and traps should deal with: alive and not lying on the ground.</summary>
    internal static bool IsTarget(Monster m) => m.Health > 0 && !(m is HumanRaider { Downed: true });

    internal void NoteRaiderDown(HumanRaider r)
    {
        Hud($"{r.RaiderName} is down! Right-click to capture them.", HUDMessage.achievement_type);
        Defenses.Add($"{r.RaiderName} was downed");
    }

    private bool TryCaptureRaider(ButtonPressedEventArgs e)
    {
        if (Game1.currentLocation is not Farm farm) return false;
        var cursor = e.Cursor.AbsolutePixels;
        var raider = farm.characters.OfType<HumanRaider>().FirstOrDefault(r => r.Downed
            && new Rectangle((int)r.Position.X - 32, (int)r.Position.Y - 64, 128, 160).Contains(cursor.X, cursor.Y));
        if (raider == null) return false;
        Helper.Input.Suppress(e.Button);
        if (Vector2.Distance(Game1.player.Tile, raider.Tile) > 3.5f) { Hud("Get closer to capture them.", HUDMessage.error_type); return true; }
        CaptureRaider(farm, raider);
        return true;
    }

    internal void CaptureRaider(GameLocation farm, HumanRaider raider)
    {
        farm.characters.Remove(raider);
        data.Captives.Add(raider.RaiderName + "|" + raider.Look.Replace("|", " "));
        Game1.playSound("coin");
        Hud($"Captured {raider.RaiderName}! ({data.Captives.Count} captive{(data.Captives.Count == 1 ? "" : "s")})", HUDMessage.achievement_type);
    }

    // Captives are stored as "Name|Look|lastRecruitAttemptDay".
    private static string CaptiveName(string c) => c.Split('|')[0];

    /// <summary>Try to turn a captive into a farmhand: 55% per try, one try per captive per day.</summary>
    internal void RecruitCaptive(int index)
    {
        if (index < 0 || index >= data.Captives.Count) return;
        var parts = data.Captives[index].Split('|');
        string name = parts[0];
        long today = Game1.stats.DaysPlayed;
        if (parts.Length > 2 && long.TryParse(parts[2], out var last) && last == today) { Hud($"{name} has already refused today. Try again tomorrow.", HUDMessage.error_type); return; }
        if (Game1.random.NextDouble() >= 0.55)
        {
            data.Captives[index] = $"{name}|{(parts.Length > 1 ? parts[1] : "")}|{today}";
            Hud($"{name} refuses to join you. Try again tomorrow.", HUDMessage.error_type);
            return;
        }
        string hand = name;
        for (int n = 2; data.Hands.Contains(hand); n++) hand = name + n;
        data.Captives.RemoveAt(index);
        data.Hands.Add(hand);
        Send(new JObject { ["type"] = "hire", ["name"] = hand });
        SyncHands(false);
        foreach (var other in data.Hands.Where(h => h != hand)) SendHandEvent(other, "recruited");
        Game1.playSound("achievement");
        Hud($"{hand} joined the farm as a farmhand! ({DailyWage}g/day)", HUDMessage.achievement_type);
    }

    internal void ReleaseCaptive(int index)
    {
        if (index < 0 || index >= data.Captives.Count) return;
        string name = CaptiveName(data.Captives[index]);
        data.Captives.RemoveAt(index);
        Hud($"You let {name} go.", HUDMessage.newQuest_type);
    }

    internal void ExecuteCaptive(int index)
    {
        if (index < 0 || index >= data.Captives.Count) return;
        string name = CaptiveName(data.Captives[index]);
        data.Captives.RemoveAt(index);
        foreach (var hand in data.Hands) SendHandEvent(hand, "executed");
        Game1.playSound("hitEnemy");
        Hud($"{name} was executed. Your farmhands are disturbed.", HUDMessage.error_type);
    }

    /// <summary>Custom monster types cannot be written to a save file, so no raider may be on the farm when the game saves.</summary>
    private void ClearRaidersBeforeSave()
    {
        foreach (var loc in Game1.locations)
            loc.characters.RemoveWhere(c => c is HumanRaider);
    }
}
