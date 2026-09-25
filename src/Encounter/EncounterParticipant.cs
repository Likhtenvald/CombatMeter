namespace DiagnosticDamageProbe.Encounter;

internal sealed class EncounterParticipant
{
    internal long PlayerId { get; }
    internal bool HasParticipated { get; private set; }
    internal bool IsAlive { get; private set; } = true;
    private string _lastDeathIncarnation;

    internal EncounterParticipant(long playerId)
    {
        PlayerId = playerId;
        HasParticipated = true;
    }

    internal bool Died(string incarnation)
    {
        if (!string.IsNullOrEmpty(incarnation))
        {
            if (_lastDeathIncarnation == incarnation) return false;
            _lastDeathIncarnation = incarnation;
        }
        else if (!IsAlive) return false;
        IsAlive = false;
        return true;
    }

    internal void Respawned() => IsAlive = true;
}
