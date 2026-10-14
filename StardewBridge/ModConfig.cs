using StardewModdingAPI.Utilities;

namespace StardewBridge;

public class ModConfig
{
    /// <summary>Key that opens the RimDew menu.</summary>
    public KeybindList MenuKey { get; set; } = KeybindList.Parse("F8");

    /// <summary>Key that heals all of your wounds (RimWorld injuries, missing parts, bleeding) and restores health and energy.</summary>
    public KeybindList HealKey { get; set; } = KeybindList.Parse("F9");

    /// <summary>When on, every recipe you know can be crafted without materials (and uses none).</summary>
    public bool FreeCrafting { get; set; }

    /// <summary>Villager sprite sheets (Content/Characters/NAME) used to draw farmhands, in hire order.</summary>
    public List<string> HandSprites { get; set; } = new() { "Linus", "Willy", "Kent", "Clint", "Jodi", "Demetrius" };
}
