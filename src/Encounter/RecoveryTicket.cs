namespace DiagnosticDamageProbe.Encounter;

internal sealed class RecoveryTicket
{
    internal long PlayerId { get; }
    internal double DeathTime { get; private set; }
    internal double ExpiryTime { get; private set; }

    internal RecoveryTicket(long playerId, double deathTime, double recoveryTimeout)
    { PlayerId = playerId; Refresh(deathTime, recoveryTimeout); }

    internal void Refresh(double deathTime, double recoveryTimeout)
    { DeathTime = deathTime; ExpiryTime = deathTime + recoveryTimeout; }

    internal void ApplyTimeout(double recoveryTimeout) => ExpiryTime = DeathTime + recoveryTimeout;
}
