# Lightweight 4C performance diagnostics

Instrumentation baseline: 40eb2d4ba9855f9f938f8ecc558a2475b7d145e2.
The uninstrumented runtime DLL is retained separately for an eventual A/B test.

## Activation and reporting

Performance diagnostics are **OFF by default** and require their own opt-in:

    [Diagnostics]
    EnablePerformanceDiagnosticLogging = false

Set this key to true on the host to collect and emit CombatMeterPerf through the
existing plugin logger. General, transport, lifecycle and magic diagnostics do not
enable performance instrumentation. Their defaults and behavior are unchanged.

The key can be toggled live. Enabling starts a fresh 60-second Stopwatch window,
excluding all disabled time and activity. Disabling immediately discards the partial
window without emitting a line. Re-enabling starts another clean window, even when
both changes happen between Updates. A session/world close emits a final partial
window with its actual duration only when performance diagnostics remain enabled.

Disabled hot paths bypass telemetry counters, timestamp reads, payload-size reads
for telemetry, report formatting and StringBuilder allocations. Only cheap setting
and window checks remain. Gameplay's existing monotonic clock is unaffected.
Clients do not allocate host telemetry state, even with the setting enabled.

The normal host Update path checks the window; there are no threads, Tasks or timer
callbacks. Counters and maxima reset after each report. Gameplay state is never
reset by telemetry. There is no per-event performance log.

Primitive counters and timing structs are allocated once when an enabled host
window is started (and again after re-enabling), not per event. Measurement uses
Stopwatch.GetTimestamp and integer arithmetic; formatting happens only at report
time. No sample history, percentile buffers or per-peer telemetry dictionaries exist.
Existing gameplay allocations, identity lookups, collections and algorithms remain
unchanged. All metric definitions below are unchanged.

## Exact metric boundaries

- damageObserved: host-local Observe inputs plus successfully decoded remote damage
  attempts, including duplicate/rejected attempts. Malformed packets are excluded.
  This is host-side input workload, not a count of observations on every client.
- damageAccepted: canonical deduplicated accepted callback, before attribution
  queuing. A delayed attribution event counts once on acceptance.
- damageIgnored: completed attributed events for which cluster Accept returns null
  (e.g. environmental policy or PvP). It is not a transport-rejection count, and
  accepted/ignored are not disjoint categories.
- commitCount/AvgUs/MaxUs: ProcessAttributed, including attribution resolution,
  existing diagnostic work, event-time conversion, legacy encounter and cluster
  processing. Deferred events count when actually processed. Queue wait time,
  provenance messages, wire decode, dedup/validation and ACK are outside this timer.
  Acceptance and processing can fall in different reporting windows.
- snapshotCycleCount/AvgUs/MaxUs: entire existing host publication call, including
  recipient processing, local apply, serialization, send calls and existing logs.
  The periodic performance report itself is outside the timer.
- identityCount/AvgUs/MaxUs and identityFailures: actual local/remote identity resolver
  calls, once per recipient, with null resolution counted as failure.
- snapshotBuildCount/AvgUs/MaxUs: existing ForPlayer builder, excluding identity
  resolution; includes both local and remote recipients. activeSnapshots means
  non-NoEncounter (including Recovery), emptySnapshots means NoEncounter.
- serializationCount/AvgUs/MaxUs: existing codec Encode plus ZPackage construction,
  performed once per remote recipient. There is no second serialization for telemetry.
- snapshotsSent/bytesSent/maxSnapshotBytes: successful remote snapshot RPC invocation
  count and its actual ZPackage.Size(). Local apply contributes no network bytes.
  bytesPerSecond divides payload bytes by actual window duration. This measures
  snapshot payload, not RPC framing, transport headers, retransmission or link usage.
- readyPeers: unique ready remote recipients from the latest publish cycle, excluding
  host and zero destinations. No additional peer scan is performed.
- activeClusters: current manager ActiveCount at report time, including Recovery.

Every timing struct retains count, total Stopwatch ticks and maximum ticks for the
window; reports convert totals to average microseconds and max to microseconds.
Zero-count averages are zero. Stopwatch measures synchronous elapsed duration,
not process CPU accounting or end-to-end packet latency; GC/scheduling pauses can
appear in maxima. Nested timings overlap and must not be summed as independent cost.
Existing logging cost is part of the baseline, so compare runs with the same flags.

## Verification and invariants

The previous baseline was 452 checks. Additional opt-in regressions cover disabled
hot paths with a counting synthetic clock, live toggles, partial close, independent
config binding and both enabled/disabled gameplay paths. Tests supply
synthetic ticks for arithmetic/report tests and inspect functional counters in real
adapter paths. No assertion depends on operation speed. Coverage includes reset,
invariant formatting, byte sizes, local exclusion, identity failures, shared
sequence, routing isolation, deferred acceptance and environmental/Recovery guards.

4C source selection, peer identity mapping, snapshot codec v1, RPC destinations,
publication interval, environmental policy, attribution, Recovery, DPS, UI and colors
are unchanged. No release metadata or frozen 0.11.2 package is modified.
