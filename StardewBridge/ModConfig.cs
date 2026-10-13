using StardewModdingAPI.Utilities;

namespace StardewBridge;

public class ModConfig
{
    /// <summary>Key that opens the RimDew menu.</summary>
    public KeybindList MenuKey { get; set; } = KeybindList.Parse("F8");
}
