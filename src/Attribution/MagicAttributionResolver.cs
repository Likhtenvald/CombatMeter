using System.Collections.Generic;
using DiagnosticDamageProbe.Transport;

namespace DiagnosticDamageProbe.Attribution;

internal sealed class MagicAttributionResolver
{
    private readonly Dictionary<string, PoisonAttributionState> _poison = new Dictionary<string, PoisonAttributionState>();
    private readonly Dictionary<string, SharedDotContributionLedger> _ledgers = new Dictionary<string, SharedDotContributionLedger>();
    internal SummonProvenanceRegistry Summons { get; } = new SummonProvenanceRegistry();

    internal long? ResolveSource(AttackerClass attacker, long? playerId, string attackerZdoId)
    {
        if (attacker == AttackerClass.Player && playerId.HasValue) return playerId;
        return attacker == AttackerClass.NPC && Summons.TryResolve(attackerZdoId, out long owner) ? owner : (long?)null;
    }

    internal void ApplyPoolUpdate(string victimZdoId, DotKind kind, float before, float after,
        AttackerClass attacker, long? playerId, string attackerZdoId, bool updateAccepted = true)
    {
        if (string.IsNullOrEmpty(victimZdoId) || !updateAccepted) return;
        long? source = ResolveSource(attacker, playerId, attackerZdoId);
        string key = Key(victimZdoId, kind);
        if (kind == DotKind.Poison)
        {
            if (!_poison.TryGetValue(key, out PoisonAttributionState state)) _poison[key] = state = new PoisonAttributionState();
            state.PoolChanged(source);
        }
        else
        {
            if (!(after > before)) return;
            if (before <= 0f || !_ledgers.TryGetValue(key, out SharedDotContributionLedger ledger))
                _ledgers[key] = ledger = new SharedDotContributionLedger();
            ledger.Add(source, after - before);
        }
    }

    internal AttributedDamageEvent Resolve(DamageCommit commit)
    {
        DamageFacts f = commit.Facts;
        if (f.Attacker == AttackerClass.Player) return new AttributedDamageEvent(commit, null, true);
        if (f.Attacker == AttackerClass.NPC && !string.IsNullOrEmpty(f.AttackerZdoId) && Summons.TryResolve(f.AttackerZdoId, out long summoner))
            return PlayerSource(commit, summoner);

        DotKind? kind = DotKindFrom(commit);
        if (kind.HasValue)
        {
            string key = Key(f.VictimZdoId, kind.Value);
            if (kind == DotKind.Poison && _poison.TryGetValue(key, out PoisonAttributionState poison) && poison.OwnerPlayerId.HasValue)
                return PlayerSource(commit, poison.OwnerPlayerId.Value);
            if (kind != DotKind.Poison && _ledgers.TryGetValue(key, out SharedDotContributionLedger ledger))
                return new AttributedDamageEvent(commit, ledger.Distribute(commit.EffectiveHpLoss), false);
        }
        return new AttributedDamageEvent(commit, null, true);
    }

    internal bool HasDotState(string victimZdoId, DotKind kind)
    {
        string key = Key(victimZdoId, kind);
        return kind == DotKind.Poison ? _poison.ContainsKey(key) : _ledgers.ContainsKey(key);
    }

    internal void Reset() { Summons.Reset(); _poison.Clear(); _ledgers.Clear(); }

    private static AttributedDamageEvent PlayerSource(DamageCommit commit, long playerId)
    {
        if (commit.Facts.VictimIsPlayer) return new AttributedDamageEvent(commit, null, false);
        return new AttributedDamageEvent(commit, new List<DamagePortion> { new DamagePortion(playerId, commit.EffectiveHpLoss) }, false);
    }
    private static DotKind? DotKindFrom(DamageCommit c)
    {
        return c.Facts.DotKind == 1 ? DotKind.Poison : c.Facts.DotKind == 2 ? DotKind.Burning :
            c.Facts.DotKind == 3 ? DotKind.Spirit : (DotKind?)null;
    }
    private static string Key(string victim, DotKind kind) => victim + "/" + (byte)kind;
}
