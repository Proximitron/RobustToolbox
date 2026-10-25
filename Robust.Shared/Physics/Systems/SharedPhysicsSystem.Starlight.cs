using System;
using System.Buffers;
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

    private const float ClippingRecoveryStuckDelay = 0.5f;

    private const float ClippingRecoveryFieldSize = 0.5f;
    private const float ClippingRecoveryMaxFieldSize = 1f;
    private const float ClippingRecoveryMaxDistance = 5f;
    private const float ClippingRecoveryFullScanDistance = 2f;
    private const int ClippingRecoveryMaxOuterRingSamples = 8;
    private const int ClippingRecoveryMaxCollisionQueries = 128;
    private const float ClippingRecoveryStartSeparation = -3f * PhysicsConstants.LinearSlop;
    private const float ClippingRecoveryMaxPush = 0.5f;
    private const float PersistentRecoveryMaxGravityAlignment = 0.5f;

    private bool RecoverClippedBodies(
        in SolverData data,
        in IslandData island,
        ContactPositionConstraint[] positionConstraints,
        Vector2[] positions,
        float[] angles,
        Vector2[] linearVelocities,
        float[] angularVelocities,
        bool contactsOkay,
        bool prediction)
    {
        var bodyCount = island.Bodies.Count;
        var offset = island.Offset;
        var moved = !contactsOkay &&
                    RecoverClippedBodiesSimple(in data,
                        in island,
                        positionConstraints,
                        positions,
                        angles,
                        linearVelocities);

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
                !CanStartClippingRecovery(in data,
                    body,
                    linearVelocities[offset + clippedIndex],
                    angularVelocities[offset + clippedIndex],
                    prediction))
            {
                continue;
            }

            var separation = GetContactMinSeparation(in constraint, positions, angles, out _);
            if (separation >= (isRecovering ? 0f : ClippingRecoveryStartSeparation))
                continue;

            body.ClippingRecoveryActive = true;
        }

        for (var i = 0; i < bodyCount; i++)
        {
            var body = island.Bodies[i].Comp1;
            if (!IsClippingRecoveryBody(body))
                continue;

            if (!body.ClippingRecoveryActive)
            {
                body.ClippingRecoveryDirection = Vector2.Zero;
                body.ClippingRecoveryTime = 0f;
                continue;
            }

            var direction = body.ClippingRecoveryDirection;
            if (direction == Vector2.Zero)
            {
                if (prediction)
                {
                    body.ClippingRecoveryTime = 0f;
                    continue;
                }

                if (body.BodyType == BodyType.Dynamic)
                {
                    body.ClippingRecoveryTime = ClippingRecoveryStuckDelay;
                }
                else
                {
                    body.ClippingRecoveryTime = MathF.Min(
                        ClippingRecoveryStuckDelay,
                        body.ClippingRecoveryTime + data.FrameTime);
                    if (body.ClippingRecoveryTime < ClippingRecoveryStuckDelay)
                        continue;
                }

                var bodyEnt = island.Bodies[i];
                var mapId = bodyEnt.Comp2.MapID;
                if (mapId == MapId.Nullspace ||
                    !_fixturesQuery.TryGetComponent(bodyEnt.Owner, out var fixtures))
                {
                    body.ClippingRecoveryTime = 0f;
                    continue;
                }

                LogManager.GetSawmill("clipping_recovery").Info(
                    "Starting complex clipping recovery search for {EntityName} ({EntityUid})",
                    MetaData(bodyEnt.Owner).EntityName,
                    bodyEnt.Owner);
                direction = FindClippingRecoveryDirection(
                    mapId,
                    body,
                    fixtures,
                    positions[i],
                    angles[i]);
                body.ClippingRecoveryDirection = direction;
            }

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

    private bool RecoverClippedBodiesSimple(
        in SolverData data,
        in IslandData island,
        ContactPositionConstraint[] positionConstraints,
        Vector2[] positions,
        float[] angles,
        Vector2[] linearVelocities)
    {
        var bodyCount = island.Bodies.Count;
        var offset = island.Offset;
        var minRecoverySeparation = -MathF.Max(
            3.0f * PhysicsConstants.LinearSlop,
            data.MaxLinearCorrection);
        var minPersistentRecoverySeparation = -3.0f * PhysicsConstants.LinearSlop;
        var gravity = Gravity;
        var hasGravity = gravity.LengthSquared() > 0f;
        var gravityDirection = hasGravity ? Vector2.Normalize(gravity) : Vector2.Zero;

        float[]? deepest = null;
        Vector2[]? pushDirs = null;
        var moved = false;

        try
        {
            for (var i = 0; i < island.Contacts.Count; i++)
            {
                ref readonly var constraint = ref positionConstraints[i];
                var indexA = constraint.IndexA;
                var indexB = constraint.IndexB;
                var bodyA = island.Bodies[indexA].Comp1;
                var bodyB = island.Bodies[indexB].Comp1;

                int clippedIndex;
                if (bodyA.BodyType == BodyType.Dynamic && bodyB.BodyType == BodyType.Static)
                    clippedIndex = indexA;
                else if (bodyB.BodyType == BodyType.Dynamic && bodyA.BodyType == BodyType.Static)
                    clippedIndex = indexB;
                else
                    continue;

                var body = island.Bodies[clippedIndex].Comp1;
                if (body.InvMass <= 0f || !body.CanCollide ||
                    body.ClippingRecoveryDirection != Vector2.Zero)
                {
                    continue;
                }

                var separation = GetContactMinSeparation(in constraint, positions, angles, out var contactNormal);
                if (contactNormal == Vector2.Zero)
                    continue;

                var sideContact = !hasGravity ||
                                  MathF.Abs(Vector2.Dot(contactNormal, gravityDirection)) <
                                  PersistentRecoveryMaxGravityAlignment;
                var lowMotionForSleepInterval = sideContact
                                                && data.SleepAllowed
                                                && body.SleepingAllowed
                                                && body.SleepTime >= data.TimeToSleep;
                var recoverySeparation = lowMotionForSleepInterval
                    ? minPersistentRecoverySeparation
                    : minRecoverySeparation;

                if (separation >= recoverySeparation)
                    continue;

                if (deepest == null)
                {
                    deepest = ArrayPool<float>.Shared.Rent(bodyCount);
                    pushDirs = ArrayPool<Vector2>.Shared.Rent(bodyCount);
                    Array.Clear(deepest, 0, bodyCount);
                }

                if (separation >= deepest[clippedIndex])
                    continue;

                deepest[clippedIndex] = separation;
                pushDirs![clippedIndex] = clippedIndex == indexA ? -contactNormal : contactNormal;
            }

            if (deepest == null)
                return false;

            for (var i = 0; i < bodyCount; i++)
            {
                var separation = deepest[i];
                if (separation >= 0f)
                    continue;

                var position = positions[i];
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
                    continue;

                var pushDir = pushDirs![i];

                ref var velocity = ref linearVelocities[offset + i];
                var intoWall = Vector2.Dot(velocity, pushDir);
                if (intoWall < 0f)
                    velocity -= pushDir * intoWall;

                var depth = -separation + PhysicsConstants.LinearSlop;
                if (depth > ClippingRecoveryMaxPush)
                    continue;

                positions[i] = position + pushDir * depth;
                moved = true;
            }

            return moved;
        }
        finally
        {
            if (deepest != null)
            {
                ArrayPool<float>.Shared.Return(deepest);
                ArrayPool<Vector2>.Shared.Return(pushDirs!);
            }
        }
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
        var fieldSize = GetClippingRecoverySampleSpacing(fixtures, angle);
        var collisionQueries = 0;
        var offset = FindFirstClippingRecoveryOffset(
            mapId,
            body,
            fixtures,
            position,
            angle,
            fieldSize,
            ClippingRecoveryMaxDistance,
            ref collisionQueries);

        return offset == Vector2.Zero ? Vector2.UnitY : Vector2.Normalize(offset);
    }

    private static float GetClippingRecoverySampleSpacing(FixturesComponent fixtures, float angle)
    {
        var transform = new Transform(angle);
        var bounds = default(Box2);
        var hasBounds = false;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard || (fixture.CollisionLayer == 0 && fixture.CollisionMask == 0))
                continue;

            for (var childIndex = 0; childIndex < fixture.Shape.ChildCount; childIndex++)
            {
                var fixtureBounds = fixture.Shape.ComputeAABB(transform, childIndex);
                if (hasBounds)
                {
                    bounds = bounds.Union(fixtureBounds);
                }
                else
                {
                    bounds = fixtureBounds;
                    hasBounds = true;
                }
            }
        }

        if (!hasBounds)
            return ClippingRecoveryFieldSize;

        var size = bounds.TopRight - bounds.BottomLeft;
        var objectSize = MathF.Max(size.X, size.Y);
        return MathF.Max(
            ClippingRecoveryFieldSize,
            MathF.Min(ClippingRecoveryMaxFieldSize, objectSize * 0.5f));
    }

    private Vector2 FindFirstClippingRecoveryOffset(
        MapId mapId,
        PhysicsComponent body,
        FixturesComponent fixtures,
        Vector2 position,
        float angle,
        float fieldSize,
        float maxDistance,
        ref int collisionQueries)
    {
        var maxRing = (int)MathF.Ceiling(maxDistance / fieldSize);
        var maxDistanceSquared = maxDistance * maxDistance;
        for (var ring = 1; ring <= maxRing; ring++)
        {
            var ringSampleCount = 8 * ring;
            var validSampleCount = 0;
            for (var sampleIndex = 0; sampleIndex < ringSampleCount; sampleIndex++)
            {
                var offset = GetClippingRecoveryRingOffset(ring, sampleIndex, fieldSize);
                if (offset.LengthSquared() <= maxDistanceSquared)
                    validSampleCount++;
            }

            if (validSampleCount == 0)
                continue;

            var remainingQueries = ClippingRecoveryMaxCollisionQueries - collisionQueries;
            var ringSampleLimit = ring * fieldSize <= ClippingRecoveryFullScanDistance
                ? validSampleCount
                : ClippingRecoveryMaxOuterRingSamples;
            var samplesThisRing = Math.Min(Math.Min(validSampleCount, ringSampleLimit), remainingQueries);
            if (samplesThisRing == 0)
                return Vector2.Zero;

            var validSampleIndex = 0;
            var sampledIndex = 0;
            for (var sampleIndex = 0; sampleIndex < ringSampleCount; sampleIndex++)
            {
                var offset = GetClippingRecoveryRingOffset(ring, sampleIndex, fieldSize);
                var distanceSquared = offset.LengthSquared();
                if (distanceSquared > maxDistanceSquared)
                    continue;

                var shouldCheck = samplesThisRing == validSampleCount ||
                                  validSampleIndex == sampledIndex * validSampleCount / samplesThisRing;
                validSampleIndex++;
                if (!shouldCheck)
                    continue;

                sampledIndex++;
                collisionQueries++;
                if (!IsPositionColliding(mapId, body, fixtures, position + offset, angle))
                    return offset;

                if (sampledIndex == samplesThisRing)
                    break;
            }

            if (collisionQueries >= ClippingRecoveryMaxCollisionQueries)
                return Vector2.Zero;
        }

        return Vector2.Zero;
    }

    private static Vector2 GetClippingRecoveryRingOffset(int ring, int sampleIndex, float fieldSize)
    {
        var ringSampleCount = 8 * ring;
        // Start at +Y so floor contacts can recover upward on the first successful sample.
        var perimeterSampleIndex = (sampleIndex + 5 * ring) % ringSampleCount;
        var sideLength = ring * 2;
        var side = perimeterSampleIndex / sideLength;
        var index = perimeterSampleIndex % sideLength;
        var offset = side switch
        {
            0 => new Vector2(-ring + index, -ring),
            1 => new Vector2(ring, -ring + index),
            2 => new Vector2(ring - index, ring),
            _ => new Vector2(-ring, ring - index),
        };
        return offset * fieldSize;
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

    private static bool IsClippingRecoveryBody(PhysicsComponent body) =>
        body.BodyType is BodyType.Dynamic or BodyType.KinematicController;

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

    private static float GetContactMinSeparation(
        in ContactPositionConstraint constraint,
        Vector2[] positions,
        float[] angles,
        out Vector2 contactNormal)
    {
        var indexA = constraint.IndexA;
        var indexB = constraint.IndexB;

        var xfA = new Transform(angles[indexA]);
        var xfB = new Transform(angles[indexB]);
        xfA.Position = positions[indexA] - Physics.Transform.Mul(xfA.Quaternion2D, constraint.LocalCenterA);
        xfB.Position = positions[indexB] - Physics.Transform.Mul(xfB.Quaternion2D, constraint.LocalCenterB);

        var minSeparation = 0.0f;
        contactNormal = Vector2.Zero;
        for (var i = 0; i < constraint.PointCount; i++)
        {
            PositionSolverManifoldInitialize(constraint, i, xfA, xfB, out var normal, out _, out var separation);
            if (separation >= minSeparation)
                continue;

            minSeparation = separation;
            contactNormal = normal;
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
