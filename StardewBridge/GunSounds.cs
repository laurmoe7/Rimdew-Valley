using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;

namespace StardewBridge;

/// <summary>RimWorld's own gunshot clips (exported from your RimWorld install to assets/sounds, kept out of git).</summary>
public partial class ModEntry
{
    private static readonly Dictionary<string, string[]> ShotClips = new()
    {
        ["Revolver"] = new[] { "revolver_a", "revolver_b", "revolver_c" },
        ["Autopistol"] = new[] { "type10_1a", "type10_1b", "type10_1c", "type10_1d", "type10_1e" },
        ["MachinePistol"] = new[] { "type25_1a", "type25_1b", "type25_1c", "type25_1d", "type25_1e" },
        ["AssaultRifle"] = new[] { "type23_1a", "type23_1b", "type23_1c", "type23_1d" },
        ["BoltActionRifle"] = new[] { "type19_1a", "type19_1b", "type19_1c", "type19_1d", "type19_1e" },
        ["SniperRifle"] = new[] { "type24_1a", "type24_1b", "type24_1c" },
        ["Shotgun"] = new[] { "type27_1a", "type27_1b", "type27_1c" },
        ["ChainShotgun"] = new[] { "type27_1a", "type27_1b", "type27_1c" },
        ["Minigun"] = new[] { "type6_1a", "type6_1b", "type6_1c", "type6_1d", "type6_1e", "type6_1f", "type6_1g" },
    };

    private readonly Dictionary<string, SoundEffect?> shotSounds = new();
    private readonly Queue<uint> recentShotSounds = new();

    private SoundEffect? LoadShot(string clip)
    {
        if (shotSounds.TryGetValue(clip, out var cached)) return cached;
        SoundEffect? fx = null;
        try
        {
            string path = Path.Combine(Helper.DirectoryPath, "assets", "sounds", clip + ".wav");
            using var stream = File.OpenRead(path);
            fx = SoundEffect.FromStream(stream);
        }
        catch (Exception e)
        {
            Monitor.Log($"Gun sound '{clip}' not loaded ({e.Message}).", LogLevel.Warn);
        }
        shotSounds[clip] = fx;
        return fx;
    }

    /// <summary>Play the gun's shot. <paramref name="worldPx"/> is where it was fired (quieter the farther from you).</summary>
    private void PlayGunShot(Gun gun, Vector2 worldPx)
    {
        if (!ShotClips.TryGetValue(gun.Id, out var clips)) return;

        // Too many overlapping sources can overload OpenAL, so cap how many shots sound per moment.
        while (recentShotSounds.Count > 0 && Game1.ticks - recentShotSounds.Peek() > 40) recentShotSounds.Dequeue();
        if (recentShotSounds.Count >= 8) return;

        var fx = LoadShot(clips[Game1.random.Next(clips.Length)]);
        if (fx == null) return;

        float dist = Vector2.Distance(worldPx, Game1.player.getStandingPosition());
        float volume = Math.Clamp(1f - dist / 1400f, 0.12f, 1f) * Game1.options.soundVolumeLevel * 0.9f;
        if (volume <= 0.01f) return;
        float pitch = (float)(Game1.random.NextDouble() * 0.12 - 0.06);
        recentShotSounds.Enqueue((uint)Game1.ticks);
        try { fx.Play(volume, pitch, 0f); } catch (Exception e) { Monitor.Log($"Gun sound failed: {e.Message}", LogLevel.Trace); }
    }
}
