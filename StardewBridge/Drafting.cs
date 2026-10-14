using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Monsters;

namespace StardewBridge;

/// <summary>RimWorld-style drafting. A drafted farmhand drops their chores, leaves the bunkhouse, arms up and stands their ground:
/// they shoot anything in line of sight but never chase. Hold Ctrl and right-click the ground to send every drafted hand there,
/// or right-click a monster to make them all focus fire on it. G (configurable) drafts or undrafts everyone.</summary>
public partial class ModEntry
{
    internal bool AnyDrafted => actors.Values.Any(a => a.Drafted);

    internal bool IsDrafted(string name) => actors.TryGetValue(name, out var a) && a.Drafted;

    internal void ToggleDraftAll()
    {
        if (actors.Count == 0) { Hud("You have no farmhands to draft.", HUDMessage.error_type); return; }
        bool on = !AnyDrafted;
        foreach (var a in actors.Values) SetDrafted(a, on);
        Hud(on ? "Farmhands drafted. Ctrl + right-click: move or focus fire." : "Farmhands stand down.", on ? HUDMessage.error_type : HUDMessage.achievement_type);
    }

    internal void ToggleDraft(string name)
    {
        if (actors.TryGetValue(name, out var a)) SetDrafted(a, !a.Drafted);
    }

    private void SetDrafted(HandActor a, bool on)
    {
        a.Drafted = on;
        a.OrderTile = null;
        a.Focus = null;
        if (on) { a.Current = null; a.Jobs.Clear(); a.Dining = false; }
        else a.Activity = "resting";
    }

    /// <summary>One tick for a drafted hand who is out on the farm.</summary>
    private void TickDrafted(HandActor a, List<Monster> monsters)
    {
        a.Armed = true;
        if (a.Focus != null && !IsTarget(a.Focus)) a.Focus = null;

        if (a.OrderTile is { } ot)
        {
            a.Activity = "moving to position";
            bool arrived = StepTo(a, new Vector2(ot.X * 64, ot.Y * 64), HandSpeed * 1.2f, allowPartial: true);
            if (arrived || a.Unreachable || (a.PathPartial && a.Path != null && a.PathIdx >= a.Path.Count)) { a.OrderTile = null; a.Moving = false; a.Unreachable = false; }
            else return;
        }

        var pool = a.Focus != null
            ? new List<Monster> { a.Focus }
            : monsters.Where(m => Vector2.Distance(m.Position + new Vector2(32, 32), a.Pos + new Vector2(32, 32)) <= 560 && HasLineOfSight(ToTile(a.Pos), ToTile(m.Position))).ToList();
        if (pool.Count > 0)
        {
            Fight(a, pool, hold: a.Focus == null);          // focus fire may walk to get a shot; otherwise they hold position
            a.Activity = a.Focus != null ? "focus fire" : "drafted (firing)";
            return;
        }
        a.Moving = false;
        a.Activity = "drafted";
    }

    /// <summary>Ctrl + right-click while hands are drafted: a monster under the cursor is focused, otherwise the ground is the new position.</summary>
    private bool TryGiveOrder(ButtonPressedEventArgs e)
    {
        if (!AnyDrafted || Game1.currentLocation is not Farm farm) return false;
        if (!(Helper.Input.IsDown(SButton.LeftControl) || Helper.Input.IsDown(SButton.RightControl))) return false;
        Helper.Input.Suppress(e.Button);
        var px = e.Cursor.AbsolutePixels;
        var drafted = actors.Values.Where(a => a.Drafted && !a.Inside).ToList();

        var monster = farm.characters.OfType<Monster>().FirstOrDefault(m => IsTarget(m) && new Rectangle((int)m.Position.X - 16, (int)m.Position.Y - 64, 96, 128).Contains(px.X, px.Y));
        if (monster != null)
        {
            foreach (var a in drafted) a.Focus = monster;
            Hud($"Focus fire!", HUDMessage.error_type);
            return true;
        }

        var click = new Point((int)(px.X / 64), (int)(px.Y / 64));
        var spots = new[] { new Point(0, 0), new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(1, 1), new Point(-1, 1), new Point(0, -1), new Point(2, 0) };
        int i = 0;
        foreach (var a in drafted)
        {
            var want = new Point(click.X + spots[i % spots.Length].X, click.Y + spots[i % spots.Length].Y);
            a.OrderTile = NearestWalkable(want, 4) ?? want;
            a.Focus = null;
            a.PathGoal = NoGoal;
            i++;
        }
        if (drafted.Count > 0) Hud($"{drafted.Count} farmhand{(drafted.Count == 1 ? "" : "s")} moving to position.", HUDMessage.newQuest_type);
        return true;
    }
}
