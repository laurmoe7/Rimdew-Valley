using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;

namespace StardewBridge;

/// <summary>Bunkhouses look like a random cabin (log, stone or plank) on the outside and keep the shed interior. A test option opens Robin's
/// (or the Wizard's) building menu with every building free and instant.</summary>
public partial class ModEntry
{
    /// <summary>True while a carpenter menu opened from the F8 test tab is up: everything costs nothing and builds at once.</summary>
    internal static bool FreeBuild;

    internal void OpenFreeBuildMenu(string builder)
    {
        FreeBuild = true;
        Game1.activeClickableMenu = new CarpenterMenu(builder);
    }

    private void TickFreeBuild()
    {
        if (FreeBuild && Game1.activeClickableMenu is not CarpenterMenu) FreeBuild = false;
    }

    private void OnBuildingListChanged(object? sender, BuildingListChangedEventArgs e)
    {
        foreach (var b in e.Added)
        {
            if (FreeBuild) b.daysOfConstructionLeft.Value = 0;
            AssignCabinLook(b);
        }
    }

    private const string LookKey = "laurm.RimDew/cabinLook";

    /// <summary>Give a bunkhouse a random cabin skin, once (remembered in the building's mod data, so it never changes after).</summary>
    private void AssignCabinLook(Building b)
    {
        if (!IsBunk(b) || b.modData.ContainsKey(LookKey)) return;
        b.modData[LookKey] = "1";
        var skins = b.GetData()?.Skins;
        if (skins == null || skins.Count == 0) return;
        int pick = Game1.random.Next(skins.Count + 1);          // 0 = the plain default look
        b.skinId.Value = pick == 0 ? null : skins[pick - 1].Id;
        b.resetTexture();
    }

    /// <summary>Bunkhouses built before cabin looks existed get one too.</summary>
    private void AssignCabinLooks()
    {
        if (!Context.IsWorldReady) return;
        foreach (var b in Game1.getFarm().buildings) AssignCabinLook(b);
    }
}

[HarmonyPatch(typeof(CarpenterMenu), nameof(CarpenterMenu.DoesFarmerHaveEnoughResourcesToBuild))]
internal static class FreeBuildResourcesPatch
{
    private static void Postfix(ref bool __result) { if (ModEntry.FreeBuild) __result = true; }
}

[HarmonyPatch(typeof(CarpenterMenu), nameof(CarpenterMenu.CanBuildCurrentBlueprint))]
internal static class FreeBuildCanBuildPatch
{
    private static void Postfix(ref bool __result) { if (ModEntry.FreeBuild) __result = true; }
}

[HarmonyPatch(typeof(CarpenterMenu), nameof(CarpenterMenu.ConsumeResources))]
internal static class FreeBuildConsumePatch
{
    private static bool Prefix() => !ModEntry.FreeBuild;
}
