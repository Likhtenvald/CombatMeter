using System;
using System.Collections.Generic;

namespace DiagnosticDamageProbe.Snapshot;

// Vanilla peer identity is trusted at the cooperative-game boundary. Never inferred from combat.
internal sealed class SessionPlayerIdentity
{
    private sealed class Binding
    {
        internal ZNetPeer Peer;
        internal long PlayerId, CurrentId;
        internal bool FromCharacter;
        internal bool Seen, Conflict;
        internal string Status;
    }
    private readonly Dictionary<long, Binding> _bindings = new Dictionary<long, Binding>();
    private readonly Dictionary<long, Binding> _players = new Dictionary<long, Binding>();
    private readonly List<long> _removed = new List<long>();
    private readonly List<Player> _observed = new List<Player>();
    private readonly Action<string> _log;

    internal SessionPlayerIdentity(Action<string> log)
    {
        _log = log;
        // One bootstrap scan per host session; subsequent changes arrive through lifecycle notifications.
        foreach (Player player in Player.GetAllPlayers()) Observe(player);
    }
    internal void Observe(Player player)
    {
        if (player && !_observed.Contains(player)) _observed.Add(player);
    }
    internal void Forget(Player player) => _observed.Remove(player);

    internal void Sync(List<ZNetPeer> peers, long hostPeer, long? localPlayerId)
    {
        foreach (Binding b in _bindings.Values) b.Seen = false;
        foreach (ZNetPeer peer in peers)
        {
            if (peer == null || !peer.IsReady() || peer.m_uid == 0 || peer.m_uid == hostPeer) continue;
            if (_bindings.TryGetValue(peer.m_uid, out Binding b) && !ReferenceEquals(b.Peer, peer))
            {
                if (b.Seen) { Reject(b, "DuplicatePeer"); continue; }
                Status(b, "CombatIdentityInvalidated", "ConnectionReplaced");
                _bindings.Remove(peer.m_uid);
                b = null;
            }
            if (b == null)
            {
                b = new Binding { Peer = peer };
                _bindings.Add(peer.m_uid, b);
            }
            b.Seen = true;
            b.CurrentId = ReadVanillaIdentity(b);
            if (b.CurrentId != 0)
            {
                if (b.PlayerId == 0) b.PlayerId = b.CurrentId;
                else if (b.PlayerId != b.CurrentId) Reject(b, "PlayerIdChanged");
            }
        }
        _removed.Clear();
        foreach (var pair in _bindings)
            if (!pair.Value.Seen) { Status(pair.Value, "CombatIdentityInvalidated", "Disconnected"); _removed.Add(pair.Key); }
        foreach (long id in _removed) _bindings.Remove(id);
        _players.Clear();
        foreach (Binding b in _bindings.Values)
        {
            if (b.PlayerId == 0 || b.CurrentId == 0) continue;
            if (localPlayerId == b.PlayerId) Reject(b, "LocalPlayerCollision");
            if (_players.TryGetValue(b.PlayerId, out Binding other))
            { Reject(b, "DuplicatePlayerId"); Reject(other, "DuplicatePlayerId"); }
            else _players.Add(b.PlayerId, b);
        }
        // Audit only lifecycle-tracked entities once per publication, not once per recipient.
        // Entities never establish identity. Their absence cannot revoke the vanilla binding.
        for (int i = _observed.Count - 1; i >= 0; i--)
        {
            Player player = _observed[i];
            if (!player) { _observed.RemoveAt(i); continue; }
            ZNetView view = player.GetComponent<ZNetView>();
            if (!view || !view.IsValid()) continue;
            ZDO zdo = view.GetZDO();
            if (zdo == null || !_bindings.TryGetValue(zdo.GetOwner(), out Binding b)) continue;
            long observed = player.GetPlayerID();
            if (observed == 0) b.CurrentId = 0;
            else if (b.PlayerId != 0 && observed != b.PlayerId) Reject(b, "ObservedPlayerMismatch");
        }
        foreach (Binding b in _bindings.Values)
        {
            if (b.Conflict) continue;
            if (b.PlayerId == 0 || b.CurrentId == 0) Status(b, "CombatSnapshotIdentityUnresolved", "ZeroPlayerId");
            else Status(b, "CombatIdentityBound", "VanillaPeer");
        }
    }
    private long ReadVanillaIdentity(Binding b)
    {
        ZNetPeer peer = b.Peer;
        ZDO character = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
        if (character != null)
        {
            long id = character.GetLong(ZDOVars.s_playerID, 0L);
            if (character.GetOwner() != peer.m_uid)
            { Reject(b, "InvalidCharacterOwner"); return 0; }
            if (id == 0) return 0; // Registration/replication may still be initializing.
            if (peer.m_playerID != 0 && peer.m_playerID != id)
            { Reject(b, "VanillaIdentityMismatch"); return 0; }
            b.FromCharacter = true;
            return id;
        }
        if (peer.m_playerID != 0) return peer.m_playerID;
        // A verified character may disappear during death/respawn or replication gaps.
        // New character data must confirm the same ID before it can replace that observation.
        return b.FromCharacter ? b.PlayerId : 0;
    }

    internal long? Resolve(long peer)
    {
        if (!_bindings.TryGetValue(peer, out Binding b) || !b.Seen || b.Conflict ||
            !b.Peer.IsReady() || b.Peer.m_uid != peer || b.PlayerId == 0 || b.CurrentId != b.PlayerId) return null;
        return b.PlayerId;
    }
    private void Reject(Binding b, string reason)
    {
        if (b.Conflict) return;
        b.Conflict = true; // Quarantined until reconnect; never silently switch identities.
        Status(b, "CombatIdentityRejected", reason);
    }
    private void Status(Binding b, string marker, string reason)
    {
        if (b.Status == reason) return;
        b.Status = reason;
        _log?.Invoke(marker + " peer=" + b.Peer.m_uid + " player=" + b.PlayerId + " reason=" + reason);
    }
}
