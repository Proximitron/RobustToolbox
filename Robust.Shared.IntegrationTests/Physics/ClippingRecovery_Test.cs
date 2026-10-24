using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
internal sealed class ClippingRecoveryTest
{
    [TestCase(BodyType.Dynamic)]
    [TestCase(BodyType.KinematicController)]
    public void EjectsBodyFromCenterOfFiveByFiveWall(BodyType bodyType)
    {
        var simulation = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entityManager = simulation.Resolve<IEntityManager>();
        var fixtureSystem = entityManager.System<FixtureSystem>();
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();
        var broadphaseSystem = entityManager.System<SharedBroadphaseSystem>();
        var transformSystem = entityManager.System<SharedTransformSystem>();
        var (mapUid, _) = simulation.CreateMap();

        physicsSystem.SetGravity(Vector2.Zero);

        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                var wallUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, new Vector2(x, y)));
                var wall = entityManager.AddComponent<PhysicsComponent>(wallUid);
                var manager = entityManager.EnsureComponent<FixturesComponent>(wallUid);
                physicsSystem.SetBodyType(wallUid, BodyType.Static, manager: manager, body: wall);

                var wallShape = new PolygonShape(0.001f);
                wallShape.SetAsBox(0.5f, 0.5f);
                fixtureSystem.CreateFixture(
                    wallUid,
                    "wall",
                    new Fixture(wallShape, 1, 1, true),
                    manager: manager,
                    body: wall);
                physicsSystem.SetCanCollide(wallUid, true, manager: manager, body: wall);
            }
        }

        var itemUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, Vector2.Zero));
        var item = entityManager.AddComponent<PhysicsComponent>(itemUid);
        var itemManager = entityManager.EnsureComponent<FixturesComponent>(itemUid);
        physicsSystem.SetBodyType(itemUid, bodyType, manager: itemManager, body: item);

        var itemShape = new PolygonShape(0.001f);
        itemShape.SetAsBox(0.75f, 0.75f);
        fixtureSystem.CreateFixture(
            itemUid,
            "item",
            new Fixture(itemShape, 1, 1, true),
            manager: itemManager,
            body: item);
        if (bodyType == BodyType.KinematicController)
            physicsSystem.SetSleepingAllowed(itemUid, item, false);
        physicsSystem.WakeBody(itemUid, manager: itemManager, body: item);

        broadphaseSystem.FindNewContacts();
        physicsSystem.CollideContacts();
        Assert.That(item.ContactCount, Is.GreaterThan(0));

        var recoveryDirection = Vector2.Zero;
        var observedRecovery = false;
        var firstRecoveryFrame = -1;
        for (var i = 0; i < 600; i++)
        {
            physicsSystem.Update(1f / 30f);
            if (item.ClippingRecoveryDirection == Vector2.Zero)
                continue;

            if (!observedRecovery)
            {
                recoveryDirection = item.ClippingRecoveryDirection;
                observedRecovery = true;
                firstRecoveryFrame = i;
            }
            else
            {
                Assert.That(item.ClippingRecoveryDirection, Is.EqualTo(recoveryDirection));
            }
        }

        Assert.That(observedRecovery, Is.True);
        if (bodyType == BodyType.KinematicController)
            Assert.That(firstRecoveryFrame, Is.LessThan(5));

        var finalPosition = transformSystem.GetWorldPosition(itemUid);
        Assert.That(
            Vector2.Dot(finalPosition, recoveryDirection),
            Is.GreaterThan(3.2f),
            $"Expected the item to leave the wall block along {recoveryDirection}, but it ended at {finalPosition}.");
        Assert.That(item.ClippingRecoveryDirection, Is.EqualTo(Vector2.Zero));
    }

    [TestCase(BodyType.Dynamic)]
    [TestCase(BodyType.KinematicController)]
    public void PrefersNearbyOpeningOverMoreDistantExit(BodyType bodyType)
    {
        var simulation = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entityManager = simulation.Resolve<IEntityManager>();
        var fixtureSystem = entityManager.System<FixtureSystem>();
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();
        var broadphaseSystem = entityManager.System<SharedBroadphaseSystem>();
        var transformSystem = entityManager.System<SharedTransformSystem>();
        var (mapUid, _) = simulation.CreateMap();

        physicsSystem.SetGravity(Vector2.Zero);

        for (var x = -2; x <= 2; x++)
        {
            for (var y = -2; y <= 2; y++)
            {
                if (x == 2 && y == 1)
                    continue;

                var wallUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, new Vector2(x, y)));
                var wall = entityManager.AddComponent<PhysicsComponent>(wallUid);
                var manager = entityManager.EnsureComponent<FixturesComponent>(wallUid);
                physicsSystem.SetBodyType(wallUid, BodyType.Static, manager: manager, body: wall);

                var wallShape = new PolygonShape(0.001f);
                wallShape.SetAsBox(0.5f, 0.5f);
                fixtureSystem.CreateFixture(
                    wallUid,
                    "wall",
                    new Fixture(wallShape, 1, 1, true),
                    manager: manager,
                    body: wall);
                physicsSystem.SetCanCollide(wallUid, true, manager: manager, body: wall);
            }
        }

        var itemUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, Vector2.Zero));
        var item = entityManager.AddComponent<PhysicsComponent>(itemUid);
        var itemManager = entityManager.EnsureComponent<FixturesComponent>(itemUid);
        physicsSystem.SetBodyType(itemUid, bodyType, manager: itemManager, body: item);

        var itemShape = new PolygonShape(0.001f);
        itemShape.SetAsBox(0.2f, 0.2f);
        fixtureSystem.CreateFixture(
            itemUid,
            "item",
            new Fixture(itemShape, 1, 1, true),
            manager: itemManager,
            body: item);
        physicsSystem.WakeBody(itemUid, manager: itemManager, body: item);

        broadphaseSystem.FindNewContacts();
        physicsSystem.CollideContacts();
        Assert.That(item.ContactCount, Is.GreaterThan(0));

        var recoveryDirection = Vector2.Zero;
        var observedRecovery = false;
        for (var i = 0; i < 600; i++)
        {
            physicsSystem.Update(1f / 30f);
            if (item.ClippingRecoveryDirection == Vector2.Zero)
                continue;

            if (!observedRecovery)
            {
                recoveryDirection = item.ClippingRecoveryDirection;
                observedRecovery = true;
            }
            else
            {
                Assert.That(item.ClippingRecoveryDirection, Is.EqualTo(recoveryDirection));
            }
        }

        var openingDirection = Vector2.Normalize(new Vector2(2f, 1f));
        var finalPosition = transformSystem.GetWorldPosition(itemUid);
        Assert.That(observedRecovery, Is.True);
        Assert.That(
            Vector2.Dot(recoveryDirection, openingDirection),
            Is.GreaterThan(0.95f),
            $"Expected recovery toward the nearby opening along {openingDirection}, but got {recoveryDirection} at {finalPosition}.");

        Assert.That(
            Vector2.Dot(finalPosition, openingDirection),
            Is.GreaterThan(1.5f),
            $"Expected the item to move toward the nearby opening, but it ended at {finalPosition}.");
    }
}
