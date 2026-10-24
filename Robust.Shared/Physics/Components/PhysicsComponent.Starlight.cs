using System.Numerics;

namespace Robust.Shared.Physics.Components;

public sealed partial class PhysicsComponent
{
    internal bool ClippingRecoveryActive;
    internal Vector2 ClippingRecoveryDirection;
}
