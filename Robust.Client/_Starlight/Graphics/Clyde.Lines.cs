using System;
using System.Buffers;
using System.Numerics;
using Robust.Shared.Graphics;
using Robust.Shared.Maths;
using SixLabors.ImageSharp.PixelFormats;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Graphics.Clyde;

internal sealed partial class Clyde
{
    private const float SmoothLineHalfWidth = 1f;

    private const float SmoothLineUvLow = 1f / 6f;
    private const float SmoothLineUvHigh = 5f / 6f;

    private const int SmoothLineMaxChunkSegments = 4096;

    private const string DefaultSpriteShaderPath = "/Shaders/Internal/default-sprite.swsl";

    private ClydeTexture _smoothLineTexture = default!;
    private bool _smoothLines = true;

    private ClydeHandle _smoothLineShaderChecked;
    private bool _smoothLineShaderOk;

    private Matrix3x2 _smoothLineToClip;
    private Matrix3x2 _smoothLineFromClip;
    private bool _smoothLineInvertible;

    private void LoadSmoothLineTexture()
    {
        var tent = new SixLabors.ImageSharp.Image<Rgba32>(3, 1);
        tent[0, 0] = new Rgba32(255, 255, 255, 0);
        tent[1, 0] = new Rgba32(255, 255, 255, 255);
        tent[2, 0] = new Rgba32(255, 255, 255, 0);

        var loadParams = TextureLoadParameters.Default;
        loadParams.SampleParameters = new TextureSampleParameters
        {
            Filter = true,
            WrapMode = TextureWrapMode.None,
        };

        _smoothLineTexture = (ClydeTexture) Texture.LoadFromImage(tent, name: "StockTextureSmoothLine", loadParameters: loadParams);
    }

    private bool CanDrawSmoothLines()
    {
        if (!_smoothLines)
            return false;

        var shader = _queuedShader;
        if (shader == _smoothLineShaderChecked)
            return _smoothLineShaderOk;

        _smoothLineShaderChecked = shader;
        _smoothLineShaderOk = _shaderInstances.TryGetValue(shader, out var instance)
                              && _loadedShaders.TryGetValue(instance.ShaderHandle, out var loaded)
                              && loaded.Name == DefaultSpriteShaderPath;

        return _smoothLineShaderOk;
    }

    private bool GetSmoothLineGeometry(in Matrix3x2 toClip, ref Vector2 a, ref Vector2 b, out Vector2 offset)
    {
        offset = default;

        var size = _currentRenderTarget.Size;
        if (size.X <= 0 || size.Y <= 0)
            return false;

        if (toClip != _smoothLineToClip)
        {
            _smoothLineToClip = toClip;
            _smoothLineInvertible = Matrix3x2.Invert(toClip, out _smoothLineFromClip);
        }

        if (!_smoothLineInvertible)
            return false;

        var half = new Vector2(size.X, size.Y) * 0.5f;

        // Pixel coordinates, (0, 0) at the corner of the render target.
        var pa = (Vector2.Transform(a, toClip) + Vector2.One) * half;
        var pb = (Vector2.Transform(b, toClip) + Vector2.One) * half;
        var dir = pb - pa;
        var length = dir.Length();
        if (!(length > 0.0001f) || !float.IsFinite(length))
            return false;

        // Axis aligned lines (UI boxes, graphs) get centered on a pixel row/column so they stay one crisp pixel
        // wide instead of smearing over two half transparent ones. GL_LINES rasterized them the same way.
        var snapped = false;
        if (MathF.Abs(dir.Y) < 0.001f)
        {
            var y = MathF.Floor(pa.Y) + 0.5f;
            pa.Y = y;
            pb.Y = y;
            snapped = true;
        }
        else if (MathF.Abs(dir.X) < 0.001f)
        {
            var x = MathF.Floor(pa.X) + 0.5f;
            pa.X = x;
            pb.X = x;
            snapped = true;
        }

        if (snapped)
        {
            a = Vector2.Transform(pa / half - Vector2.One, _smoothLineFromClip);
            b = Vector2.Transform(pb / half - Vector2.One, _smoothLineFromClip);
        }

        var normal = new Vector2(-dir.Y, dir.X) * (SmoothLineHalfWidth / length);
        offset = Vector2.TransformNormal(normal / half, _smoothLineFromClip);
        return float.IsFinite(offset.X) && float.IsFinite(offset.Y);
    }

    private bool TryDrawSmoothLine(Vector2 a, Vector2 b, Color color)
    {
        if (!CanDrawSmoothLines())
            return false;

        var toClip = _currentMatrixView * _currentMatrixProj;
        if (!GetSmoothLineGeometry(toClip, ref a, ref b, out var offset))
            return true;

        EnsureBatchSpaceAvailable(4, GetQuadBatchIndexCount());
        EnsureBatchState(_smoothLineTexture.TextureId, true, GetQuadBatchPrimitiveType(), _queuedShader);

        var vIdx = BatchVertexIndex;
        BatchVertexData[vIdx + 0] = new Vertex2D(a - offset, new Vector2(SmoothLineUvLow, 0.5f), color);
        BatchVertexData[vIdx + 1] = new Vertex2D(b - offset, new Vector2(SmoothLineUvLow, 0.5f), color);
        BatchVertexData[vIdx + 2] = new Vertex2D(b + offset, new Vector2(SmoothLineUvHigh, 0.5f), color);
        BatchVertexData[vIdx + 3] = new Vertex2D(a + offset, new Vector2(SmoothLineUvHigh, 0.5f), color);
        BatchVertexIndex += 4;
        QuadBatchIndexWrite(BatchIndexData, ref BatchIndexIndex, (ushort) vIdx);

        _debugStats.LastClydeDrawCalls += 1;
        return true;
    }

    private bool TryDrawSmoothLinePrimitives(DrawPrimitiveTopology topology, ClydeHandle textureId,
        ReadOnlySpan<ushort> indices, bool indexed, ReadOnlySpan<Vertex2D> vertices)
    {
        if (topology is not (DrawPrimitiveTopology.LineList or DrawPrimitiveTopology.LineStrip or DrawPrimitiveTopology.LineLoop))
            return false;

        if (textureId != _stockTextureWhite.TextureId || !CanDrawSmoothLines())
            return false;

        var count = indexed ? indices.Length : vertices.Length;
        if (count < 2)
            return true;

        // Upper bound of segments for any of the topologies.
        var maxSegments = topology == DrawPrimitiveTopology.LineList ? count / 2 : count;

        var chunkSegments = Math.Min(Math.Min(maxSegments, SmoothLineMaxChunkSegments),
            Math.Min((BatchVertexData.Length - 1) / 4, BatchIndexData.Length / 6));
        var quadVertices = ArrayPool<Vertex2D>.Shared.Rent(chunkSegments * 4);
        var quadIndices = ArrayPool<ushort>.Shared.Rent(chunkSegments * 6);

        try
        {
            var toClip = _currentMatrixModel * _currentMatrixView * _currentMatrixProj;
            var written = 0;

            void Flush(ref int n)
            {
                if (n == 0)
                    return;

                // Same path as any other indexed triangle DrawPrimitives, so the model matrix is applied in the shader.
                DrawPrimitives(DrawPrimitiveTopology.TriangleList, _smoothLineTexture.TextureId,
                    quadIndices.AsSpan(0, n * 6), quadVertices.AsSpan(0, n * 4));
                n = 0;
            }

            void Segment(ref int n, in Matrix3x2 m, in Vertex2D va, in Vertex2D vb)
            {
                var a = va.Position;
                var b = vb.Position;
                if (!GetSmoothLineGeometry(m, ref a, ref b, out var offset))
                    return;

                var v = n * 4;
                quadVertices[v + 0] = new Vertex2D(a - offset, new Vector2(SmoothLineUvLow, 0.5f), va.Modulate);
                quadVertices[v + 1] = new Vertex2D(b - offset, new Vector2(SmoothLineUvLow, 0.5f), vb.Modulate);
                quadVertices[v + 2] = new Vertex2D(b + offset, new Vector2(SmoothLineUvHigh, 0.5f), vb.Modulate);
                quadVertices[v + 3] = new Vertex2D(a + offset, new Vector2(SmoothLineUvHigh, 0.5f), va.Modulate);

                var i = n * 6;
                quadIndices[i + 0] = (ushort) v;
                quadIndices[i + 1] = (ushort) (v + 1);
                quadIndices[i + 2] = (ushort) (v + 2);
                quadIndices[i + 3] = (ushort) v;
                quadIndices[i + 4] = (ushort) (v + 2);
                quadIndices[i + 5] = (ushort) (v + 3);

                if (++n == chunkSegments)
                    Flush(ref n);
            }

            if (topology == DrawPrimitiveTopology.LineList)
            {
                for (var i = 0; i + 1 < count; i += 2)
                {
                    var ia = indexed ? indices[i] : i;
                    var ib = indexed ? indices[i + 1] : i + 1;
                    if (ia == PrimitiveRestartIndex || ib == PrimitiveRestartIndex)
                        continue;

                    Segment(ref written, toClip, vertices[ia], vertices[ib]);
                }
            }
            else
            {
                // Strips and loops, primitive restart starts a new one.
                var first = -1;
                var previous = -1;
                for (var i = 0; i <= count; i++)
                {
                    var restart = i == count || indexed && indices[i] == PrimitiveRestartIndex;
                    if (restart)
                    {
                        if (topology == DrawPrimitiveTopology.LineLoop && first != -1 && previous != first)
                            Segment(ref written, toClip, vertices[previous], vertices[first]);

                        first = -1;
                        previous = -1;
                        continue;
                    }

                    var index = indexed ? indices[i] : i;
                    if (previous != -1)
                        Segment(ref written, toClip, vertices[previous], vertices[index]);
                    else
                        first = index;

                    previous = index;
                }
            }

            Flush(ref written);
        }
        finally
        {
            ArrayPool<Vertex2D>.Shared.Return(quadVertices);
            ArrayPool<ushort>.Shared.Return(quadIndices);
        }

        return true;
    }
}
