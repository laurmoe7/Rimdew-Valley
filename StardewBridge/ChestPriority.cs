using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace StardewBridge;

/// <summary>RimWorld-style storage priority on every chest (including fridges and freezers). Farmhands put what they make into the
/// highest-priority chest that has room (and allows the item, see ChestSettings.cs), and take supplies from the lowest-priority chest first.</summary>
public partial class ModEntry
{
    private const string PriorityKey = "laurm.RimDew/storagePriority";
    internal static readonly string[] PriorityNames = { "Low", "Normal", "Preferred", "Important", "Critical" };

    internal static int ChestPriority(Chest c) =>
        c.modData.TryGetValue(PriorityKey, out var s) && int.TryParse(s, out var p) ? Math.Clamp(p, 0, PriorityNames.Length - 1) : 1;

    private static bool HasRoom(Chest c, Item item) =>
        c.Items.Any(x => x != null && x.canStackWith(item) && x.Stack < x.maximumStackSize()) || c.Items.CountItemStacks() < c.GetActualCapacity();

    /// <summary>Where a hand puts something they made: the highest-priority chest with room (freezers last, since cooked food loses its taste there), nearest first.</summary>
    private Chest? DepositChest(Item item, Vector2 near) =>
        FarmChests().Where(c => ChestAllows(c, item) && HasRoom(c, item))
                    .OrderBy(c => IsFreezer(c) ? 1 : 0)
                    .ThenByDescending(ChestPriority)
                    .ThenBy(c => Vector2.DistanceSquared(c.TileLocation, near))
                    .FirstOrDefault();
}
