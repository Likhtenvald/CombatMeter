using System;

namespace DiagnosticDamageProbe.Encounter;

internal sealed class EncounterSettings
{
    internal const double DefaultSoftTimeoutSeconds = 20d;
    internal const double DefaultRecoveryTimeoutSeconds = 180d;
    internal const double DefaultDpsIdleTimeoutSeconds = 6d;
    internal const double MinSoftTimeoutSeconds = 5d;
    internal const double MaxSoftTimeoutSeconds = 60d;
    internal const double MinRecoveryTimeoutSeconds = 30d;
    internal const double MaxRecoveryTimeoutSeconds = 600d;
    internal const double MinDpsIdleTimeoutSeconds = 1d;
    internal const double MaxDpsIdleTimeoutSeconds = 20d;

    internal double SoftTimeoutSeconds { get; private set; }
    internal double RecoveryTimeoutSeconds { get; private set; }
    internal double DpsIdleTimeoutSeconds { get; private set; }

    internal EncounterSettings(double softTimeout = DefaultSoftTimeoutSeconds,
        double recoveryTimeout = DefaultRecoveryTimeoutSeconds,
        double dpsIdleTimeout = DefaultDpsIdleTimeoutSeconds)
    { Update(softTimeout, recoveryTimeout, dpsIdleTimeout); }

    internal bool Update(double softTimeout, double recoveryTimeout)
        => Update(softTimeout, recoveryTimeout, DpsIdleTimeoutSeconds > 0d ? DpsIdleTimeoutSeconds : DefaultDpsIdleTimeoutSeconds);

    internal bool Update(double softTimeout, double recoveryTimeout, double dpsIdleTimeout)
    {
        double soft = Sanitize(softTimeout, DefaultSoftTimeoutSeconds, MinSoftTimeoutSeconds, MaxSoftTimeoutSeconds);
        double recovery = Sanitize(recoveryTimeout, DefaultRecoveryTimeoutSeconds, MinRecoveryTimeoutSeconds, MaxRecoveryTimeoutSeconds);
        double dps = Sanitize(dpsIdleTimeout, DefaultDpsIdleTimeoutSeconds, MinDpsIdleTimeoutSeconds, MaxDpsIdleTimeoutSeconds);
        bool changed = soft != SoftTimeoutSeconds || recovery != RecoveryTimeoutSeconds || dps != DpsIdleTimeoutSeconds;
        SoftTimeoutSeconds = soft; RecoveryTimeoutSeconds = recovery; DpsIdleTimeoutSeconds = dps; return changed;
    }

    private static double Sanitize(double value, double fallback, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d) return fallback;
        return Math.Max(min, Math.Min(max, value));
    }
}
