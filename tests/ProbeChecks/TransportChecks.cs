using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Transport;

internal static class TransportChecks
{
    private static readonly Guid Epoch = new Guid("01234567-89ab-cdef-0123-456789abcdef");
    private static int _passed;
    private static DamageFacts Facts => new DamageFacts(111, 3, true, 888, AttackerClass.Player, 777, 1, "Игрок", "Лучник");
    private static DamageCommit Commit(long sequence = 1, float loss = 10, long peer = 11, Guid? epoch = null) =>
        new DamageCommit(new EventId(peer, epoch ?? Epoch, sequence), Facts, loss, DateTime.UtcNow.Ticks);

    internal static int Run()
    {
        Test("positive commit accepted", () => Eq(Acceptance.Accepted, new CommitAcceptor().Process(Commit(), 11, CommitOrigin.Remote)));
        foreach (float value in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            float loss = value;
            Test("invalid loss rejected: " + loss, () => Eq(Acceptance.InvalidDamage, new CommitAcceptor().Process(Commit(loss: loss), 11, CommitOrigin.Remote)));
        }
        Test("duplicate rejected, identical hits with different IDs accepted", () =>
        {
            var a = new CommitAcceptor();
            Eq(Acceptance.Accepted, a.Process(Commit(), 11, CommitOrigin.Remote));
            Eq(Acceptance.Duplicate, a.Process(Commit(), 11, CommitOrigin.Local));
            Eq(Acceptance.Accepted, a.Process(Commit(2), 11, CommitOrigin.Remote));
        });
        Test("invalid IDs and source mismatch do not consume dedup slots", () =>
        {
            var a = new CommitAcceptor();
            Eq(Acceptance.InvalidEvent, a.Process(Commit(0), 11, CommitOrigin.Remote));
            Eq(Acceptance.InvalidEvent, a.Process(Commit(epoch: Guid.Empty), 11, CommitOrigin.Remote));
            Eq(Acceptance.InvalidEvent, a.Process(Commit(peer: 0), 0, CommitOrigin.Remote));
            Eq(Acceptance.SenderMismatch, a.Process(Commit(), 12, CommitOrigin.Remote));
            Eq(0, a.SourceCount);
        });
        Test("sequence uniqueness across reconnect with reused peer ID", () =>
        {
            var generator = new EventSequence(11, Epoch);
            var ids = new HashSet<EventId>();
            for (int i = 1; i <= 10000; i++)
            { EventId id = generator.Next(); Eq((long)i, id.Sequence); True(ids.Add(id)); }
            var nextSession = new EventSequence(11, Guid.NewGuid());
            EventId next = nextSession.Next(); Eq(1L, next.Sequence); True(ids.Add(next));
        });
        Test("window accepts out of order, rejects duplicates and old replay", () =>
        {
            var a = new CommitAcceptor(4, 2);
            foreach (long n in new long[] { 1, 3, 2, 4, 5 }) Eq(Acceptance.Accepted, a.Process(Commit(n), 11, CommitOrigin.Remote));
            Eq(Acceptance.TooOld, a.Process(Commit(1), 11, CommitOrigin.Remote));
            Eq(Acceptance.Duplicate, a.Process(Commit(3), 11, CommitOrigin.Remote));
            Eq(Acceptance.Accepted, a.Process(Commit(long.MaxValue), 11, CommitOrigin.Remote));
            Eq(Acceptance.Duplicate, a.Process(Commit(long.MaxValue), 11, CommitOrigin.Remote));
        });
        Test("dedup remains bounded without evicting source replay protection", () =>
        {
            var a = new CommitAcceptor(8, 2);
            for (int n = 1; n <= 100000; n++) Eq(Acceptance.Accepted, a.Process(Commit(n), 11, CommitOrigin.Remote));
            Eq(Acceptance.Accepted, a.Process(Commit(peer: 12), 12, CommitOrigin.Remote));
            Eq(Acceptance.SourceCapacity, a.Process(Commit(peer: 13), 13, CommitOrigin.Remote));
            Eq(2, a.SourceCount); Eq(16, a.AllocatedSlots);
            Eq(Acceptance.Duplicate, a.Process(Commit(100000), 11, CommitOrigin.Remote));
        });
        Test("codec roundtrip includes optional IDs, names and overkill metric", () =>
        {
            DamageCommit c = Commit(loss: 6);
            DamageCommit r = CommitCodec.Decode(CommitCodec.Encode(c));
            Eq(c.Id, r.Id); Eq(6f, r.EffectiveHpLoss); Eq(c.TimestampUtcTicks, r.TimestampUtcTicks);
            Eq("Игрок", r.Facts.VictimName); Eq("Лучник", r.Facts.AttackerName);
            Eq(888L, r.Facts.VictimPlayerId.Value); Eq(777L, r.Facts.AttackerPlayerId.Value);
            Eq(AttackerClass.Player, r.Facts.Attacker);
            var noAttacker = new DamageCommit(c.Id, new DamageFacts(1, 1, true, 9, AttackerClass.None, null, 2, null, null), 1, c.TimestampUtcTicks);
            Eq((long?)null, CommitCodec.Decode(CommitCodec.Encode(noAttacker)).Facts.AttackerPlayerId);
        });
        Test("codec rejects truncation, wrong version, trailing data, invalid bool and oversize", () =>
        {
            byte[] bytes = CommitCodec.Encode(Commit());
            for (int n = 0; n < bytes.Length; n++) Fails(() => CommitCodec.Decode(bytes.Take(n).ToArray()));
            byte[] badVersion = (byte[])bytes.Clone(); badVersion[0] = 99; Fails(() => CommitCodec.Decode(badVersion));
            byte[] badBool = (byte[])bytes.Clone(); badBool[45] = 2; Fails(() => CommitCodec.Decode(badBool));
            Fails(() => CommitCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
            Fails(() => CommitCodec.Decode(new byte[1025]));
        });
        Test("invalid payloads rejected before dedup", () =>
        {
            var a = new CommitAcceptor(); var c = Commit();
            Eq(Acceptance.InvalidPayload, a.Process(new DamageCommit(c.Id, null, 1, c.TimestampUtcTicks), 11, CommitOrigin.Local));
            Eq(Acceptance.InvalidPayload, a.Process(new DamageCommit(c.Id, Facts, 1, 0), 11, CommitOrigin.Local));
            var bad = new DamageFacts(0, 0, false, null, (AttackerClass)99, null, 0, "", "");
            Eq(Acceptance.InvalidPayload, a.Process(new DamageCommit(c.Id, bad, 1, c.TimestampUtcTicks), 11, CommitOrigin.Local));
            Eq(0, a.SourceCount);
        });
        Test("local and remote use same canonical host acceptance component", () =>
        {
            var log = new List<(string Kind, DamageCommit C, string Detail)>();
            var host = new CommitSession(11, Epoch, true, _ => throw new Exception("Local path sent RPC"),
                (kind, c, detail) => log.Add((kind, c, detail)));
            host.Observe(Facts, 6, DateTime.UtcNow.Ticks);
            DamageCommit local = log[0].C;
            Eq(Acceptance.Duplicate, host.ProcessDamageCommit(local, 11, CommitOrigin.Remote));
            Eq(Acceptance.Accepted, host.ProcessDamageCommit(Commit(peer: 12), 12, CommitOrigin.Remote));
            Eq(2, log.Count(x => x.Kind == "DamageCommitAccepted"));
            True(log.Any(x => x.Kind == "DamageCommitAccepted" && x.Detail == "Local"));
            True(log.Any(x => x.Kind == "DamageCommitAccepted" && x.Detail == "Remote"));
        });
        Test("lost commit and lost ACK retry converge on exactly one host acceptance", () =>
        {
            int attempts = 0, accepted = 0, created = 0;
            var host = new CommitSession(22, Guid.NewGuid(), true, _ => { },
                (kind, c, d) => { if (kind == "DamageCommitAccepted") accepted++; });
            CommitSession client = null;
            client = new CommitSession(11, Epoch, false, c =>
            {
                attempts++;
                if (attempts == 1) return; // lost request
                var decoded = CommitCodec.Decode(CommitCodec.Encode(c));
                Acceptance result = host.ProcessDamageCommit(decoded, 11, CommitOrigin.Remote);
                if (attempts == 2) return; // accepted but lost ACK
                var ack = CommitCodec.DecodeAck(CommitCodec.EncodeAck(c.Id, result));
                client.Acknowledge(ack.Id, ack.Result);
            }, (kind, c, d) => { if (kind == "DamageCommitCreated") created++; });
            client.Observe(Facts, 6, DateTime.UtcNow.Ticks);
            client.Pump(0); client.Pump(0.5); Eq(1, attempts);
            client.Pump(1); Eq(1, accepted); Eq(1, client.Outbox.Count);
            client.Pump(2); Eq(3, attempts); Eq(1, created); Eq(1, accepted); Eq(0, client.Outbox.Count);
        });
        Test("outbox bounded, retries head only, unrelated ACK ignored", () =>
        {
            var box = new CommitOutbox(2);
            True(box.Enqueue(Commit(1))); True(box.Enqueue(Commit(2))); True(!box.Enqueue(Commit(3)));
            Eq(1L, box.Due(0).Id.Sequence); Eq((DamageCommit)null, box.Due(0.5));
            True(!box.Complete(Commit(2).Id)); Eq(1L, box.Due(1).Id.Sequence);
            True(box.Complete(Commit(1).Id)); Eq(2L, box.Due(1).Id.Sequence); Eq(1, box.Count);
        });
        Test("closing session abandons pending once and prevents stale sends", () =>
        {
            int sends = 0, abandoned = 0;
            var session = new CommitSession(11, Epoch, false, _ => sends++, (kind, c, d) => { if (kind == "DamageCommitAbandoned") abandoned++; });
            session.Observe(Facts, 1, DateTime.UtcNow.Ticks); session.Close("Menu"); session.Close("Again");
            session.Pump(1); session.Observe(Facts, 2, DateTime.UtcNow.Ticks);
            Eq(0, sends); Eq(1, abandoned);
        });
        Test("Valheim adapter doubles: directed RPC, ACK, no local RPC, one registration", AdapterDelivery);
        Test("Valheim adapter doubles: snapshot heartbeat converges host and client", SnapshotDelivery);
        Test("Valheim adapter doubles: snapshot stores clear with network lifecycle", SnapshotLifecycleReset);
        Test("Valheim adapter doubles: host timeout overrides client-local timeout", HostAuthoritativeTimeout);
        Test("Valheim adapter doubles: lost provenance ACK retries duplicate and completes", AttributionLostAck);
        Test("Valheim adapter doubles: four rapid summons each complete", AttributionSequence);
        Test("Valheim adapter doubles: disconnect and stop clear provenance outbox", AttributionLifecycleReset);
        Test("Valheim adapter doubles: reconnect, stale handler and new world", AdapterLifecycle);
        return _passed;
    }

    private static void AttributionLostAck()
    {
        Plugin.TransportMessages.Clear(); double now = 0; bool dropAck = true;
        var host = new Node(101, true); var client = new Node(202, false) { Adapter = new DamageCommitTransport(() => now) };
        client.Net.ServerPeer = new ZNetPeer { m_uid = host.Peer };
        client.Rpc.Send = (target, name, package) =>
        { client.Sent.Add((target, name, package)); host.Use(); host.Rpc.Handlers[name](client.Peer, package); client.Use(); };
        host.Rpc.Send = (target, name, package) =>
        {
            host.Sent.Add((target, name, package));
            if (name == DamageCommitTransport.AttributionAckRpc && dropAck) { dropAck = false; return; }
            client.Use(); client.Rpc.Handlers[name](host.Peer, package); host.Use();
        };
        host.Use(); host.Adapter.Bind(host.Net); client.Use(); client.Adapter.Bind(client.Net);
        client.Adapter.ObserveSummonProvenance(202, 202, 151, -451055642); client.Adapter.Update();
        Eq(1, client.Adapter.AttributionPendingCount); Eq(1, Messages("SummonProvenanceAccepted"));
        now = 1; client.Adapter.Update();
        Eq(0, client.Adapter.AttributionPendingCount); Eq(1, Messages("SummonProvenanceAccepted"));
        Eq(1, Messages("SummonProvenanceDuplicate")); Eq(2, Messages("SummonProvenanceAckSent"));
        Eq(1, Messages("SummonProvenanceMessageCompleted"));
    }

    private static void SnapshotDelivery()
    {
        double now = 0d;
        var host = new Node(101, true) { Adapter = new DamageCommitTransport(() => now) };
        var client = new Node(202, false) { Adapter = new DamageCommitTransport(() => now) };
        Link(host, client);
        host.Use(); host.Adapter.Bind(host.Net);
        var observedPlayer = new Player { PlayerId = 777 }; observedPlayer.View.Zdo.Owner = 202; Player.Instances.Add(observedPlayer);
        var pve = new DamageFacts(111, 3, false, null, AttackerClass.Player, 777, 1, "Greydwarf", "Лучник");
        client.Use(); client.Adapter.Bind(client.Net); client.Adapter.Observe(202, pve, 6f);

        host.Use(); host.Adapter.Update();
        True(host.Adapter.SnapshotStore.TryGetLatest(out var local));
        client.Use(); True(client.Adapter.SnapshotStore.TryGetLatest(out var remote));
        Eq(local.SnapshotEpoch, remote.SnapshotEpoch); Eq(local.Sequence, remote.Sequence);
        Eq(0L, local.EncounterId); Eq(0, local.Players.Count); True(remote.EncounterId > 0); Eq(1, remote.Players.Count);
        Eq(6f, remote.Players[0].DamageDone);

        int sent = host.Sent.Count(x => x.Name == DamageCommitTransport.SnapshotRpc);
        now = 0.49d; host.Use(); host.Adapter.Update();
        Eq(sent, host.Sent.Count(x => x.Name == DamageCommitTransport.SnapshotRpc));
        now = 0.5d; host.Adapter.Update();
        Eq(sent + 1, host.Sent.Count(x => x.Name == DamageCommitTransport.SnapshotRpc));
        client.Use(); True(client.Adapter.SnapshotStore.TryGetLatest(out remote)); Eq(2L, remote.Sequence);
    }

    private static void SnapshotLifecycleReset()
    {
        double now = 0d;
        var host = new Node(101, true) { Adapter = new DamageCommitTransport(() => now) };
        var client = new Node(202, false) { Adapter = new DamageCommitTransport(() => now) };
        Link(host, client);
        host.Use(); host.Adapter.Bind(host.Net);
        client.Use(); client.Adapter.Bind(client.Net);
        host.Use(); host.Adapter.Update();
        True(host.Adapter.SnapshotStore.TryGetLatest(out _));
        client.Net.ServerPeer = null; client.Use(); client.Adapter.Disconnected(client.Net);
        True(!client.Adapter.SnapshotStore.TryGetLatest(out _));
        host.Use(); host.Adapter.Stop(host.Net);
        True(!host.Adapter.SnapshotStore.TryGetLatest(out _));
    }

    private static void HostAuthoritativeTimeout()
    {
        double now = 0d;
        var host = new Node(101, true) { Adapter = new DamageCommitTransport(() => now, () => 20d, () => 180d, () => 6d) };
        var client = new Node(202, false) { Adapter = new DamageCommitTransport(() => now, () => 5d, () => 30d, () => 1d) };
        Link(host, client); host.Use(); host.Adapter.Bind(host.Net); client.Use(); client.Adapter.Bind(client.Net);
        var observedPlayer = new Player { PlayerId = 777 }; observedPlayer.View.Zdo.Owner = 202; Player.Instances.Add(observedPlayer);
        var pve = new DamageFacts(111, 3, false, null, AttackerClass.Player, 777, 1, "Troll", "Player");
        client.Adapter.Observe(202, pve, 5f);
        now = 6d; host.Use(); host.Adapter.Update(); Eq(DiagnosticDamageProbe.Encounter.EncounterState.Active, host.Adapter.Encounter.State);
        client.Use(); True(client.Adapter.Encounter == null); True(client.Adapter.SnapshotStore.TryGetLatest(out var active));
        Eq(DiagnosticDamageProbe.Encounter.EncounterState.Active, active.EncounterState);
        True(Math.Abs((5d / 6d) - active.Players[0].Dps) < .001d);
        Eq(6d, active.EncounterElapsedSeconds); Eq(5f, active.Players[0].DamageDone); Eq(0f, active.Players[0].DamageTaken);
        now = 20.1d; host.Use(); host.Adapter.Update(); Eq(DiagnosticDamageProbe.Encounter.EncounterState.Finished, host.Adapter.Encounter.State);
        client.Use(); True(client.Adapter.SnapshotStore.TryGetLatest(out var finished));
        Eq(DiagnosticDamageProbe.Encounter.EncounterState.NoEncounter, finished.EncounterState); Eq(0, finished.Players.Count);
    }

    private static void AttributionSequence()
    {
        Plugin.TransportMessages.Clear();
        var host = new Node(101, true); var client = new Node(202, false); Link(host, client);
        host.Use(); host.Adapter.Bind(host.Net); client.Use(); client.Adapter.Bind(client.Net);
        uint[] objects = { 31u, 50u, 151u, 162u };
        foreach (uint obj in objects) { client.Adapter.ObserveSummonProvenance(202, 202, obj, -451055642); client.Adapter.Update(); }
        Eq(0, client.Adapter.AttributionPendingCount);
        host.Use(); foreach (uint obj in objects) { True(host.Adapter.Attribution.Summons.TryResolve("202:" + obj, out long id)); Eq(-451055642L, id); }
        Eq(4, Messages("SummonProvenanceAccepted")); Eq(4, Messages("SummonProvenanceAckSent"));
        Eq(4, Messages("SummonProvenanceMessageCompleted"));
    }

    private static void AttributionLifecycleReset()
    {
        var client = new Node(202, false); client.Net.ServerPeer = new ZNetPeer { m_uid = 101 };
        client.Rpc.Send = (_, _, _) => { };
        client.Use(); client.Adapter.Bind(client.Net);
        client.Adapter.ObserveSummonProvenance(202, 202, 1, 7); client.Adapter.Update(); Eq(1, client.Adapter.AttributionPendingCount);
        client.Net.ServerPeer = null; client.Adapter.Disconnected(client.Net); Eq(0, client.Adapter.AttributionPendingCount);

        var stopped = new Node(303, false); stopped.Net.ServerPeer = new ZNetPeer { m_uid = 101 };
        stopped.Rpc.Send = (_, _, _) => { };
        stopped.Use(); stopped.Adapter.Bind(stopped.Net);
        stopped.Adapter.ObserveSummonProvenance(303, 303, 1, 7); stopped.Adapter.Update(); Eq(1, stopped.Adapter.AttributionPendingCount);
        stopped.Adapter.Stop(stopped.Net); Eq(0, stopped.Adapter.AttributionPendingCount);
    }

    private static void AdapterDelivery()
    {
        Plugin.TransportMessages.Clear();
        var host = new Node(101, true); var client = new Node(202, false);
        Link(host, client);
        host.Use(); host.Adapter.Bind(host.Net); host.Adapter.Bind(host.Net);
        client.Use(); client.Adapter.Bind(client.Net); client.Adapter.Bind(client.Net);
        Eq(5, host.Rpc.Handlers.Count); Eq(5, client.Rpc.Handlers.Count);
        client.Adapter.Observe(202, Facts, 6);
        Eq(1, client.Sent.Count); Eq(DamageCommitTransport.CommitRpc, client.Sent[0].Name);
        Eq(101L, client.Sent[0].Target); Eq(1, host.Sent.Count); Eq(DamageCommitTransport.AckRpc, host.Sent[0].Name);
        Eq(1, Messages("DamageCommitCreated")); Eq(1, Messages("DamageCommitAccepted"));
        // Duplicate payload reaches host but cannot enter acceptance twice.
        client.Rpc.InvokeRoutedRPC(101, DamageCommitTransport.CommitRpc, client.Sent[0].Package);
        Eq(1, Messages("DamageCommitAccepted"));
        int sent = host.Sent.Count;
        host.Use(); host.Adapter.Observe(101, Facts, 6);
        Eq(sent, host.Sent.Count); Eq(2, Messages("DamageCommitAccepted"));
        True(Plugin.TransportMessages.Any(x => x.Contains("\"Route\":\"Local\"")));
        True(Plugin.TransportMessages.Any(x => x.Contains("\"Origin\":\"Remote\"")));
        client.Use(); client.Adapter.ObserveSummonProvenance(202, 202, 77, 777); client.Adapter.Update();
        host.Use(); True(host.Adapter.Attribution.Summons.TryResolve("202:77", out long summoner)); Eq(777L, summoner);
        True(Plugin.TransportMessages.Any(x => x.StartsWith("SummonProvenanceAccepted", StringComparison.Ordinal)));
    }

    private static void AdapterLifecycle()
    {
        Plugin.TransportMessages.Clear();
        var host = new Node(101, true); var client = new Node(202, false); Link(host, client);
        host.Use(); host.Adapter.Bind(host.Net);
        client.Use(); client.Adapter.Bind(client.Net); client.Adapter.Observe(202, Facts, 1);
        EventId first = CommitCodec.Decode(client.Sent[0].Package.GetArray()).Id;
        client.Net.ServerPeer = null; client.Adapter.Disconnected(client.Net);
        client.Adapter.Observe(202, Facts, 2); Eq(1, Messages("DamageCommitCreated"));
        client.Net.ServerPeer = new ZNetPeer { m_uid = 101 };
        client.Adapter.Update(); client.Adapter.Bind(client.Net); client.Adapter.Observe(202, Facts, 2);
        EventId second = CommitCodec.Decode(client.Sent[1].Package.GetArray()).Id;
        Eq(1L, first.Sequence); Eq(1L, second.Sequence); True(!first.Equals(second));
        Eq(5, client.Rpc.Handlers.Count); Eq(2, Messages("DamageCommitAccepted"));
        host.Use(); host.Adapter.Stop(host.Net);
        host.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, client.Sent[0].Package);
        host.Adapter.Update(); Eq(2, Messages("DamageCommitAccepted"));
        // Reuse plugin adapter in next world's new network instance; old callbacks remain inert.
        var newHost = new Node(101, true) { Adapter = host.Adapter };
        var newClient = new Node(202, false) { Adapter = client.Adapter };
        client.Use(); client.Adapter.Stop(client.Net);
        Link(newHost, newClient);
        newHost.Use(); newHost.Adapter.Bind(newHost.Net);
        newClient.Use(); newClient.Adapter.Bind(newClient.Net); newClient.Adapter.Observe(202, Facts, 3);
        Eq(3, Messages("DamageCommitAccepted"));
        newHost.Use(); host.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, client.Sent[0].Package);
        Eq(3, Messages("DamageCommitAccepted"));
    }

    private sealed class Node
    {
        internal readonly long Peer;
        internal readonly ZNet Net;
        internal readonly ZRoutedRpc Rpc = new ZRoutedRpc();
        internal DamageCommitTransport Adapter = new DamageCommitTransport();
        internal readonly List<(long Target, string Name, ZPackage Package)> Sent = new List<(long, string, ZPackage)>();
        internal Node(long peer, bool host)
        {
            Peer = peer; Net = new ZNet { Server = host, SinglePlayer = false };
            Rpc.Send = (_, _, _) => { };
        }
        internal void Use() { ZNet.instance = Net; ZRoutedRpc.instance = Rpc; ZDOMan.Session = Peer; }
    }
    private static void Link(Node host, Node client)
    {
        client.Net.ServerPeer = new ZNetPeer { m_uid = host.Peer };
        host.Net.Peers.Add(new ZNetPeer { m_uid = client.Peer });
        void Wire(Node from, Node to) => from.Rpc.Send = (target, name, package) =>
        {
            if (target != ZRoutedRpc.Everybody) Eq(to.Peer, target); from.Sent.Add((target, name, package));
            to.Use(); to.Rpc.Handlers[name](from.Peer, package); from.Use();
        };
        Wire(host, client); Wire(client, host);
    }
    private static int Messages(string kind) => Plugin.TransportMessages.Count(x => x.StartsWith(kind + " ", StringComparison.Ordinal));
    private static void Test(string name, Action run) { Player.Instances.Clear(); Player.m_localPlayer = null; run(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Fails(Action action)
    { try { action(); } catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException) { return; } throw new Exception("Malformed packet accepted"); }
}
