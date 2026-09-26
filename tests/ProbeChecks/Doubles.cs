// Minimal managed test doubles. These are not Valheim or Harmony runtime implementations.
using System;
using System.Collections.Generic;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type target, string method, Type[] arguments) { }
    }
    internal sealed class HarmonyPrefix : Attribute { }
    internal sealed class HarmonyPostfix : Attribute { }
}

namespace DiagnosticDamageProbe
{
    internal static class MagicAttributionProbe
    {
        internal static void ObserveDotTick(Character victim, HitData hit) { }
        internal static void ObserveSummonHit(Character attacker) { }
    }

    internal static class Plugin
    {
        internal static bool LoggingEnabled = true;
        internal static readonly string ProcessRunId = "test-process";
        internal static readonly TestLog ProbeLog = new TestLog();
        internal static readonly List<string> Warnings = new List<string>();
        internal static readonly List<string> TransportMessages = new List<string>();
        internal static void TransportLog(string message) => TransportMessages.Add(message);
        internal static void MagicLog(string message) => TransportMessages.Add(message);
        internal static void EncounterLog(string message) => TransportMessages.Add(message);
        internal static void SnapshotLog(string message) => TransportMessages.Add(message);
        internal static readonly List<DiagnosticDamageProbe.Transport.DamageFacts> Published = new List<DiagnosticDamageProbe.Transport.DamageFacts>();
        internal static readonly List<float> PublishedLosses = new List<float>();
        internal static void Publish(long peer, DiagnosticDamageProbe.Transport.DamageFacts facts, float loss)
        { Published.Add(facts); PublishedLosses.Add(loss); }
        internal static void Warn(string message) => Warnings.Add(message);
    }
    internal sealed class TestLog
    {
        internal readonly List<string> Events = new List<string>();
        internal void LogInfo(string message) => Events.Add(message);
    }
}

internal class EngineObject
{
    internal bool Destroyed;
    public static implicit operator bool(EngineObject value) => value != null && !value.Destroyed;
}

internal struct ZDOID
{
    internal long UserID => IsNone() ? 0 : 10;
    internal uint ID => IsNone() ? 0u : 1u;
    private string _id;
    internal ZDOID(string id) { _id = id; }
    internal bool IsNone() => _id == null;
    public override string ToString() => _id ?? "0:0";
}

internal sealed class ZDO
{
    internal ZDOID m_uid = new ZDOID("victim:1");
    internal long Owner = 101;
    internal long GetOwner() => Owner;
}

internal sealed class ZNetView : EngineObject
{
    internal bool Valid = true;
    internal ZDO Zdo = new ZDO();
    internal bool IsValid() => Valid && Zdo != null;
    internal bool IsOwner() => Zdo?.Owner == ZDOMan.Session;
    internal ZDO GetZDO() => Zdo;
}

internal sealed class ZDOMan
{
    internal static ZDOMan instance = new ZDOMan();
    internal static long Session = 101;
    internal static long GetSessionID() => Session;
}

internal sealed class ZNet : EngineObject
{
    internal static ZNet instance = new ZNet();
    internal bool Server = true;
    internal bool Dedicated;
    internal bool SinglePlayer = true;
    internal bool IsServer() => Server;
    internal bool IsDedicated() => Dedicated;
    internal static bool IsSinglePlayer => instance.SinglePlayer;
    internal readonly List<ZNetPeer> Peers = new List<ZNetPeer>();
    internal List<ZNetPeer> GetPeers() => Peers;
    internal ZNetPeer ServerPeer;
    internal ZNetPeer GetServerPeer() => ServerPeer;
}

internal sealed class ZNetPeer
{
    internal long m_uid;
    internal bool Ready = true;
    internal bool IsReady() => Ready && m_uid != 0;
}

internal sealed class ZPackage
{
    private readonly byte[] _data;
    internal ZPackage(byte[] data) { _data = data; }
    internal int Size() => _data.Length;
    internal byte[] GetArray() => _data;
}

internal sealed class ZRoutedRpc
{
    internal static ZRoutedRpc instance;
    internal readonly Dictionary<string, Action<long, ZPackage>> Handlers = new Dictionary<string, Action<long, ZPackage>>();
    internal Action<long, string, ZPackage> Send;
    internal void Register<T>(string name, Action<long, T> callback) => Handlers.Add(name, (sender, pkg) => callback(sender, (T)(object)pkg));
    internal void InvokeRoutedRPC(long target, string name, params object[] args)
    {
        Send?.Invoke(target, name, (ZPackage)args[0]);
    }
    internal const long Everybody = 0;
}

internal class Character : EngineObject
{
    internal ZNetView View = new ZNetView();
    internal float Hp = 100;
    internal bool ThrowOnHealth;
    internal string m_name = "$enemy_test";
    internal string name = "Test(Clone)";
    internal T GetComponent<T>() where T : class => View as T;
    internal float GetHealth() => ThrowOnHealth ? throw new InvalidOperationException() : Hp;
    internal bool IsPlayer() => this is Player;
    public void ApplyDamage(HitData hit, bool showDamageText, bool triggerEffects, HitData.DamageModifier mod) { }
}

internal sealed class Player : Character
{
    internal static Player m_localPlayer;
    internal static readonly List<Player> Instances = new List<Player>();
    internal static List<Player> GetAllPlayers() => Instances;
    internal long PlayerId = 9223372036854775806;
    internal string PlayerName = "Tester";
    internal long GetPlayerID() => PlayerId;
    internal string GetPlayerName() => PlayerName;
}

internal sealed class HitData
{
    internal enum DamageModifier { Normal }
    // Exact values from the referenced Valheim HitData.HitType byte enum.
    internal enum HitType : byte { Undefined = 0, EnemyHit = 1, PlayerHit = 2, Fall = 3, Drowning = 4, Burning = 5, Poisoned = 7, Smoke = 9, Tree = 13, Incinerator = 23 }
    internal struct DamageTypes
    {
        internal float m_damage, m_blunt, m_slash, m_pierce, m_chop, m_pickaxe;
        internal float m_fire, m_frost, m_lightning, m_poison, m_spirit, m_nonPlayer;
    }
    internal DamageTypes m_damage;
    internal HitType m_hitType;
    internal ZDOID m_attacker;
    internal Character Attacker;
    internal Character GetAttacker() => Attacker;
    internal float GetTotalDamage() => m_damage.m_damage + m_damage.m_blunt + m_damage.m_slash +
        m_damage.m_pierce + m_damage.m_chop + m_damage.m_pickaxe + m_damage.m_fire +
        m_damage.m_frost + m_damage.m_lightning + m_damage.m_poison + m_damage.m_spirit + m_damage.m_nonPlayer;
}
