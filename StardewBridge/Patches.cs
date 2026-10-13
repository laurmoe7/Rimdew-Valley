using HarmonyLib;
using StardewValley;

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
