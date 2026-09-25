namespace DiagnosticDamageProbe.Snapshot;

// Uses Valheim's registered Player instances, never scene scans or identity guesses.
// Recomputed at publication time so missing entities/ownership changes cannot retain a stale route.
internal static class HostPlayerIdentity
{
    internal static long? ResolveLocal(long hostPeer)
    {
        Player player = Player.m_localPlayer;
        return TryObserve(player, hostPeer, out long playerId) ? playerId : (long?)null;
    }

    internal static long? ResolveRemote(long peer)
    {
        if (peer == 0) return null;
        long? resolved = null;
        foreach (Player player in Player.GetAllPlayers())
        {
            if (!TryObserve(player, peer, out long playerId)) continue;
            // Multiple different identities owned by this peer: fail closed, never choose by order.
            if (resolved.HasValue && resolved.Value != playerId) return null;
            resolved = playerId;
        }
        return resolved;
    }

    private static bool TryObserve(Player player, long owner, out long playerId)
    {
        playerId = 0;
        if (!player || owner == 0) return false;
        ZNetView view = player.GetComponent<ZNetView>();
        if (!view || !view.IsValid()) return false;
        ZDO zdo = view.GetZDO();
        if (zdo == null || zdo.GetOwner() != owner) return false;
        playerId = player.GetPlayerID();
        return playerId != 0;
    }
}
