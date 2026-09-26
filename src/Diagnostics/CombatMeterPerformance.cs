using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DiagnosticDamageProbe.Diagnostics;

internal struct TimingCounter
{
    internal long Count, TotalTicks, MaxTicks;
    internal void Record(long ticks)
    {
        ticks = Math.Max(0, ticks);
        Count++; TotalTicks += ticks; MaxTicks = Math.Max(MaxTicks, ticks);
    }
    internal double AverageMicroseconds(long frequency) => Count == 0 ? 0d : TotalTicks * (1000000d / frequency) / Count;
    internal double MaxMicroseconds(long frequency) => MaxTicks * (1000000d / frequency);
}

// Host-session diagnostics only. No samples, history, per-peer state or event allocations.
internal sealed class CombatMeterPerformance
{
    internal const double IntervalSeconds = 60d;
    private readonly long _frequency;
    private long _windowStart;
    internal long DamageObserved, DamageAccepted, DamageIgnored;
    internal TimingCounter CommitProcessing, SnapshotCycle, IdentityResolution, SnapshotBuild, Serialization;
    internal long IdentityResolutionFailures, EmptySnapshotsBuilt, ActiveSnapshotsBuilt;
    internal long SnapshotsSent, SnapshotBytesSent, SnapshotMaxBytes;
    internal int ReadyPeers; // Last publication's ready remote recipients, not an extra scan.

    internal CombatMeterPerformance(long start, long frequency = 0)
    { _windowStart = start; _frequency = frequency > 0 ? frequency : Stopwatch.Frequency; }
    internal void RecordIdentity(long ticks, bool resolved)
    { IdentityResolution.Record(ticks); if (!resolved) IdentityResolutionFailures++; }
    internal void RecordBuild(long ticks, bool empty)
    { SnapshotBuild.Record(ticks); if (empty) EmptySnapshotsBuilt++; else ActiveSnapshotsBuilt++; }
    internal void RecordSend(int packageBytes)
    { SnapshotsSent++; SnapshotBytesSent += packageBytes; SnapshotMaxBytes = Math.Max(SnapshotMaxBytes, packageBytes); }

    // Called from the existing host Update path, and once on close for the partial window.
    // The transport bypasses this entirely when disabled and discards its window on toggle.
    internal string TryReport(long now, bool enabled, int activeClusters, bool force = false)
    {
        double seconds = (now - _windowStart) / (double)_frequency;
        if (seconds <= 0 || (!force && seconds < IntervalSeconds)) return null;
        string report = null;
        if (enabled)
        {
            var text = new StringBuilder(768);
            text.AppendFormat(CultureInfo.InvariantCulture,
                "CombatMeterPerf windowSeconds={0:F3} damageObserved={1} damageAccepted={2} damageIgnored={3}",
                seconds, DamageObserved, DamageAccepted, DamageIgnored);
            AppendTiming(text, "commit", CommitProcessing);
            AppendTiming(text, "snapshotCycle", SnapshotCycle);
            AppendTiming(text, "identity", IdentityResolution);
            text.AppendFormat(CultureInfo.InvariantCulture, " identityFailures={0} activeSnapshots={1} emptySnapshots={2}",
                IdentityResolutionFailures, ActiveSnapshotsBuilt, EmptySnapshotsBuilt);
            AppendTiming(text, "snapshotBuild", SnapshotBuild);
            AppendTiming(text, "serialization", Serialization);
            text.AppendFormat(CultureInfo.InvariantCulture,
                " snapshotsSent={0} bytesSent={1} bytesPerSecond={2:F3} maxSnapshotBytes={3} readyPeers={4} activeClusters={5}",
                SnapshotsSent, SnapshotBytesSent, SnapshotBytesSent / seconds, SnapshotMaxBytes, ReadyPeers, activeClusters);
            report = text.ToString();
        }
        DamageObserved = DamageAccepted = DamageIgnored = 0;
        CommitProcessing = SnapshotCycle = IdentityResolution = SnapshotBuild = Serialization = default;
        IdentityResolutionFailures = EmptySnapshotsBuilt = ActiveSnapshotsBuilt = 0;
        SnapshotsSent = SnapshotBytesSent = SnapshotMaxBytes = 0;
        _windowStart = now;
        return report;
    }
    private void AppendTiming(StringBuilder text, string name, TimingCounter timing) => text.AppendFormat(CultureInfo.InvariantCulture,
        " {0}Count={1} {0}AvgUs={2:F3} {0}MaxUs={3:F3}", name, timing.Count, timing.AverageMicroseconds(_frequency), timing.MaxMicroseconds(_frequency));
}
