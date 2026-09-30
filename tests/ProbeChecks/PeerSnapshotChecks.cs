using System;
using System.Collections.Generic;
using System.Linq;
using DiagnosticDamageProbe;
using DiagnosticDamageProbe.Encounter;
using DiagnosticDamageProbe.Snapshot;
using DiagnosticDamageProbe.Transport;

internal static class PeerSnapshotChecks
{
    private static int _passed;
    internal static int Run()
    {
        Test("local host routes by local PlayerID and applies without RPC", () =>
        {
            var h = new Host(); var c = h.Done(1001, 10, 7); h.Publish();
            Eq(c.Id, h.Local.EncounterId); Rows(h.Local, 1001); Eq(7f, h.Local.Players[0].DamageDone); Eq(0, h.Sent.Count);
        });
        Test("remote mapping reads observed Player ZDO owner and gameplay ID", () =>
        {
            var h = new Host(); h.AddPeer(202, -42); var c = h.Done(-42, 10, 9); h.Publish();
            Eq(c.Id, h.Remote(202).EncounterId); Rows(h.Remote(202), -42); Eq(-42L, HostPlayerIdentity.ResolveRemote(202).Value);
        });
        Test("peer equal to another PlayerID cannot select that player's data", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(202, 10, 50); var c = h.Done(2002, 20, 3); h.Publish();
            Eq(c.Id, h.Remote(202).EncounterId); Rows(h.Remote(202), 2002);
        });
        Test("unresolved peer gets empty despite other active clusters", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Done(202, 10); h.Publish(); Empty(h.Remote(202));
            Eq(1, h.Adapter.Clusters.ActiveCount);
        });
        Test("resolved player with no cluster receives empty", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(1001, 10); h.Publish(); Empty(h.Remote(202));
        });
        Test("environmental player-only cluster publishes Damage Taken", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002);
            h.Adapter.Observe(101, new DamageFacts(99, 10, true, 2002, AttackerClass.None, null, 1, "Player", ""), 4);
            h.Publish(); Rows(h.Remote(202), 2002); Eq(4f, h.Remote(202).Players[0].DamageTaken); Eq(0f, h.Remote(202).Players[0].DamageDone);
        });
        Test("isolation A B versus C with shared cycle identity", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, 3003); h.AddPeer(404, 4004);
            h.Done(1001, 10, 3); h.Done(2002, 10, 7); h.Done(3003, 20, 11); h.Publish();
            Rows(h.Local, 1001, 2002); Rows(h.Remote(202), 1001, 2002); Rows(h.Remote(303), 3003); Empty(h.Remote(404));
            True(CombatSnapshotCodec.Encode(h.Local).SequenceEqual(CombatSnapshotCodec.Encode(h.Remote(202))));
            True(h.Local.EncounterId != h.Remote(303).EncounterId);
            foreach (long peer in new long[] { 202, 303, 404 }) { Eq(h.Local.SnapshotEpoch, h.Remote(peer).SnapshotEpoch); Eq(h.Local.Sequence, h.Remote(peer).Sequence); }
        });
        Test("merge publishes survivor to all former donor recipients", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, 3003);
            var a = h.Done(1001, 10); var b = h.Done(2002, 20); h.Done(3003, 20); h.Publish(); Eq(b.Id, h.Remote(202).EncounterId);
            h.Done(1001, 20, 5); h.Now = 1; h.Publish();
            foreach (var s in new[] { h.Local, h.Remote(202), h.Remote(303) }) { Eq(a.Id, s.EncounterId); Rows(s, 1001, 2002, 3003); }
            Eq(8f, h.Local.Players.Single(p => p.PlayerId == 1001).DamageDone); True(!h.Adapter.Clusters.TryGet(b.Id, out _));
        });
        Test("Finished sends newer empty snapshots replacing client combat", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(1001, 10); h.Done(2002, 10); h.Publish(); long sequence = h.Remote(202).Sequence;
            h.Now = 21; h.Publish(); Empty(h.Local); Empty(h.Remote(202)); Eq(sequence + 1, h.Remote(202).Sequence);
            True(h.Adapter.Clusters.LastFinished != null);
        });
        Test("new combat after finish publishes new encounter ID", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); var old = h.Done(2002, 10); h.Publish();
            h.Now = 21; h.Publish(); Empty(h.Remote(202)); h.Now = 22; var next = h.Done(2002, 10); h.Publish();
            True(next.Id != old.Id); Eq(next.Id, h.Remote(202).EncounterId); Rows(h.Remote(202), 2002);
        });
        Test("sequence increments once per cycle including empty recipients", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, null); h.Done(2002, 10); h.Publish();
            Eq(1L, h.Local.Sequence); Eq(1L, h.Remote(202).Sequence); Eq(1L, h.Remote(303).Sequence);
            h.Now = .49; h.Publish(); Eq(2, h.Sent.Count);
            h.Now = .5; h.Publish(); Eq(4, h.Sent.Count);
            Eq(2L, h.Local.Sequence); Eq(2L, h.Remote(202).Sequence); Eq(2L, h.Remote(303).Sequence);
        });
        Test("only ready remote peers receive directed snapshots; no zero or self", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, 3003, false);
            h.Net.Peers.Add(new ZNetPeer { m_uid = 0 }); h.Net.Peers.Add(new ZNetPeer { m_uid = 101 });
            h.Net.Peers.Add(new ZNetPeer { m_uid = 202 }); h.Net.Peers.Add(null);
            h.Publish(); Eq(1, h.Sent.Count); Eq(202L, h.Sent[0].Peer); True(h.Sent.All(s => s.Peer != ZRoutedRpc.Everybody && s.Peer != 0 && s.Peer != 101));
        });
        Test("missing local identity receives empty without legacy fallback", () =>
        {
            var h = new Host(); h.Done(1001, 10); Player.m_localPlayer = null; h.Publish(); Empty(h.Local);
            Eq(1, h.Adapter.Encounter.Statistics.Count);
        });
        Test("mapping loss clears previously delivered combat on next cycle", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish(); Rows(h.Remote(202), 2002);
            Player.Instances.RemoveAll(p => p.PlayerId == 2002); h.Net.Peers[0].m_playerID = 0; h.Now = 1; h.Publish(); Empty(h.Remote(202));
        });
        Test("ownership change is observed without stale mapping cache", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, null); h.Done(2002, 10); h.Publish();
            Player.Instances.Single(p => p.PlayerId == 2002).View.Zdo.Owner = 303;
            h.Net.Peers[0].m_playerID = 0; h.Net.Peers[1].m_playerID = 2002;
            h.Now = 1; h.Publish(); Empty(h.Remote(202)); Rows(h.Remote(303), 2002);
        });
        Test("ambiguous owner identities fail closed independent of enumeration", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddEntity(202, 3003); h.Done(2002, 10); h.Done(3003, 20);
            h.Publish(); Empty(h.Remote(202)); Player.Instances.Reverse(); h.Now = 1; h.Publish(); Empty(h.Remote(202));
        });
        Test("invalid destroyed or zero-ID Player instances cannot resolve", () =>
        {
            var h = new Host(); h.AddPeer(202, null);
            var invalid = h.AddEntity(202, 2); invalid.View.Valid = false;
            var destroyed = h.AddEntity(202, 3); destroyed.Destroyed = true;
            h.AddEntity(202, 0); var missing = h.AddEntity(202, 4); missing.View.Zdo = null;
            h.Done(2, 10); h.Publish(); Empty(h.Remote(202));
        });
        Test("local identity must be a valid Player owned by this host", () =>
        {
            var h = new Host(); h.Done(1001, 10); Player.m_localPlayer.View.Zdo.Owner = 202; h.Publish(); Empty(h.Local);
        });
        Test("mapping does not create membership or depend on names", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); Player.Instances.ForEach(p => p.PlayerName = "Same");
            for (int i = 0; i < 10; i++) { Eq(2002L, HostPlayerIdentity.ResolveRemote(202).Value); Eq(1001L, HostPlayerIdentity.ResolveLocal(101).Value); }
            Eq(0, h.Adapter.Clusters.ActiveCount); h.Publish(); Empty(h.Local); Empty(h.Remote(202));
        });
        Test("v2 codec roundtrips routed and empty payloads unchanged", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Done(1001, 10); h.Publish();
            foreach (var s in new[] { h.Local, h.Remote(202) })
            { byte[] encoded = CombatSnapshotCodec.Encode(s); Eq((byte)2, encoded[0]); True(encoded.SequenceEqual(CombatSnapshotCodec.Encode(CombatSnapshotCodec.Decode(encoded)))); }
            Eq("CombatMeter.CombatSnapshot.v2", DamageCommitTransport.SnapshotRpc);
        });
        Test("client rejects non-host spoof and host-session mismatch", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish(); var good = h.Remote(202);
            h.Deliver(202, 303, CombatSnapshotBuilder.Empty(303, good.SnapshotEpoch, 99)); Eq(good, h.Remote(202));
            h.Deliver(202, 101, CombatSnapshotBuilder.Empty(303, good.SnapshotEpoch, 99)); Eq(good, h.Remote(202));
        });
        Test("client rejects duplicate stale malformed and mismatched-epoch payloads", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish(); var first = h.Remote(202);
            h.Now = 1; h.Publish(); var current = h.Remote(202);
            h.Deliver(202, 101, CombatSnapshotBuilder.Empty(101, current.SnapshotEpoch, current.Sequence)); Eq(current, h.Remote(202));
            h.Deliver(202, 101, first); Eq(current, h.Remote(202));
            h.DeliverBytes(202, 101, new byte[] { 1, 2 }); Eq(current, h.Remote(202));
            var fresh = CombatSnapshotBuilder.Empty(101, Guid.NewGuid(), 1); h.Deliver(202, 101, fresh); Eq(current, h.Remote(202));
            h.Deliver(202, 101, CombatSnapshotBuilder.Empty(101, current.SnapshotEpoch, 100)); Eq(current.SnapshotEpoch, h.Remote(202).SnapshotEpoch); Empty(h.Remote(202));
        });
        Test("Recovery cluster remains the player's current snapshot", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); var c = h.Done(2002, 10); h.Now = 1; h.Adapter.ObservePlayerDeath(2002);
            h.Now = 21; h.Publish(); Eq(c.Id, h.Remote(202).EncounterId); Eq(EncounterState.Recovery, h.Remote(202).EncounterState);
        });
        Test("singleplayer applies cluster snapshot without any network send", () =>
        {
            var h = new Host(); h.Net.SinglePlayer = true; h.AddPeer(202, 2002); h.Done(1001, 10); h.Publish();
            Rows(h.Local, 1001); Eq(0, h.Sent.Count);
        });
        Test("Empty builder preserves strict snapshot identity validation", () =>
        {
            foreach (Action invalid in new Action[] { () => CombatSnapshotBuilder.Empty(0, Guid.NewGuid(), 1), () => CombatSnapshotBuilder.Empty(101, Guid.Empty, 1), () => CombatSnapshotBuilder.Empty(101, Guid.NewGuid(), 0) })
            { bool rejected = false; try { invalid(); } catch (ArgumentException) { rejected = true; } True(rejected); }
        });

        Test("verified vanilla identity survives remote entity unloading", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish(); Rows(h.Remote(202), 2002);
            h.Unload(2002); h.Now = 1; h.Publish(); Rows(h.Remote(202), 2002);
        });
        Test("initial connection outside host area needs no observed Player", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Net.Peers[0].m_playerID = -42;
            h.Done(-42, 10); h.Publish(); Rows(h.Remote(202), -42);
        });
        Test("two absent remote players keep independent snapshots", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, 3003);
            h.Done(2002, 10, 4); h.Done(3003, 20, 9); h.Publish(); h.Unload(2002); h.Unload(3003);
            h.Now = 1; h.Publish(); Rows(h.Remote(202), 2002); Rows(h.Remote(303), 3003);
            Eq(4f, h.Remote(202).Players[0].DamageDone); Eq(9f, h.Remote(303).Players[0].DamageDone);
        });
        Test("absent remote players receive survivor after legitimate bridge", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.AddPeer(303, 3003);
            var first = h.Done(2002, 10); h.Done(3003, 20); h.Publish(); h.Unload(2002); h.Unload(3003);
            h.Now = 1; h.Done(2002, 20); h.Publish();
            Rows(h.Remote(202), 2002, 3003); Rows(h.Remote(303), 2002, 3003);
            Eq(first.Id, h.Remote(202).EncounterId); Eq(first.Id, h.Remote(303).EncounterId);
        });
        Test("disconnect invalidates and reconnect needs current vanilla registration", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish(); h.Unload(2002);
            h.Net.Peers.Clear(); h.Adapter.Disconnected(h.Net);
            h.Net.Peers.Add(new ZNetPeer { m_uid = 202 }); h.Now = 1; h.Publish(); Empty(h.Remote(202));
            h.Net.Peers[0].m_playerID = 3003; h.Done(3003, 20); h.Now = 2; h.Publish(); Rows(h.Remote(202), 3003);
        });
        Test("connection replacement between publications does not reuse old binding", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish(); h.Unload(2002);
            h.Net.Peers[0] = new ZNetPeer { m_uid = 202 }; h.Now = 1; h.Publish(); Empty(h.Remote(202));
        });
        Test("respawn with unchanged vanilla identity retains route", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish();
            h.Unload(2002); h.AddEntity(202, 2002); h.Now = 1; h.Publish(); Rows(h.Remote(202), 2002);
        });
        Test("new observed conflicting entity quarantines binding until reconnect", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish();
            h.AddEntity(202, 3003); h.Now = 1; h.Publish(); Empty(h.Remote(202));
            h.Unload(3003); h.Now = 2; h.Publish(); Empty(h.Remote(202));
            h.Net.Peers[0] = new ZNetPeer { m_uid = 202, m_playerID = 2002 }; h.Now = 3; h.Publish(); Rows(h.Remote(202), 2002);
        });
        Test("changed vanilla identity cannot silently select another cluster", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Done(3003, 20); h.Publish(); h.Unload(2002);
            h.Net.Peers[0].m_playerID = 3003; h.Now = 1; h.Publish(); Empty(h.Remote(202));
            h.Net.Peers[0].m_playerID = 2002; h.Now = 2; h.Publish(); Empty(h.Remote(202));
        });
        Test("duplicate vanilla player identities fail closed for both peers", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.AddPeer(303, null);
            h.Net.Peers[0].m_playerID = h.Net.Peers[1].m_playerID = 2002;
            h.Done(2002, 10); h.Publish(); Empty(h.Remote(202)); Empty(h.Remote(303));
        });
        Test("remote claiming local host identity fails closed", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Net.Peers[0].m_playerID = 1001;
            h.Done(1001, 10); h.Publish(); Rows(h.Local, 1001); Empty(h.Remote(202));
        });
        Test("world reset discards quarantined bindings and combat state", () =>
        {
            var h = new Host(); h.AddPeer(202, 2002); h.Done(2002, 10); h.Publish();
            h.AddEntity(202, 3003); h.Now = 1; h.Publish(); Empty(h.Remote(202));
            h.Adapter.Stop(h.Net);
            var fresh = new Host(); fresh.AddPeer(202, 2002); fresh.Done(2002, 20); fresh.Publish(); Rows(fresh.Remote(202), 2002);
        });
        Test("remote commits without Player preserve ACK dedup sender validation and routing", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002);
            var commit = new DamageCommit(new EventId(202, Guid.NewGuid(), 1),
                new DamageFacts(99, 10, false, null, AttackerClass.Player, 2002, 1, "NPC", "P"), 7, DateTime.UtcNow.Ticks);
            h.Use();
            h.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, new ZPackage(CommitCodec.Encode(commit)));
            h.Rpc.Handlers[DamageCommitTransport.CommitRpc](202, new ZPackage(CommitCodec.Encode(commit)));
            h.Rpc.Handlers[DamageCommitTransport.CommitRpc](303, new ZPackage(CommitCodec.Encode(commit)));
            Eq(Acceptance.Accepted, h.Acks[0]); Eq(Acceptance.Duplicate, h.Acks[1]); Eq(Acceptance.SenderMismatch, h.Acks[2]);
            Eq(1, h.Adapter.Clusters.ActiveCount); h.Publish(); Rows(h.Remote(202), 2002); Eq(7f, h.Remote(202).Players[0].DamageDone);
            h.Now = 21; h.Publish(); Empty(h.Remote(202));
        });

        Test("vanilla character registration resolves initial distant peer without PlayerID RPC", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002); h.Done(2002, 10); h.Publish(); Rows(h.Remote(202), 2002);
        });
        Test("character replication absence and respawn retain verified identity", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002); h.Done(2002, 10); h.Publish();
            ZDOMan.instance.Objects.Clear(); h.Net.Peers[0].m_characterID = default; h.Now = 1; h.Publish(); Rows(h.Remote(202), 2002);
            h.Character(202, 2002); h.Now = 2; h.Publish(); Rows(h.Remote(202), 2002);
        });
        Test("new character with changed gameplay identity is rejected", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002); h.Done(2002, 10); h.Publish();
            h.Character(202, 3003); h.Now = 1; h.Publish(); Empty(h.Remote(202));
        });
        Test("character registration owner mismatch cannot route another peer", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002, 303); h.Done(2002, 10); h.Publish(); Empty(h.Remote(202));
        });
        Test("zero character identity fails closed", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 0); h.Done(2002, 10); h.Publish(); Empty(h.Remote(202));
        });
        Test("vanilla PlayerID and character disagreement fails closed", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Net.Peers[0].m_playerID = 2002; h.Character(202, 3003);
            h.Done(2002, 10); h.Done(3003, 20); h.Publish(); Empty(h.Remote(202));
        });
        Test("unregistered initial character cannot use combat facts as identity", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Net.Peers[0].m_characterID = new ZDOID("missing");
            h.Done(2002, 10); h.Publish(); Empty(h.Remote(202));
            h.Character(202, 2002); h.Now = 1; h.Publish(); Rows(h.Remote(202), 2002);
        });

        Test("v2 distant recipient receives authoritative damage largest hit and accepted deaths", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002);
            h.Done(2002, 10, 60); h.Done(2002, 10, 40); h.Now = 1;
            h.Adapter.ObservePlayerDeath(2002, "life1"); h.Adapter.ObservePlayerDeath(2002, "life1"); h.Publish();
            var row = h.Remote(202).Players.Single(); Eq(100f, row.DamageDone); Eq(60f, row.LargestHit); Eq(1, row.Deaths);
            h.Now = 21; h.Publish(); Eq(EncounterState.Recovery, h.Remote(202).EncounterState);
            row = h.Remote(202).Players.Single(); Eq(60f, row.LargestHit); Eq(1, row.Deaths);
            h.Now = 182; h.Publish(); Empty(h.Remote(202));
            h.Now = 183; h.Done(2002, 20, 5); h.Publish(); row = h.Remote(202).Players.Single();
            Eq(5f, row.LargestHit); Eq(0, row.Deaths);
        });
        Test("v2 distant independent rows preserve own metrics through merge", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.AddPeer(303, null); h.Character(202, 2002); h.Character(303, 3003);
            h.Done(2002, 10, 40); h.Done(3003, 20, 70); h.Now = 1;
            h.Adapter.ObservePlayerDeath(2002, "a"); h.Adapter.ObservePlayerDeath(3003, "b1");
            h.Adapter.ObservePlayerDeath(3003, "b2"); h.Publish();
            Eq(40f, h.Remote(202).Players.Single().LargestHit); Eq(1, h.Remote(202).Players.Single().Deaths);
            Eq(70f, h.Remote(303).Players.Single().LargestHit); Eq(2, h.Remote(303).Players.Single().Deaths);
            h.Now = 2; h.Done(2002, 20, 5); h.Publish();
            foreach (long peer in new long[] { 202, 303 })
            {
                var rows = h.Remote(peer).Players;
                Eq(40f, rows.Single(r => r.PlayerId == 2002).LargestHit); Eq(1, rows.Single(r => r.PlayerId == 2002).Deaths);
                Eq(70f, rows.Single(r => r.PlayerId == 3003).LargestHit); Eq(2, rows.Single(r => r.PlayerId == 3003).Deaths);
            }
        });
        Test("reconnect alone never increments deaths or resets retained encounter metrics", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002); h.Done(2002, 10, 60); h.Publish();
            h.Net.Peers.Clear(); h.Adapter.Disconnected(h.Net);
            h.Net.Peers.Add(new ZNetPeer { m_uid = 202, m_playerID = 2002 }); h.Now = 1; h.Publish();
            Eq(0, h.Remote(202).Players.Single().Deaths); Eq(60f, h.Remote(202).Players.Single().LargestHit);
        });
        Test("malformed v2 metrics cannot replace accepted remote snapshot", () =>
        {
            var h = new Host(); h.AddPeer(202, null); h.Character(202, 2002); h.Done(2002, 10, 60); h.Publish();
            var good = h.Remote(202);
            var next = new CombatSnapshot(101, good.SnapshotEpoch, good.Sequence + 1, good.EncounterId, good.EncounterState, good.EncounterElapsedSeconds, good.Players);
            var bytes = CombatSnapshotCodec.Encode(next);
            Array.Copy(BitConverter.GetBytes(-1), 0, bytes, bytes.Length - 4, 4);
            h.DeliverBytes(202, 101, bytes); Eq(good, h.Remote(202));
            bytes = CombatSnapshotCodec.Encode(next); Array.Copy(BitConverter.GetBytes(float.NaN), 0, bytes, bytes.Length - 8, 4);
            h.DeliverBytes(202, 101, bytes); Eq(good, h.Remote(202));
        });
        return _passed;
    }

    private sealed class Host
    {
        internal double Now;
        internal readonly List<Acceptance> Acks = new List<Acceptance>();
        internal readonly ZNet Net = new ZNet { Server = true, SinglePlayer = false };
        internal readonly ZRoutedRpc Rpc = new ZRoutedRpc();
        internal readonly DamageCommitTransport Adapter;
        internal readonly List<(long Peer, CombatSnapshot Snapshot)> Sent = new List<(long, CombatSnapshot)>();
        private readonly Dictionary<long, Client> _clients = new Dictionary<long, Client>();
        internal Host()
        {
            Player.Instances.Clear(); Player.m_localPlayer = AddEntity(101, 1001);
            ZDOMan.instance = new ZDOMan(); Use();
            Adapter = new DamageCommitTransport(() => Now); Adapter.Bind(Net);
            Rpc.Send = (peer, name, package) =>
            {
                if (name == DamageCommitTransport.AckRpc) { Acks.Add(CommitCodec.DecodeAck(package.GetArray()).Result); return; }
                if (name != DamageCommitTransport.SnapshotRpc) return;
                Sent.Add((peer, CombatSnapshotCodec.Decode(package.GetArray())));
                DeliverBytes(peer, 101, package.GetArray());
            };
        }
        internal void Use() { ZNet.instance = Net; ZRoutedRpc.instance = Rpc; ZDOMan.Session = 101; }
        internal Player AddEntity(long owner, long id)
        { var p = new Player { PlayerId = id }; p.View.Zdo.Owner = owner; Player.Instances.Add(p); Adapter?.ObserveIdentityPlayer(p); return p; }
        internal void AddPeer(long peer, long? player, bool ready = true)
        {
            Net.Peers.Add(new ZNetPeer { m_uid = peer, m_playerID = player ?? 0, Ready = ready });
            if (player.HasValue) AddEntity(peer, player.Value);
            var c = new Client(peer, () => Now); _clients.Add(peer, c); Use();
        }
        internal CombatCluster Done(long player, uint npc, float loss = 3)
        {
            Use(); Adapter.Observe(101, new DamageFacts(99, npc, false, null, AttackerClass.Player, player, 1, "NPC", "Player"), loss);
            True(Adapter.Clusters.TryGetClusterForPlayer(player, out var c)); return c;
        }
        internal void Character(long peer, long player, long? owner = null)
        {
            var id = new ZDOID(Guid.NewGuid().ToString());
            Net.Peers.Single(p => p.m_uid == peer).m_characterID = id;
            ZDOMan.instance.Objects[id] = new ZDO { m_uid = id, Owner = owner ?? peer, PlayerId = player };
        }
        internal void Unload(long player)
        {
            foreach (var entity in Player.Instances.Where(p => p.PlayerId == player).ToArray())
            { Adapter.ForgetIdentityPlayer(entity); Player.Instances.Remove(entity); entity.Destroyed = true; }
        }
        internal void Publish() { Use(); Adapter.Update(); }
        internal CombatSnapshot Local { get { True(Adapter.SnapshotStore.TryGetLatest(out var s)); return s; } }
        internal CombatSnapshot Remote(long peer) { True(_clients[peer].Adapter.SnapshotStore.TryGetLatest(out var s)); return s; }
        internal void Deliver(long peer, long sender, CombatSnapshot snapshot) => DeliverBytes(peer, sender, CombatSnapshotCodec.Encode(snapshot));
        internal void DeliverBytes(long peer, long sender, byte[] bytes)
        {
            True(_clients.TryGetValue(peer, out var c)); c.Use();
            try { c.Rpc.Handlers[DamageCommitTransport.SnapshotRpc](sender, new ZPackage(bytes)); } finally { Use(); }
        }
    }
    private sealed class Client
    {
        internal readonly long Peer;
        internal readonly ZNet Net = new ZNet { Server = false, SinglePlayer = false, ServerPeer = new ZNetPeer { m_uid = 101 } };
        internal readonly ZRoutedRpc Rpc = new ZRoutedRpc();
        internal readonly DamageCommitTransport Adapter;
        internal Client(long peer, Func<double> now) { Peer = peer; Use(); Adapter = new DamageCommitTransport(now); Adapter.Bind(Net); }
        internal void Use() { ZNet.instance = Net; ZRoutedRpc.instance = Rpc; ZDOMan.Session = Peer; }
    }
    private static void Empty(CombatSnapshot s) { Eq(0L, s.EncounterId); Eq(EncounterState.NoEncounter, s.EncounterState); Eq(0d, s.EncounterElapsedSeconds); Eq(0, s.Players.Count); }
    private static void Rows(CombatSnapshot s, params long[] ids) { True(ids.SequenceEqual(s.Players.Select(p => p.PlayerId))); }
    private static void Test(string name, Action action) { action(); _passed++; Console.WriteLine("PASS peer snapshots: " + name); }
    private static void Eq<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool condition) { if (!condition) throw new Exception("Peer snapshot assertion failed"); }
}
