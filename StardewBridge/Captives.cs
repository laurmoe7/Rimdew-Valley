using Microsoft.Xna.Framework;
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
        data.Captives.Add(raider.RaiderName + "|" + raider.Look);
        Game1.playSound("coin");
        Hud($"Captured {raider.RaiderName}! ({data.Captives.Count} captive{(data.Captives.Count == 1 ? "" : "s")})", HUDMessage.achievement_type);
    }

    /// <summary>Custom monster types cannot be written to a save file, so no raider may be on the farm when the game saves.</summary>
    private void ClearRaidersBeforeSave()
    {
        foreach (var loc in Game1.locations)
            loc.characters.RemoveWhere(c => c is HumanRaider);
    }
}
