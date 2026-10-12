using System;
using System.Buffers;
using System.Numerics;
using Robust.Shared.Maths;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Graphics.Clyde;

internal sealed partial class Clyde
{
    /// <summary>
    /// <see cref="Robust.Shared.CVars.DisplayPrimitiveColorFix"/>.
    /// </summary>
    private bool _primitiveColorFix;

    private Color _lastPrimitiveLinear = new(-2f, -2f, -2f, -2f);
    private Color _lastPrimitiveSrgb;

    private ReadOnlySpan<DrawVertexUV2DColor> PrimitiveColorsForShader(
        ReadOnlySpan<DrawVertexUV2DColor> vertices,
        out DrawVertexUV2DColor[]? rented)
    {
        rented = null;
        if (!_primitiveColorFix)
            return vertices;

        rented = ArrayPool<DrawVertexUV2DColor>.Shared.Rent(vertices.Length);
        var span = rented.AsSpan(0, vertices.Length);
        vertices.CopyTo(span);

        foreach (ref var vertex in span)
        {
            var color = vertex.Color;

            if (color.R < 0)
                continue;

            if (!color.Equals(_lastPrimitiveLinear))
            {
                _lastPrimitiveLinear = color;
                _lastPrimitiveSrgb = Color.ToSrgb(color);
            }

            vertex.Color = _lastPrimitiveSrgb;
        }

        return span;
    }

    internal float DrawGetPixelScale()
    {
        var size = _currentRenderTarget.Size;
        if (size.X <= 0 || size.Y <= 0)
            return 1f;

        var m = _currentMatrixModel * _currentMatrixView * _currentMatrixProj;
        var half = new Vector2(size.X, size.Y) / 2;
        var x = new Vector2(m.M11 * half.X, m.M12 * half.Y).Length();
        var y = new Vector2(m.M21 * half.X, m.M22 * half.Y).Length();
        var scale = MathF.Max(x, y);
        return float.IsFinite(scale) && scale > 0 ? scale : 1f;
    }
}
