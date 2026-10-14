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

/// <summary>The Freezer is a real storage chest with its own look: creating the item yields a Chest carrying the freezer's id.</summary>
[HarmonyPatch(typeof(StardewValley.ItemTypeDefinitions.BigCraftableDataDefinition), nameof(StardewValley.ItemTypeDefinitions.BigCraftableDataDefinition.CreateItem))]
internal static class FreezerCreatePatch
{
    private static void Postfix(StardewValley.ItemTypeDefinitions.ParsedItemData data, ref Item __result)
    {
        if (data.ItemId == ModEntry.FreezerId && __result is not Chest)
            __result = new Chest(true, Microsoft.Xna.Framework.Vector2.Zero, ModEntry.FreezerId);
    }
}

/// <summary>Vanilla chests animate a lid from a fixed sprite layout; the freezer is a single static sprite, so draw it like any big craftable.</summary>
[HarmonyPatch(typeof(Chest), nameof(Chest.draw), new[] { typeof(Microsoft.Xna.Framework.Graphics.SpriteBatch), typeof(int), typeof(int), typeof(float) })]
internal static class FreezerDrawPatch
{
    private static bool Prefix(Chest __instance, Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.ItemId != ModEntry.FreezerId) return true;
        var data = ItemRegistry.GetDataOrErrorItem(__instance.QualifiedItemId);
        var pos = Game1.GlobalToLocal(Game1.viewport, new Microsoft.Xna.Framework.Vector2(x * 64, (y - 1) * 64));
        float depth = Math.Max(0f, ((y + 1) * 64 - 24) / 10000f) + x * 1E-05f;
        spriteBatch.Draw(data.GetTexture(), pos, data.GetSourceRect(), Microsoft.Xna.Framework.Color.White * alpha, 0f, Microsoft.Xna.Framework.Vector2.Zero, 4f,
            Microsoft.Xna.Framework.Graphics.SpriteEffects.None, depth);
        return false;
    }
}

/// <summary>A microwave standing on a table. The game would draw its tall sprite from the table top and let it hang over the edge,
/// so hide it from the normal pass and draw only the sprite's lower 16x16 (the microwave itself) where a small item would sit.</summary>
[HarmonyPatch(typeof(StardewValley.Objects.Furniture), nameof(StardewValley.Objects.Furniture.draw),
    new[] { typeof(Microsoft.Xna.Framework.Graphics.SpriteBatch), typeof(int), typeof(int), typeof(float) })]
internal static class TableMicrowaveDrawPatch
{
    private static void Prefix(StardewValley.Objects.Furniture __instance, out StardewValley.Object? __state)
    {
        __state = null;
        if (__instance.heldObject.Value is { } h && ModEntry.IsMicrowaveObj(h))
        {
            __state = h;
            __instance.heldObject.Value = null;
        }
    }

    // a finalizer, so the microwave always goes back even if the table's own drawing throws
    private static Exception? Finalizer(StardewValley.Objects.Furniture __instance, Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch, float alpha,
        StardewValley.Object? __state, Exception? __exception)
    {
        if (__state == null) return __exception;
        __instance.heldObject.Value = __state;
        if (__exception != null) return __exception;
        var data = ItemRegistry.GetDataOrErrorItem(__state.QualifiedItemId);
        var src = data.GetSourceRect();
        src = new Microsoft.Xna.Framework.Rectangle(src.X, src.Y + 14, src.Width, 16);   // rows 14-29: the 14-row microwave plus its outline
        var box = __instance.boundingBox.Value;
        float low = __instance.drawHeldObjectLow.Value ? 32 : 85;
        var pos = Game1.GlobalToLocal(Game1.viewport, new Microsoft.Xna.Framework.Vector2(box.Center.X - 32, box.Center.Y - low));
        float depth = box.Bottom / 10000f;
        spriteBatch.Draw(Game1.shadowTexture, pos + new Microsoft.Xna.Framework.Vector2(32f, 53f), Game1.shadowTexture.Bounds, Microsoft.Xna.Framework.Color.White * alpha, 0f,
            new Microsoft.Xna.Framework.Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y), 4f,
            Microsoft.Xna.Framework.Graphics.SpriteEffects.None, depth);
        spriteBatch.Draw(data.GetTexture(), pos, src, Microsoft.Xna.Framework.Color.White * alpha, 0f, Microsoft.Xna.Framework.Vector2.Zero, 4f,
            Microsoft.Xna.Framework.Graphics.SpriteEffects.None, depth + 1E-05f);
        return null;
    }
}
