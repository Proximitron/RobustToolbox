using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Client.Replays.Loading;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Replays;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;

namespace Robust.UnitTesting.Client.Replays;

[TestFixture]
[TestOf(typeof(ReplayCheckpointStore))]
public sealed class ReplayCheckpointStoreTest : RobustIntegrationTest
{
    private const ushort MetaId = 1;

    [Test]
    public async Task RoundTripsKeyframesDeltasAndImplicitStates()
    {
        var server = StartServer();
        var client = StartClient();
        await Task.WhenAll(server.WaitIdleAsync(), client.WaitIdleAsync());

        // The mapped string package is only locked after the handshake.
        client.SetConnectTarget(server);
        await client.WaitPost(() => client.ResolveDependency<IClientNetManager>().ClientConnect(null!, 0, null!));
        for (var i = 0; i < 10; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
        }

        var serializer = client.ResolveDependency<IRobustSerializer>();
        var sawmill = client.ResolveDependency<ILogManager>().GetSawmill("test");

        await client.WaitAssertion(() =>
        {
            var implicitA2 = new MetaDataComponentState("implicit2", null, null, null);
            var implicitA3 = new MetaDataComponentState("implicit3", null, null, null);
            var implicitList = new List<ComponentChange>
            {
                new(2, implicitA2, GameTick.Zero),
                new(3, implicitA3, GameTick.Zero),
            };
            var implicitSet = new HashSet<ushort> { MetaId, 2, 3 };

            bool Resolve(string proto, out List<ComponentChange> states, out HashSet<ushort> netComps)
            {
                states = implicitList;
                netComps = implicitSet;
                return proto == "A";
            }

            using var store = new ReplayCheckpointStore(serializer, sawmill, 1, Resolve, MetaId);

            var e1 = new NetEntity(1);
            var e2 = new NetEntity(2);
            var e3 = new NetEntity(3);

            var ents = new Dictionary<NetEntity, EntityState>
            {
                // Mostly implicit, shares the implicit NetComponents set.
                [e1] = new(e1, new List<ComponentChange>
                {
                    new(MetaId, new MetaDataComponentState("one", null, "A", null), new GameTick(5)),
                    new(2, implicitA2, GameTick.Zero),
                    new(3, new MetaDataComponentState("custom", null, null, null), new GameTick(7)),
                }, new GameTick(7), implicitSet),
                // Own NetComponents set, one implicit state.
                [e2] = new(e2, new List<ComponentChange>
                {
                    new(MetaId, new MetaDataComponentState("two", null, "A", null), new GameTick(5)),
                    new(2, new MetaDataComponentState("custom2", null, null, null), new GameTick(3)),
                    new(3, implicitA3, GameTick.Zero),
                }, new GameTick(5), [MetaId, 2, 3]),
                // No prototype, nothing to strip.
                [e3] = new(e3, new List<ComponentChange>
                {
                    new(MetaId, new MetaDataComponentState("three", null, null, null), new GameTick(1)),
                }, new GameTick(1)),
            };

            var changed = new HashSet<NetEntity>();
            var deleted = new HashSet<NetEntity>();

            var cp0 = Checkpoint(store, store.Append(new GameTick(10), ents, changed, deleted, []), 10);
            var expected0 = ents.ToDictionary();

            // Delta: e2 loses its implicit state, e3 gets deleted.
            ents[e2] = new(e2, new List<ComponentChange>
            {
                new(MetaId, new MetaDataComponentState("two", null, "A", null), new GameTick(5)),
                new(2, new MetaDataComponentState("custom2", null, null, null), new GameTick(3)),
                new(3, new MetaDataComponentState("custom3", null, null, null), new GameTick(20)),
            }, new GameTick(20), ents[e2].NetComponents);
            ents.Remove(e3);
            changed.Add(e2);
            deleted.Add(e3);

            var cp1 = Checkpoint(store, store.Append(new GameTick(20), ents, changed, deleted, []), 20);
            var expected1 = ents.ToDictionary();

            store.FinishWrites();

            AssertState(cp1.FullState, expected1, implicitSet);
            AssertState(cp0.FullState, expected0, implicitSet);
            AssertState(cp1.FullState, expected1, implicitSet);

            // The restored implicit states must be the shared cached instances, not deserialized copies.
            var loaded1 = cp1.FullState.EntityStates.Value.Single(x => x.NetEntity == e1);
            Assert.That(loaded1.ComponentChanges.Value.Single(x => x.NetID == 2).State, Is.SameAs(implicitA2));
            Assert.That(loaded1.NetComponents, Is.SameAs(implicitSet));
        });

        await client.WaitPost(() => client.ResolveDependency<IClientNetManager>().ClientDisconnect(""));
        await server.WaitRunTicks(5);
        await client.WaitRunTicks(5);
    }

    private static CheckpointState Checkpoint(ReplayCheckpointStore store, int storeIndex, uint tick)
    {
        return new CheckpointState(store, storeIndex, new GameTick(tick), default, new(), (int) tick, []);
    }

    private static void AssertState(GameState state, Dictionary<NetEntity, EntityState> expected, HashSet<ushort> implicitSet)
    {
        var actual = state.EntityStates.Value.ToDictionary(x => x.NetEntity);
        Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));

        foreach (var (ent, exp) in expected)
        {
            var act = actual[ent];
            Assert.That(act.EntityLastModified, Is.EqualTo(exp.EntityLastModified));

            if (ReferenceEquals(exp.NetComponents, implicitSet))
                Assert.That(act.NetComponents, Is.SameAs(implicitSet));
            else if (exp.NetComponents == null)
                Assert.That(act.NetComponents, Is.Null);
            else
                Assert.That(act.NetComponents, Is.EquivalentTo(exp.NetComponents));

            var expComps = exp.ComponentChanges.Value.ToList();
            var actComps = act.ComponentChanges.Value.ToList();
            Assert.That(actComps.Select(x => x.NetID), Is.EqualTo(expComps.Select(x => x.NetID)));
            Assert.That(actComps.Select(x => x.LastModifiedTick), Is.EqualTo(expComps.Select(x => x.LastModifiedTick)));
            Assert.That(
                actComps.Select(x => ((MetaDataComponentState) x.State!).Name),
                Is.EqualTo(expComps.Select(x => ((MetaDataComponentState) x.State!).Name)));
        }
    }
}
