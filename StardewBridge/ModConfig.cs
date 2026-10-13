using StardewModdingAPI.Utilities;

namespace StardewBridge;

public class ModConfig
{
    /// <summary>Key that opens the RimDew menu.</summary>
    public KeybindList MenuKey { get; set; } = KeybindList.Parse("F8");

    /// <summary>Villager sprite sheets (Content/Characters/NAME) used to draw farmhands, in hire order.</summary>
    public List<string> HandSprites { get; set; } = new() { "Linus", "Willy", "Kent", "Clint", "Jodi", "Demetrius" };
}
