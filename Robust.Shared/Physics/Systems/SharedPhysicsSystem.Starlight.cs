using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;

namespace Robust.Shared.Physics.Systems;

public abstract partial class SharedPhysicsSystem
{
    private const float ClippingRecoverySpeed = 0.5f;
    // Sub-tile sampling avoids skipping openings when the body starts off-center.
    private const float ClippingRecoveryFieldSize = 0.5f;
    private const float ClippingRecoveryRefinementFieldSize = 0.25f;
    private const int ClippingRecoveryMaxRings = 10;
    private const float ClippingRecoveryStartSeparation = -3f * PhysicsConstants.LinearSlop;

    private bool RecoverClippedBodies(
        in SolverData data,
        in IslandData island,
        ContactPositionConstraint[] positionConstraints,
        Vector2[] positions,
        float[] angles,
        Vector2[] linearVelocities,
        float[] angularVelocities,
        bool prediction)
    {
        var bodyCount = island.Bodies.Count;
        var offset = island.Offset;

        for (var i = 0; i < bodyCount; i++)
        {
            var body = island.Bodies[i].Comp1;
            if (IsClippingRecoveryBody(body))
                body.ClippingRecoveryActive = false;
        }

        for (var i = 0; i < island.Contacts.Count; i++)
        {
            ref readonly var constraint = ref positionConstraints[i];
            var indexA = constraint.IndexA;
            var indexB = constraint.IndexB;
            var bodyA = island.Bodies[indexA].Comp1;
            var bodyB = island.Bodies[indexB].Comp1;

            int clippedIndex;
            if (IsClippingRecoveryBody(bodyA) && bodyB.BodyType == BodyType.Static)
                clippedIndex = indexA;
            else if (IsClippingRecoveryBody(bodyB) && bodyA.BodyType == BodyType.Static)
                clippedIndex = indexB;
            else
                continue;

            var body = island.Bodies[clippedIndex].Comp1;
            if (body.InvMass <= 0f || !body.CanCollide)
                continue;

            var isRecovering = body.ClippingRecoveryDirection != Vector2.Zero;
            if (!isRecovering &&
                (!CanStartClippingRecovery(in data,
                    body,
                    linearVelocities[offset + clippedIndex],
                    angularVelocities[offset + clippedIndex],
                    prediction)))
            {
                continue;
            }

            var separation = GetContactMinSeparation(in constraint, positions, angles);
            if (separation >= (isRecovering ? 0f : ClippingRecoveryStartSeparation))
                continue;

            if (isRecovering)
            {
                body.ClippingRecoveryActive = true;
                continue;
            }

            if (!_fixturesQuery.TryGetComponent(island.Bodies[clippedIndex].Owner, out var fixtures))
                continue;

            var bodyEnt = island.Bodies[clippedIndex];
            var mapId = bodyEnt.Comp2.MapID;
            if (mapId == MapId.Nullspace)
                continue;

            body.ClippingRecoveryDirection = FindClippingRecoveryDirection(
                mapId,
                body,
                fixtures,
                positions[clippedIndex],
                angles[clippedIndex]);
            body.ClippingRecoveryActive = true;
        }

        var moved = false;
        for (var i = 0; i < bodyCount; i++)
        {
            var body = island.Bodies[i].Comp1;
            if (!IsClippingRecoveryBody(body))
                continue;

            if (!body.ClippingRecoveryActive)
            {
                body.ClippingRecoveryDirection = Vector2.Zero;
                continue;
            }

            var direction = body.ClippingRecoveryDirection;
            if (direction == Vector2.Zero)
                continue;

            ref var velocity = ref linearVelocities[offset + i];
            var intoRecoveryDirection = Vector2.Dot(velocity, direction);
            if (intoRecoveryDirection < 0f)
                velocity -= direction * intoRecoveryDirection;

            positions[i] += direction * (ClippingRecoverySpeed * data.FrameTime);
            SetSleepTime(body, 0f);
            moved = true;
        }

        return moved;
    }

    private static void PrepareClippingRecoveryPositionConstraints(
        in IslandData island,
        ContactPositionConstraint[] positionConstraints)
    {
        // Prevent the static position solver from undoing the chosen recovery step.
        for (var i = 0; i < island.Contacts.Count; i++)
        {
            ref var constraint = ref positionConstraints[i];
            var bodyA = island.Bodies[constraint.IndexA].Comp1;
            var bodyB = island.Bodies[constraint.IndexB].Comp1;

            if (bodyA.ClippingRecoveryActive &&
                bodyA.ClippingRecoveryDirection != Vector2.Zero &&
                bodyB.BodyType == BodyType.Static)
            {
                constraint.InvMassA = 0f;
                constraint.InvIA = 0f;
            }

            if (bodyB.ClippingRecoveryActive &&
                bodyB.ClippingRecoveryDirection != Vector2.Zero &&
                bodyA.BodyType == BodyType.Static)
            {
                constraint.InvMassB = 0f;
                constraint.InvIB = 0f;
            }
        }
    }

    private Vector2 FindClippingRecoveryDirection(
        MapId mapId,
        PhysicsComponent body,
        FixturesComponent fixtures,
        Vector2 position,
        float angle)
    {
        var nearestOffset = FindNearestClippingRecoveryOffset(
            mapId,
            body,
            fixtures,
            position,
            angle,
            ClippingRecoveryFieldSize,
            ClippingRecoveryMaxRings * ClippingRecoveryFieldSize);

        if (nearestOffset == Vector2.Zero)
            return Vector2.UnitY;

        if (ClippingRecoveryFieldSize > ClippingRecoveryRefinementFieldSize)
        {
            var refinedOffset = FindNearestClippingRecoveryOffset(
                mapId,
                body,
                fixtures,
                position,
                angle,
                ClippingRecoveryRefinementFieldSize,
                nearestOffset.Length());

            if (refinedOffset != Vector2.Zero)
                nearestOffset = refinedOffset;
        }

        return Vector2.Normalize(nearestOffset);
    }

    private Vector2 FindNearestClippingRecoveryOffset(
        MapId mapId,
        PhysicsComponent body,
        FixturesComponent fixtures,
        Vector2 position,
        float angle,
        float fieldSize,
        float maxDistance)
    {
        var maxSamples = (int)MathF.Ceiling(maxDistance / fieldSize);
        var maxDistanceSquared = maxDistance * maxDistance;
        var nearestOffset = Vector2.Zero;
        var nearestDistanceSquared = float.PositiveInfinity;

        for (var x = -maxSamples; x <= maxSamples; x++)
        {
            for (var y = -maxSamples; y <= maxSamples; y++)
            {
                var offset = new Vector2(x, y) * fieldSize;
                var distanceSquared = offset.LengthSquared();
                if (distanceSquared == 0f ||
                    distanceSquared > maxDistanceSquared ||
                    distanceSquared >= nearestDistanceSquared)
                {
                    continue;
                }

                if (IsPositionColliding(mapId, body, fixtures, position + offset, angle))
                    continue;

                nearestOffset = offset;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return nearestOffset;
    }

    private bool IsPositionColliding(
        MapId mapId,
        PhysicsComponent body,
        FixturesComponent fixtures,
        Vector2 position,
        float angle)
    {
        var bodyTransform = new Transform(angle);
        bodyTransform.Position = position - Physics.Transform.Mul(bodyTransform.Quaternion2D, body.LocalCenter);

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard || (fixture.CollisionLayer == 0 && fixture.CollisionMask == 0))
                continue;

            var query = new FixtureQueryArgs(
                new QueryFilter
                {
                    LayerBits = fixture.CollisionLayer,
                    MaskBits = fixture.CollisionMask,
                    Flags = QueryFlags.Static
                });

            for (var childIndex = 0; childIndex < fixture.Shape.ChildCount; childIndex++)
            {
                var colliding = false;
                _lookup.ForEachFixtureIntersecting(
                    mapId,
                    fixture.Shape,
                    childIndex,
                    bodyTransform,
                    ref colliding,
                    new AnyFixtureOverlapCallback(),
                    query);

                if (colliding)
                    return true;
            }
        }

        return false;
    }

    private static bool CanStartClippingRecovery(
        in SolverData data,
        PhysicsComponent body,
        Vector2 linearVelocity,
        float angularVelocity,
        bool prediction)
    {
        return !prediction &&
            (body.BodyType == BodyType.KinematicController ||
                data.SleepAllowed &&
                body.SleepingAllowed &&
                body.SleepTime + data.FrameTime >= data.TimeToSleep &&
                linearVelocity.LengthSquared() <= data.LinTolSqr &&
                angularVelocity * angularVelocity <= data.AngTolSqr);
    }

    private static bool IsClippingRecoveryBody(PhysicsComponent body) =>
        body.BodyType is BodyType.Dynamic or BodyType.KinematicController;

    private static float GetContactMinSeparation(
        in ContactPositionConstraint constraint,
        Vector2[] positions,
        float[] angles)
    {
        var indexA = constraint.IndexA;
        var indexB = constraint.IndexB;

        var xfA = new Transform(angles[indexA]);
        var xfB = new Transform(angles[indexB]);
        xfA.Position = positions[indexA] - Physics.Transform.Mul(xfA.Quaternion2D, constraint.LocalCenterA);
        xfB.Position = positions[indexB] - Physics.Transform.Mul(xfB.Quaternion2D, constraint.LocalCenterB);

        var minSeparation = 0f;
        for (var i = 0; i < constraint.PointCount; i++)
        {
            PositionSolverManifoldInitialize(constraint, i, xfA, xfB, out _, out _, out var separation);
            minSeparation = MathF.Min(minSeparation, separation);
        }

        return minSeparation;
    }

    private readonly struct AnyFixtureOverlapCallback : IFixtureQueryCallback<bool>
    {
        public bool Invoke(ref bool state, in FixtureProxy fixture)
        {
            state = true;
            return false;
        }
    }
}
