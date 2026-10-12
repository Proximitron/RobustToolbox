using System;
using System.Buffers;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Robust.Client._Starlight.Graphics;

/// <summary>
/// Circle tessellation used by <c>DrawCircle</c>.
/// </summary>
/// <remarks>
/// See https://github.com/space-wizards/RobustToolbox/issues/2455 and https://siegelord.net/circle_draw.
/// </remarks>
internal static class CircleGeometry
{
    /// <summary>
    /// Max distance between the polygon edge and the true circle, in pixels.
    /// </summary>
    public const float Tolerance = 0.25f;

    public const int MinSegments = 8;
    public const int MaxSegments = 1024;

    public static int SegmentCount(float pixelRadius)
    {
        if (!(pixelRadius > Tolerance * 2))
            return MinSegments;

        var halfAngle = MathF.Acos(1f - Tolerance / pixelRadius);
        var segments = MathF.Ceiling(MathF.PI / halfAngle);
        return (int) Math.Clamp(segments, MinSegments, MaxSegments);
    }

    /// <summary>
    /// Fills <paramref name="points"/> with points on the circle, starting at angle 0 and going counter-clockwise.
    /// </summary>
    public static void Points(Span<Vector2> points, Vector2 center, float radius)
    {
        var step = MathF.Tau / points.Length;
        var cos = MathF.Cos(step);
        var sin = MathF.Sin(step);

        var x = radius;
        var y = 0f;
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Vector2(center.X + x, center.Y + y);

            var t = x;
            x = cos * x - sin * y;
            y = sin * t + cos * y;
        }
    }

    public static void Draw(DrawingHandleBase handle, Vector2 center, float radius, Color color, bool filled, int segments)
    {
        segments = Math.Clamp(segments, MinSegments, MaxSegments);

        var pointCount = filled ? segments + 1 : segments;
        var indexCount = filled ? segments * 3 : segments * 2;

        var points = ArrayPool<Vector2>.Shared.Rent(pointCount);
        var indices = ArrayPool<ushort>.Shared.Rent(indexCount);
        try
        {
            Points(points.AsSpan(0, segments), center, radius);

            if (filled)
            {
                points[segments] = center;
                for (var i = 0; i < segments; i++)
                {
                    indices[i * 3] = (ushort) segments;
                    indices[i * 3 + 1] = (ushort) i;
                    indices[i * 3 + 2] = (ushort) ((i + 1) % segments);
                }

                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList,
                    indices.AsSpan(0, indexCount), points.AsSpan(0, pointCount), color);
            }
            else
            {
                for (var i = 0; i < segments; i++)
                {
                    indices[i * 2] = (ushort) i;
                    indices[i * 2 + 1] = (ushort) ((i + 1) % segments);
                }

                handle.DrawPrimitives(DrawPrimitiveTopology.LineList,
                    indices.AsSpan(0, indexCount), points.AsSpan(0, pointCount), color);
            }
        }
        finally
        {
            ArrayPool<Vector2>.Shared.Return(points);
            ArrayPool<ushort>.Shared.Return(indices);
        }
    }
}
