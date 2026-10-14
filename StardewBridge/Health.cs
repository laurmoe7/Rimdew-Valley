using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buffs;
using StardewValley.Monsters;

namespace StardewBridge;

/// <summary>Feature 3: the farmer is a RimWorld pawn. Lost HP becomes a body-part injury; mobility, pain and
/// bleeding come back as a lasting debuff, and sleeping restores health only as far as the injuries allow.</summary>
public partial class ModEntry
{
    private const string InjuryBuffId = "laurm.StardewBridge/injury";
    private const string MoodBuffId = "laurm.StardewBridge/mood";
    private int lastHealth;
    private double lastTendQuality;
    private double bleed;
    private int ignoreHealthUntil;

    private void TickHealth(UpdateTickedEventArgs e)
    {
        int hp = Game1.player.health;
        if (e.Ticks < ignoreHealthUntil) { lastHealth = hp; return; }
        if (hp < lastHealth)
            Send(new JObject { ["type"] = "player_hurt", ["amount"] = lastHealth - hp, ["kind"] = GuessDamageKind() });
        lastHealth = hp;

        // Bleeding drains stamina (not HP, which would feed back into new injuries).
        if (bleed > 0 && e.IsMultipleOf(300))
            Game1.player.Stamina = Math.Max(0, Game1.player.Stamina - (float)(bleed * 4));
    }

    private string GuessDamageKind()
    {
        if (forcedHurtKind != null) { var k = forcedHurtKind; forcedHurtKind = null; return k; }
        var m = Game1.currentLocation.characters.OfType<Monster>()
            .OrderBy(x => Vector2Distance(x.Tile, Game1.player.Tile)).FirstOrDefault();
        if (m == null) return "blunt";
        if (m is GreenSlime) return "blunt";
        if (m is Bat) return "bite";
        return "cut";
    }

    private static float Vector2Distance(Microsoft.Xna.Framework.Vector2 a, Microsoft.Xna.Framework.Vector2 b) =>
        Microsoft.Xna.Framework.Vector2.Distance(a, b);

    private void OnHealthState(JObject msg)
    {
        LastHealth = msg;
        double moving = (double)msg["moving"]!, manip = (double)msg["manipulation"]!, pain = (double)msg["pain"]!;
        bleed = (double)msg["bleed"]!;
        bool dead = (bool)msg["dead"]!;
        double pct = (double)msg["health_pct"]!;
        var injuries = ((JArray)msg["injuries"]!).Select(x => (string)x!).ToList();

        // Lasting debuff scaled by mobility, hand function and pain.
        var effects = new BuffEffects();
        effects.Speed.Value = -(float)Math.Round((1 - moving) * 3 + pain * 1.5);
        effects.Attack.Value = -(float)Math.Round((1 - manip) * 4);
        Game1.player.buffs.Remove(InjuryBuffId);
        if (effects.Speed.Value != 0 || effects.Attack.Value != 0)
        {
            Game1.player.applyBuff(new Buff(InjuryBuffId, displaySource: "Injuries", duration: Buff.ENDLESS,
                effects: effects, isDebuff: true, displayName: "Injured",
                description: injuries.Count > 0 ? string.Join(", ", injuries) : "Recovering"));
        }

        // Farmer's own RimWorld mood (pain, weather, chats, festivals, raids) shifts stamina and luck.
        Game1.player.buffs.Remove(MoodBuffId);
        if (msg["mood"] is JValue mv && !dead)
        {
            double mood = (double)mv;
            var m = new BuffEffects();
            m.MaxStamina.Value = (float)Math.Round((mood - 0.5) * 80);
            m.LuckLevel.Value = mood >= 0.8 ? 1 : mood <= 0.2 ? -1 : 0;
            if (Math.Abs(m.MaxStamina.Value) >= 4)
                Game1.player.applyBuff(new Buff(MoodBuffId, displaySource: "Mood", duration: Buff.ENDLESS, effects: m,
                    isDebuff: m.MaxStamina.Value < 0, displayName: mood >= 0.5 ? "Good mood" : "Bad mood",
                    description: $"Mood {mood:0.00}: {m.MaxStamina.Value:+0;-0} max stamina"));
        }

        if (dead)
        {
            Game1.player.health = 1;
            Game1.player.Stamina = 0;
            Hud("You collapse from your injuries!", HUDMessage.error_type);
        }
        else if ((string?)msg["phase"] == "tend")
        {
            // Tending restores HP right away, up to what the body can currently support.
            int ceiling = Math.Max(10, (int)(Game1.player.maxHealth * Math.Min(1.0, pct)));
            int heal = 10 + (int)(25 * lastTendQuality);
            Game1.player.health = Math.Min(Math.Max(Game1.player.health, ceiling), Game1.player.health + heal);
            Hud($"Wounds tended: +{Game1.player.health - lastHealth} HP.", HUDMessage.achievement_type);
        }
        else if ((string?)msg["phase"] == "day")
        {
            // Morning after: Stardew's full heal is capped by what RimWorld says the body can recover.
            Game1.player.health = Math.Max(10, (int)(Game1.player.maxHealth * Math.Min(1.0, pct)));
            if (injuries.Count > 0) Hud($"Still hurting: {string.Join(", ", injuries.Take(3))}", HUDMessage.error_type);
        }
        ignoreHealthUntil = (int)Game1.ticks + 30;
        lastHealth = Game1.player.health;
    }

    /// <summary>Cheat: clear every injury in RimWorld, and restore health and energy here.</summary>
    internal void HealAllWounds()
    {
        if (!Context.IsWorldReady) return;
        Send(new JObject { ["type"] = "heal_all" });
        var p = Game1.player;
        p.health = p.maxHealth;
        p.Stamina = p.MaxStamina;
        p.buffs.Remove(InjuryBuffId);
        ignoreHealthUntil = (int)Game1.ticks + 60;
        lastHealth = p.health;
        Hud("All wounds healed.", HUDMessage.achievement_type);
    }

    /// <summary>Use medicine: Muscle Remedy tends well, Fiber works as a rough bandage.</summary>
    private void TendCommand()
    {
        float quality;
        if (Game1.player.Items.ContainsId("(O)351")) { Game1.player.Items.ReduceId("(O)351", 1); quality = 0.8f; }
        else if (Game1.player.Items.ContainsId("(O)771")) { Game1.player.Items.ReduceId("(O)771", 1); quality = 0.35f; }
        else { Monitor.Log("You need a Muscle Remedy (best) or Fiber (rough bandage).", LogLevel.Warn); return; }
        lastTendQuality = quality;
        Send(new JObject { ["type"] = "tend", ["quality"] = quality });
        Monitor.Log($"Tending wounds (quality {quality}).", LogLevel.Info);
    }
}
