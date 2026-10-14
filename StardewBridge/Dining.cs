using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

namespace StardewBridge;

/// <summary>RimWorld pawns eat sitting at a table. A hungry hand takes food from the chest (or dispenser), carries it to the bunkhouse
/// and eats on a chair that stands next to a table. With no such seat they eat standing at the chest, as before.</summary>
public partial class ModEntry
{
    private const int MealTicks = 420;

    private static bool IsTableFurniture(Furniture f) => f.furniture_type.Value == Furniture.table || f.furniture_type.Value == Furniture.longTable;

    /// <summary>A free seat in the hand's bunkhouse that is orthogonally next to a table.</summary>
    private (Furniture chair, Point seat, Furniture table)? FindDiningSeat(HandActor a)
    {
        var loc = a.Bunk?.GetIndoors();
        if (loc == null || !a.Door.HasValue) return null;
        var tables = loc.furniture.Where(IsTableFurniture).ToList();
        if (tables.Count == 0) return null;
        foreach (var chair in loc.furniture)
        {
            if (chair is BedFurniture || IsTableFurniture(chair)) continue;
            foreach (var s in chair.GetSeatPositions())
            {
                var seat = new Point((int)s.X, (int)s.Y);
                if (actors.Values.Any(o => o != a && o.Inside && o.UseTarget == chair && ToTile(o.UseSpot) == seat)) continue;
                foreach (var table in tables)
                {
                    var bb = table.boundingBox.Value;
                    int x0 = bb.X / 64, y0 = bb.Y / 64, x1 = (bb.Right - 1) / 64, y1 = (bb.Bottom - 1) / 64;
                    bool beside = (seat.X >= x0 && seat.X <= x1 && (seat.Y == y0 - 1 || seat.Y == y1 + 1))
                               || (seat.Y >= y0 && seat.Y <= y1 && (seat.X == x0 - 1 || seat.X == x1 + 1));
                    if (beside) return (chair, seat, table);
                }
            }
        }
        return null;
    }

    private bool HasMeal(HandActor a) => a.Meal != null || a.MealPaste;

    /// <summary>The meal a hand carried to the table is finished: hunger drops and the mood event fires.</summary>
    private void FinishMeal(HandActor a)
    {
        if (a.MealPaste)
        {
            data.Hunger[a.Name] = Math.Max(0, HungerOf(a.Name) - 45);
            SendHandEvent(a.Name, "ate_paste");
            Defenses.Add($"{a.Name} ate nutrient paste at the table");
        }
        else if (a.Meal != null)
        {
            ApplyMeal(a.Name, a.Meal, a.MealFrozen);
            Defenses.Add($"{a.Name} ate at the table");
        }
        a.Meal = null; a.MealPaste = false; a.MealFrozen = false; a.Dining = false;
    }

    /// <summary>Inside the bunkhouse with food in hand: go sit down. If the table or chair is gone, just eat.</summary>
    private void StartDining(HandActor a)
    {
        var seat = FindDiningSeat(a);
        if (seat == null) { FinishMeal(a); return; }
        var (chair, spot, table) = seat.Value;
        a.UseTarget = chair;
        a.UseSpot = new Vector2(spot.X * 64, spot.Y * 64);
        a.UseDir = DirToward(a.UseSpot, new Vector2(table.boundingBox.Value.Center.X, table.boundingBox.Value.Center.Y) - new Vector2(32, 32));
        a.AtUse = false;
        a.Dining = true;
        ResetPath(a);
    }
}
