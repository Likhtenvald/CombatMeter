using System;
using System.Globalization;
using System.Text.Json;
using DiagnosticDamageProbe;

internal static class Program
{
    private static int _passed;

    private static void Main()
    {
        Check("positive loss, attacker and input snapshot", () =>
        {
            var victim = new Character();
            var hit = PlayerHit(500);
            ApplyDamagePatch.Prefix(victim, hit, out var state);
            hit.m_damage.m_blunt = 30;
            victim.Hp = 87;
            ApplyDamagePatch.Postfix(victim, state);
            var e = Event(0);
            Equal(13f, e.GetProperty("EffectiveHpLoss").GetSingle());
            Equal(500f, e.GetProperty("HitTotalDamageBefore").GetSingle());
            Equal("Player", e.GetProperty("AttackerClass").GetString());
            Equal("9223372036854775806", e.GetProperty("AttackerPlayerID").GetString());
            Equal("101", e.GetProperty("VictimOwner").GetString());
            Equal(true, e.GetProperty("IsOwner").GetBoolean());
            Equal(1, Plugin.ProbeLog.Events.Count);
            Equal(1, Plugin.PublishedLosses.Count);
            Equal(13f, Plugin.PublishedLosses[0]);
        });
        Check("lethal overkill logs remaining HP", () =>
        {
            var victim = new Character { Hp = 7 };
            ApplyDamagePatch.Prefix(victim, PlayerHit(900), out var state);
            victim.Hp = 0;
            ApplyDamagePatch.Postfix(victim, state);
            Equal(7f, Event(0).GetProperty("EffectiveHpLoss").GetSingle());
            Equal(7f, Plugin.PublishedLosses[0]);
        });
        Check("nested calls on same victim have independent snapshots", () =>
        {
            var victim = new Character();
            ApplyDamagePatch.Prefix(victim, PlayerHit(10), out var outer);
            victim.Hp = 90;
            var dot = new HitData { m_hitType = HitData.HitType.Burning };
            ApplyDamagePatch.Prefix(victim, dot, out var inner);
            victim.Hp = 70;
            ApplyDamagePatch.Postfix(victim, inner);
            victim.Hp = 65;
            ApplyDamagePatch.Postfix(victim, outer);
            Equal(2, Plugin.ProbeLog.Events.Count);
            Equal(20f, Event(0).GetProperty("EffectiveHpLoss").GetSingle());
            Equal(35f, Event(1).GetProperty("EffectiveHpLoss").GetSingle());
            Equal("None", Event(0).GetProperty("AttackerClass").GetString());
            Equal("Player", Event(1).GetProperty("AttackerClass").GetString());
            if (Event(0).GetProperty("CallId").GetString() == Event(1).GetProperty("CallId").GetString())
                throw new Exception("Nested call IDs were mixed");
        });
        Check("none, unresolved and NPC are distinct", () =>
        {
            RunLoss(new HitData { m_hitType = HitData.HitType.Poisoned });
            RunLoss(new HitData { m_attacker = new ZDOID("missing:42") });
            RunLoss(new HitData { m_attacker = new ZDOID("npc:42"), Attacker = new Character() });
            Equal("None", Event(0).GetProperty("AttackerClass").GetString());
            Equal("Unresolved", Event(1).GetProperty("AttackerClass").GetString());
            Equal("NPC", Event(2).GetProperty("AttackerClass").GetString());
            Equal("null", Event(1).GetProperty("GetAttackerResult").GetString());
        });
        Check("every damage component survives the input snapshot", () =>
        {
            RunLoss(new HitData
            {
                m_damage = new HitData.DamageTypes
                {
                    m_damage = 1, m_blunt = 2, m_slash = 3, m_pierce = 4,
                    m_chop = 5, m_pickaxe = 6, m_fire = 7, m_frost = 8,
                    m_lightning = 9, m_poison = 10, m_spirit = 11, m_nonPlayer = 12
                }
            });
            string[] names = { "Generic", "Blunt", "Slash", "Pierce", "Chop", "Pickaxe",
                "Fire", "Frost", "Lightning", "Poison", "Spirit", "NonPlayer" };
            for (int i = 0; i < names.Length; i++)
                Equal((float)(i + 1), Event(0).GetProperty(names[i] + "Before").GetSingle());
            Equal(78f, Event(0).GetProperty("HitTotalDamageBefore").GetSingle());
        });
        Check("zero loss and healing do not log", () =>
        {
            var victim = new Character();
            ApplyDamagePatch.Prefix(victim, PlayerHit(1), out var unchanged);
            ApplyDamagePatch.Postfix(victim, unchanged);
            ApplyDamagePatch.Prefix(victim, PlayerHit(1), out var healed);
            victim.Hp = 110;
            ApplyDamagePatch.Postfix(victim, healed);
            Equal(0, Plugin.ProbeLog.Events.Count);
        });
        Check("invalid, absent and remote-owned targets are excluded", () =>
        {
            var victim = new Character();
            victim.View.Zdo.Owner = 202;
            ApplyDamagePatch.Prefix(victim, PlayerHit(1), out var remote);
            victim.View.Valid = false;
            ApplyDamagePatch.Prefix(victim, PlayerHit(1), out var invalid);
            victim.View = null;
            ApplyDamagePatch.Prefix(victim, PlayerHit(1), out var missingView);
            ApplyDamagePatch.Prefix(null, PlayerHit(1), out var missingVictim);
            Equal<ApplyDamagePatch.CallState>(null, remote);
            Equal<ApplyDamagePatch.CallState>(null, invalid);
            Equal<ApplyDamagePatch.CallState>(null, missingView);
            Equal<ApplyDamagePatch.CallState>(null, missingVictim);
        });
        Check("disabled probe logs do not disable commit observation", () =>
        {
            Plugin.LoggingEnabled = false;
            ApplyDamagePatch.Prefix(new Character(), PlayerHit(1), out var state);
            if (state == null) throw new Exception("Logging disabled observation");
            RunLoss(PlayerHit(10));
            Equal(0, Plugin.ProbeLog.Events.Count);
            Equal(1, Plugin.Published.Count);
        });
        Check("lost ownership suppresses uncertain observation", () =>
        {
            var victim = new Character();
            ApplyDamagePatch.Prefix(victim, PlayerHit(10), out var state);
            victim.Hp = 90;
            victim.View.Zdo.Owner = 202;
            ApplyDamagePatch.Postfix(victim, state);
            Equal(0, Plugin.ProbeLog.Events.Count);
            Equal(1, Plugin.Warnings.Count);
        });
        Check("destruction suppresses fallback HP observation", () =>
        {
            var victim = new Character();
            ApplyDamagePatch.Prefix(victim, PlayerHit(10), out var state);
            victim.Destroyed = true;
            ApplyDamagePatch.Postfix(victim, state);
            Equal(0, Plugin.ProbeLog.Events.Count);
            Equal(1, Plugin.Warnings.Count);
        });
        Check("nonfinite HP is not a damage event", () =>
        {
            var victim = new Character();
            ApplyDamagePatch.Prefix(victim, PlayerHit(10), out var state);
            victim.Hp = float.NaN;
            ApplyDamagePatch.Postfix(victim, state);
            Equal(0, Plugin.ProbeLog.Events.Count);
            Equal(1, Plugin.Warnings.Count);
        });
        Check("abandoned invocation has no shared state to leak", () =>
        {
            ApplyDamagePatch.Prefix(new Character(), PlayerHit(800), out var abandoned);
            // Model an original that threw: no Postfix for that invocation.
            RunLoss(new HitData { m_hitType = HitData.HitType.Fall });
            Equal(1, Plugin.ProbeLog.Events.Count);
            Equal("None", Event(0).GetProperty("AttackerClass").GetString());
            Equal(10f, Event(0).GetProperty("EffectiveHpLoss").GetSingle());
        });
        Check("capture errors do not escape into gameplay", () =>
        {
            ApplyDamagePatch.Prefix(new Character { ThrowOnHealth = true }, PlayerHit(1), out var state);
            Equal<ApplyDamagePatch.CallState>(null, state);
            Equal(1, Plugin.Warnings.Count);
            RunLoss(PlayerHit(1));
            Equal(1, Plugin.ProbeLog.Events.Count);
        });
        Check("roles distinguish single-player, host, dedicated and client", () =>
        {
            RunLoss(PlayerHit(1));
            ZNet.instance.SinglePlayer = false;
            RunLoss(PlayerHit(1));
            ZNet.instance.Dedicated = true;
            RunLoss(PlayerHit(1));
            ZNet.instance.Dedicated = false;
            ZNet.instance.Server = false;
            RunLoss(PlayerHit(1));
            Equal("SinglePlayer", Event(0).GetProperty("Role").GetString());
            Equal("ListenHost", Event(1).GetProperty("Role").GetString());
            Equal("DedicatedServer", Event(2).GetProperty("Role").GetString());
            Equal("Client", Event(3).GetProperty("Role").GetString());
        });
        Check("JSON is single-line and round-trips under Russian culture", () =>
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
                var hit = PlayerHit(2.75f);
                ((Player)hit.Attacker).PlayerName = "Имя\"\\\n\t";
                RunLoss(hit);
                Equal(2.75f, Event(0).GetProperty("HitTotalDamageBefore").GetSingle());
                Equal("Имя\"\\\n\t", Event(0).GetProperty("AttackerPlayerName").GetString());
                if (Plugin.ProbeLog.Events[0].Contains('\n')) throw new Exception("Multiline log");
            }
            finally { CultureInfo.CurrentCulture = old; }
        });
        _passed += TransportChecks.Run();
        _passed += StatisticsChecks.Run();
        _passed += EncounterChecks.Run();
        _passed += EncounterSettingsChecks.Run();
        _passed += ConfigurationChecks.Run();
        _passed += DpsActivityChecks.Run();
        _passed += RecoveryTicketChecks.Run();
        _passed += SnapshotChecks.Run();
        _passed += UiPresentationChecks.Run();
        _passed += UiLayoutChecks.Run();
        _passed += UiEditModeChecks.Run();
        _passed += UiContributionChecks.Run();
        _passed += PlayerColorChecks.Run();
        _passed += CombatClusterChecks.Run();
        _passed += PlayerClusterRoutingChecks.Run();
        _passed += ClusterTransportChecks.Run();
        _passed += LifecycleIntegrationChecks.Run();
        _passed += AttributionChecks.Run();
        Console.WriteLine($"PASS: {_passed} managed checks (observer, protocol, delivery, adapter doubles). Unity/Harmony runtime not exercised.");
    }

    private static void Check(string name, Action action)
    {
        Plugin.LoggingEnabled = true;
        Plugin.ProbeLog.Events.Clear();
        Plugin.Warnings.Clear();
        Plugin.Published.Clear();
        Plugin.PublishedLosses.Clear();
        ZDOMan.Session = 101;
        ZNet.instance = new ZNet();
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static HitData PlayerHit(float damage) => new HitData
    {
        m_damage = new HitData.DamageTypes { m_blunt = damage },
        m_attacker = new ZDOID("attacker:2"), Attacker = new Player()
    };

    private static void RunLoss(HitData hit)
    {
        var victim = new Character();
        ApplyDamagePatch.Prefix(victim, hit, out var state);
        victim.Hp -= 10;
        ApplyDamagePatch.Postfix(victim, state);
    }

    private static JsonElement Event(int index)
    {
        using var doc = JsonDocument.Parse(Plugin.ProbeLog.Events[index].Substring("DamageProbeEvent ".Length));
        return doc.RootElement.Clone();
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}");
    }
}
