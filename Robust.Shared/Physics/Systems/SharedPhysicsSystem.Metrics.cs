using System.Diagnostics;
using Prometheus;

namespace Robust.Shared.Physics.Systems;

public abstract partial class SharedPhysicsSystem
{
    private enum PhysicsPhase : byte
    {
        PreSolve,
        Broadphase,
        Collide,
        BuildIslands,
        SolveIslands,
        UpdateBodies,
        Cleanup,
        PostSolve,
        Count,
    }

    private static readonly Histogram PhaseHistogram = Metrics.CreateHistogram("robust_physics_phase_usage",
        "Time spent in each phase of a physics tick, summed over sub-steps", new HistogramConfiguration
        {
            LabelNames = new[] {"phase"},
            Buckets = Histogram.ExponentialBuckets(0.000_001, 1.5, 30)
        });

    private static readonly Gauge AwakeBodiesGauge = Metrics.CreateGauge("robust_physics_awake_bodies",
        "Awake physics bodies at the start of the last physics tick");

    private static readonly Gauge ContactsGauge = Metrics.CreateGauge("robust_physics_contacts",
        "Active contacts at the end of the last physics tick");

    private static readonly Gauge IslandsGauge = Metrics.CreateGauge("robust_physics_islands",
        "Islands solved in the last physics sub-step");

    private static readonly Gauge SubstepsGauge = Metrics.CreateGauge("robust_physics_substeps",
        "Physics sub-steps per tick");

    private readonly Histogram.Child[] _phaseChildren = new Histogram.Child[(int) PhysicsPhase.Count];
    private readonly long[] _phaseTicks = new long[(int) PhysicsPhase.Count];

    private void InitializeMetrics()
    {
        for (var i = 0; i < _phaseChildren.Length; i++)
        {
            _phaseChildren[i] = PhaseHistogram.WithLabels(((PhysicsPhase) i).ToString());
        }
    }

    private long AddPhase(PhysicsPhase phase, long start)
    {
        if (!MetricsEnabled)
            return 0;

        var now = Stopwatch.GetTimestamp();
        _phaseTicks[(int) phase] += now - start;
        return now;
    }

    private long PhaseTimestamp() => MetricsEnabled ? Stopwatch.GetTimestamp() : 0;

    private void FlushPhaseMetrics()
    {
        if (!MetricsEnabled)
            return;

        for (var i = 0; i < _phaseTicks.Length; i++)
        {
            _phaseChildren[i].Observe((double) _phaseTicks[i] / Stopwatch.Frequency);
            _phaseTicks[i] = 0;
        }

        ContactsGauge.Set(ContactCount);
        SubstepsGauge.Set(_substeps);
    }
}
