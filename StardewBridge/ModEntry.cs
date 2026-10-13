using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewBridge;

/// <summary>Persisted per save (via SMAPI save data).</summary>
public class SaveData
{
    public List<string> Hands { get; set; } = new();
    public HashSet<string> Researched { get; set; } = new();
    public HashSet<string> GatedRemoved { get; set; } = new();
    public bool GateRecipes { get; set; }
    public string ResearchTarget { get; set; } = "";
    public long LastEarned { get; set; }
    public Dictionary<string, int> Priorities { get; set; } = new(); // "name|task" -> 0..4
}

/// <summary>Stardew side of the bridge: Stardew is the visible game, RimWorld simulates in the background.
/// Features live in partial files: Villagers, Health, Farmhands, Raids, Research, Commands.</summary>
public partial class ModEntry : Mod
{
    private HubClient hub = null!;
    private SaveData data = new();
    internal ModConfig Config = new();
    internal bool RimWorldOnline;
    internal JArray LastHands = new();
    internal JObject? LastHealth;
    internal readonly Dictionary<string, string> NpcLabel = new();
    internal HubClient Hub => hub;
    internal SaveData Data => data;

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        hub = new HubClient("127.0.0.1", 7777, m => Monitor.Log(m, LogLevel.Info));

        helper.Events.GameLoop.SaveLoaded += (_, _) => OnSaveLoaded();
        helper.Events.GameLoop.Saving += (_, _) => Helper.Data.WriteSaveData("state", data);
        helper.Events.GameLoop.DayStarted += (_, _) => OnDayStarted();
        helper.Events.GameLoop.DayEnding += (_, _) => OnDayEnding();
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Input.ButtonsChanged += (_, _) => { if (Config.MenuKey.JustPressed()) { if (Game1.activeClickableMenu is RimDewMenu) Game1.exitActiveMenu(); else OpenMenu(); } };

        RegisterCommands();
    }

    private void Send(JObject msg) => hub.Send(msg);

    private void OnSaveLoaded()
    {
        data = Helper.Data.ReadSaveData<SaveData>("state") ?? new SaveData();
        lastHealth = Game1.player.health;
        SendRoster();
        ApplyRecipeGating();
    }

    /// <summary>Tell RimWorld who exists. Sent on load and whenever RimWorld (re)connects.</summary>
    private void SendRoster()
    {
        var npcs = Utility.getAllVillagers().Where(n => n.CanSocialize).Select(n => n.Name).Distinct().ToList();
        Send(new JObject { ["type"] = "npc_list", ["npcs"] = new JArray(npcs) });
        foreach (var h in data.Hands) Send(new JObject { ["type"] = "hire", ["name"] = h });
        foreach (var (key, v) in data.Priorities)
        {
            var parts = key.Split('|');
            Send(new JObject { ["type"] = "priority", ["name"] = parts[0], ["task"] = parts[1], ["value"] = v });
        }
        if (data.ResearchTarget != "") Send(new JObject { ["type"] = "research_set", ["id"] = data.ResearchTarget });
    }

    private void OnDayStarted()
    {
        Send(new JObject
        {
            ["type"] = "time_sync",
            ["day"] = Game1.dayOfMonth,
            ["season"] = Game1.currentSeason,
            ["year"] = Game1.year
        });
        SendDayStartNpcEvents();
        ReportTrampling();
        ApplyRecipeGating();
    }

    private void OnDayEnding()
    {
        var farm = Game1.getFarm();
        bool paid = PayHands();
        long earned = Game1.player.totalMoneyEarned;
        long gained = Math.Max(0, earned - data.LastEarned);
        data.LastEarned = earned;

        Send(new JObject
        {
            ["type"] = "day_end",
            ["day"] = Game1.dayOfMonth,
            ["season"] = Game1.currentSeason,
            ["weather"] = CurrentWeather(),
            ["wealth"] = ComputeWealth(),
            ["wages_paid"] = paid,
            ["research_points"] = gained / 25,
            ["backlog"] = BuildBacklog(farm)
        });
        TrampleCrops(farm);
    }

    private static string CurrentWeather()
    {
        if (Game1.isGreenRain) return "green_rain";
        if (Game1.isLightning) return "storm";
        if (Game1.isRaining) return "rain";
        if (Game1.isSnowing) return "snow";
        return "sun";
    }

    private static long ComputeWealth()
    {
        var farm = Game1.getFarm();
        return Game1.player.Money + Game1.player.totalMoneyEarned / 4 + farm.buildings.Count * 1000 + farm.getAllFarmAnimals().Count * 300;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady) return;

        TickVillagers(e);
        TickHealth(e);

        if (!e.IsMultipleOf(30)) return;
        while (hub.Inbox.TryDequeue(out var msg))
        {
            try { Handle(msg); }
            catch (Exception ex) { Monitor.Log($"Error handling {msg["type"]}: {ex}", LogLevel.Error); }
        }
    }

    private void Handle(JObject msg)
    {
        switch ((string?)msg["type"])
        {
            case "welcome": RimWorldOnline = (bool)msg["peer_online"]!; break;
            case "peer_status":
                bool online = (bool)msg["online"]!;
                RimWorldOnline = online;
                Game1.addHUDMessage(new HUDMessage(online ? "RimWorld connected" : "RimWorld disconnected", HUDMessage.newQuest_type));
                if (online) SendRoster();
                break;
            case "item_send":
                var item = ItemRegistry.Create((string?)msg["item"] ?? "", (int?)msg["count"] ?? 1, allowNull: true);
                if (item == null) { Monitor.Log($"Unknown item '{msg["item"]}' from RimWorld.", LogLevel.Warn); break; }
                Game1.player.addItemByMenuIfNecessary(item);
                break;
            case "npc_state": OnNpcState(msg); break;
            case "health_state": OnHealthState(msg); break;
            case "incident": OnIncident(msg); break;
            case "work_done": OnWorkDone(msg); break;
            case "research_state": OnResearchState(msg); break;
            case "research_unlocked": OnResearchUnlocked((string?)msg["id"] ?? ""); break;
            default: Monitor.Log($"Hub message: {msg}", LogLevel.Trace); break;
        }
    }

    private static void Hud(string text, int type = HUDMessage.newQuest_type) => Game1.addHUDMessage(new HUDMessage(text, type));
}
