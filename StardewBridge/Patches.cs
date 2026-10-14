using HarmonyLib;
using StardewValley;
using StardewValley.Objects;

namespace StardewBridge;

/// <summary>Stardew raises the farmer's arms and draws the held object overhead whenever Farmer.IsCarrying() is true.
/// That is wrong for guns, so report "not carrying" while one is held.</summary>
[HarmonyPatch(typeof(Farmer), nameof(Farmer.IsCarrying))]
internal static class IsCarryingPatch
{
    private static void Postfix(Farmer __instance, ref bool __result)
    {
        if (__result && __instance.IsLocalPlayer && __instance.CurrentItem?.ItemId?.StartsWith("laurm.RimDew_") == true)
            __result = false;
    }
}

/// <summary>Draw farmhands from inside GameLocation.draw (the world's depth-sorted pass) so objects can hide them.</summary>
[HarmonyPatch(typeof(GameLocation), nameof(GameLocation.draw))]
internal static class LocationDrawPatch
{
    private static void Postfix(GameLocation __instance, Microsoft.Xna.Framework.Graphics.SpriteBatch b)
    {
        try { ModEntry.Instance?.DrawHandsInWorld(__instance, b); }
        catch (Exception e) { ModEntry.Instance?.LogPatchError(e); }
    }
}

/// <summary>"Free crafting" setting: recipes always count as affordable and consume nothing.</summary>
[HarmonyPatch(typeof(CraftingRecipe), "doesFarmerHaveIngredientsInInventory")]
internal static class FreeCraftCheckPatch
{
    private static void Postfix(ref bool __result)
    {
        if (ModEntry.Instance?.Config.FreeCrafting == true) __result = true;
    }
}

[HarmonyPatch(typeof(CraftingRecipe), "consumeIngredients")]
internal static class FreeCraftConsumePatch
{
    private static bool Prefix() => ModEntry.Instance?.Config.FreeCrafting != true;
}
