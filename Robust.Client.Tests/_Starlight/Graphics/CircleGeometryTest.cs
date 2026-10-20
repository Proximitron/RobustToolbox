using System.Numerics;
using NUnit.Framework;
using Robust.Client._Starlight.Graphics;

namespace Robust.Client.Tests._Starlight.Graphics;

[TestFixture]
[TestOf(typeof(CircleGeometry))]
internal sealed class CircleGeometryTest
{
    [Test]
    public void SegmentCountGrowsWithRadius()
    {
        Assert.That(CircleGeometry.SegmentCount(0f), Is.EqualTo(CircleGeometry.MinSegments));
        Assert.That(CircleGeometry.SegmentCount(float.NaN), Is.EqualTo(CircleGeometry.MinSegments));
        Assert.That(CircleGeometry.SegmentCount(2f), Is.EqualTo(CircleGeometry.MinSegments));
        Assert.That(CircleGeometry.SegmentCount(1e9f), Is.EqualTo(CircleGeometry.MaxSegments));

        var last = 0;
        foreach (var r in new[] { 1f, 5f, 20f, 100f, 500f, 2000f })
        {
            var n = CircleGeometry.SegmentCount(r);
            Assert.That(n, Is.GreaterThanOrEqualTo(last));
            last = n;
        }
    }

    [TestCase(10f)]
    [TestCase(100f)]
    [TestCase(1000f)]
    public void PolygonStaysWithinTolerance(float radius)
    {
        var n = CircleGeometry.SegmentCount(radius);
        // Distance from the center to the middle of an edge is the closest the polygon gets to the center.
        var sagitta = radius * (1 - System.MathF.Cos(System.MathF.PI / n));
        Assert.That(sagitta, Is.LessThanOrEqualTo(CircleGeometry.Tolerance + 1e-3f));
    }

    [Test]
    public void PointsLieOnCircle()
    {
        var center = new Vector2(3, -7);
        var points = new Vector2[CircleGeometry.MaxSegments];
        CircleGeometry.Points(points, center, 250f);

        foreach (var point in points)
        {
            Assert.That((point - center).Length(), Is.EqualTo(250f).Within(0.05f));
        }

        // Evenly spaced all the way around: the last point is one step before the first.
        var step = (points[1] - points[0]).Length();
        Assert.That((points[0] - points[^1]).Length(), Is.EqualTo(step).Within(0.01f));
    }
}
