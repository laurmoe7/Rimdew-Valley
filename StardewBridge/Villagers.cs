using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewBridge;

/// <summary>Feature 1: villagers are RimWorld pawns. Gifts/chats/festivals become thoughts; mood comes back as
/// friendship changes and mental breaks (a broken villager won't talk to you that day).</summary>
public partial class ModEntry
{
    private readonly Dictionary<string, (int gifts, bool talked)> seenFriendship = new();
    internal readonly Dictionary<string, double> npcMood = new();
    internal readonly HashSet<string> npcBroken = new();
    private Item? heldPrev;

    private void TickVillagers(UpdateTickedEventArgs e)
    {
        // Snapshot the held item every tick: a gift consumes it before we notice the friendship change.
        var held = Game1.player.CurrentItem;
        if (!e.IsMultipleOf(6)) { if (held != null) heldPrev = held.getOne(); return; }

        foreach (var (name, fr) in Game1.player.friendshipData.Pairs)
        {
            seenFriendship.TryGetValue(name, out var prev);
            if (fr.GiftsToday > prev.gifts && heldPrev != null)
            {
                var npc = Game1.getCharacterFromName(name);
                string kind = npc == null ? "gift_neutral" : npc.getGiftTasteForThisItem(heldPrev) switch
                {
                    NPC.gift_taste_love => "gift_loved",
                    NPC.gift_taste_like => "gift_liked",
                    NPC.gift_taste_dislike => "gift_disliked",
                    NPC.gift_taste_hate => "gift_hated",
                    _ => "gift_neutral"
                };
                if (kind != "gift_neutral") SendNpcEvent(name, kind);
            }
            if (fr.TalkedToToday && !prev.talked) SendNpcEvent(name, "talked");
            seenFriendship[name] = (fr.GiftsToday, fr.TalkedToToday);
        }
    }

    private void SendNpcEvent(string npc, string kind) =>
        Send(new JObject { ["type"] = "npc_event", ["npc"] = npc, ["kind"] = kind });

    private void SendDayStartNpcEvents()
    {
        seenFriendship.Clear();
        bool festival = Game1.isFestival();
        foreach (var npc in Utility.getAllVillagers().Where(n => n.CanSocialize))
        {
            if (festival) SendNpcEvent(npc.Name, "festival");
            if (npc.isBirthday()) SendNpcEvent(npc.Name, "birthday");
        }
    }

    private void OnNpcState(JObject msg)
    {
        var moods = ((JArray)msg["npcs"]!).Select(n => (double)n["mood"]!).ToList();
        if (moods.Count > 0)
            Monitor.Log($"RimWorld finished the night. Village mood: avg {moods.Average():0.00}, " +
                        $"{moods.Count(m => m >= 0.65)} happy, {moods.Count(m => m <= 0.25)} unhappy (rd_moods for details).", LogLevel.Info);
        npcBroken.Clear();
        foreach (var n in (JArray)msg["npcs"]!)
        {
            string name = (string)n["name"]!;
            double mood = (double)n["mood"]!;
            bool brk = (bool)n["break"]!;
            npcMood[name] = mood;
            NpcLabel[name] = brk ? (string)n["label"]! : "";
            var npc = Game1.getCharacterFromName(name);
            if (npc == null) continue;

            if (brk)
            {
                npcBroken.Add(name);
                Hud($"{name} is having a {(string)n["label"]!}.", HUDMessage.error_type);
            }
            if (msg["apply"] is JValue { Type: JTokenType.Boolean } a && !(bool)a) continue;
            // Mood moves friendship once per day: happy villagers warm to you, unhappy ones drift away.
            if (Game1.player.friendshipData.ContainsKey(name))
            {
                if (mood >= 0.65) Game1.player.changeFriendship(10, npc);
                else if (mood <= 0.25) Game1.player.changeFriendship(brk ? -25 : -5, npc);
            }
        }
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady) return;
        SuppressSwing(e);
        if (e.Button.IsActionButton() && Game1.activeClickableMenu == null && (Game1.currentLocation is Farm || InBunkInterior())
            && HandAt(e.Cursor.AbsolutePixels) != null)
        {
            Helper.Input.Suppress(e.Button);
            Game1.activeClickableMenu = new RimDewMenu(this, 1);
            return;
        }
        if (!e.Button.IsActionButton() || npcBroken.Count == 0) return;
        var npc = Game1.currentLocation.isCharacterAtTile(e.Cursor.GrabTile) as NPC
                  ?? Game1.currentLocation.isCharacterAtTile(e.Cursor.Tile) as NPC;
        if (npc != null && npcBroken.Contains(npc.Name))
        {
            Helper.Input.Suppress(e.Button);
            Hud($"{npc.displayName} is too upset to talk right now.", HUDMessage.error_type);
        }
    }
}
