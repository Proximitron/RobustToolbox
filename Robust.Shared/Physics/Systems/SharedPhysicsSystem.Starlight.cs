using System;
using System.Buffers;
using System.Numerics;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;

namespace Robust.Shared.Physics.Systems;

/*
 * All of this solves the issue of entities getting stuck in walls or close to it forever
 * Ending up causing physics movements and massiv lag until the round ends
 */

public abstract partial class SharedPhysicsSystem
{
    private const float ClippingRecoveryMaxPush = 0.5f;

    private const float PersistentRecoveryMaxGravityAlignment = 0.5f;

    private void RecoverClippedBodies(
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

                var body = clippedIndex == indexA ? bodyA : bodyB;
                if (body.InvMass <= 0f || !body.CanCollide)
                    continue;

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
                return;

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
            }
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
}
