using System;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Graphics;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;
using Stopwatch = Robust.Shared.Timing.Stopwatch;
using Vertex = Robust.Client.Graphics.DrawVertexUV2DColor;

namespace Robust.Client._Starlight.Graphics;

/// <summary>
/// Represents a custom loading screen for the Starlight.Launcher, featuring a starfield background, animated logo/title, progress bar, and additional visual elements.
/// </summary>
internal sealed class StarlightLoadingScreen
{
    private const string FontPath = "/EngineFonts/NotoSans/NotoSans-Regular.ttf";

    private const string BadgePrefix = "Powered by ";
    private const string BadgeName = "Starlight.Launcher";
    private const string FallbackTitle = "SPACE STATION 14";

    private static readonly Color BackgroundTop = Color.FromHex("#050609");
    private static readonly Color BackgroundBottom = Color.FromHex("#0D0E15");
    private static readonly Color PanelColor = Color.FromHex("#25252A");
    private static readonly Color PanelBorder = Color.FromHex("#3E3E46");
    private static readonly Color AccentColor = Color.FromHex("#4C6FB5");
    private static readonly Color AccentDark = Color.FromHex("#2D4373");
    private static readonly Color AccentLight = Color.FromHex("#8FB0F0");
    private static readonly Color NanoGold = Color.FromHex("#A88B5E");
    private static readonly Color TextColor = Color.FromHex("#E0E0E6");
    private static readonly Color MutedText = Color.FromHex("#8A8A96");

    private static readonly (Color Color, float Alpha, float Radius, Vector2 Anchor, Vector2 Drift, float Speed)[] Nebulae =
    [
        (Color.FromHex("#3A55A8"), 0.20f, 0.55f, new(0.22f, 0.30f), new(0.06f, 0.04f), 0.050f),
        (Color.FromHex("#6A3A9A"), 0.17f, 0.50f, new(0.80f, 0.68f), new(0.05f, 0.06f), 0.040f),
        (Color.FromHex("#2A7A8F"), 0.12f, 0.42f, new(0.62f, 0.18f), new(0.07f, 0.03f), 0.065f),
        (Color.FromHex("#A88B5E"), 0.04f, 0.35f, new(0.30f, 0.82f), new(0.04f, 0.05f), 0.055f),
    ];

    private const int TitleSize = 30;
    private const int TextSize = 11;
    private const int BadgeSize = 10;

    private const float BarWidth = 380;
    private const float BarHeight = 6;
    private const float BarPadding = 3;
    private const float MaxLogoWidthFraction = 0.6f;
    private const float MaxLogoHeightFraction = 0.4f;

    private const float FadeInTime = 0.8f;
    private const int StarCount = 260;
    private const float ShootingStarPeriod = 4.5f;
    private const float ShootingStarDuration = 0.9f;

    private VectorFont? _titleFont;
    private VectorFont? _textFont;
    private VectorFont? _badgeFont;

    private readonly Stopwatch _animationSw = new();
    private readonly Star[] _stars = new Star[StarCount];

    private float _lastTime;
    private float _displayedProgress;
    private float _lastProgress = -1;
    private float _progressChangedAt;

    private struct Star
    {
        public Vector2 Position;
        public float Depth;
        public float Phase;
        public Color Tint;
    }

    public void Initialize(IResourceCache resourceCache, ISawmill sawmill)
    {
        if (resourceCache.TryGetResource<FontResource>(FontPath, out var res))
        {
            _titleFont = new VectorFont(res, TitleSize);
            _textFont = new VectorFont(res, TextSize);
            _badgeFont = new VectorFont(res, BadgeSize);
        }
        else
        {
            sawmill.Error($"Could not load loading screen font: {FontPath}");
        }

        var random = new Random(14);
        for (var i = 0; i < _stars.Length; i++)
        {
            var warm = random.NextSingle();
            _stars[i] = new Star
            {
                Position = new Vector2(random.NextSingle(), random.NextSingle()),
                Depth = MathF.Pow(random.NextSingle(), 2.2f),
                Phase = random.NextSingle() * MathF.Tau,
                Tint = warm < 0.15f ? Color.FromHex("#FFE2B8") : warm < 0.35f ? Color.FromHex("#C8D8FF") : Color.White,
            };
        }

        _animationSw.Restart();
    }

    public void Draw(
        IRenderHandle handle,
        Vector2i screenSize,
        float scale,
        Texture? logo,
        float progress,
        float sectionEnd,
        string? status)
    {
        var screen = handle.DrawingHandleScreen;
        screen.SetTransform(Matrix3x2.Identity);

        var time = (float) _animationSw.Elapsed.TotalSeconds;
        var dt = Math.Clamp(time - _lastTime, 0f, 0.25f);
        _lastTime = time;

        var fade = Ease(Math.Clamp(time / FadeInTime, 0f, 1f));
        var size = (Vector2) screenSize;

        EnsureTextures();
        DrawBackground(screen, size, time);
        DrawDither(screen, size);

        var center = size / 2;
        var bottomOfLogo = DrawLogo(screen, size, center, scale, logo, time, fade);

        UpdateProgress(progress, sectionEnd, time, dt);
        DrawProgress(screen, center with { Y = bottomOfLogo + 36 * scale }, scale, status, time, fade);
        DrawSpinner(screen, size, scale, time, fade);
        DrawBadge(screen, size, scale, time, fade);
    }

    #region Background

    private void DrawBackground(DrawingHandleScreen screen, Vector2 size, float time)
    {
        Span<Vertex> quad = stackalloc Vertex[6];
        GradientQuad(quad, Vector2.Zero, size, BackgroundTop, BackgroundTop, BackgroundBottom, BackgroundBottom);
        screen.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, quad);

        var diagonal = size.Length();
        foreach (var (color, alpha, radius, anchor, drift, speed) in Nebulae)
        {
            var t = time * speed * MathF.Tau;
            var pos = (anchor + new Vector2(MathF.Sin(t) * drift.X, MathF.Cos(t * 0.8f) * drift.Y)) * size;
            var breathe = 1f + 0.06f * MathF.Sin(t * 1.7f);
            DrawGlow(screen, pos, radius * diagonal * 0.5f * breathe, color.WithAlpha(alpha));
        }

        foreach (ref var star in _stars.AsSpan())
        {
            var speed = 0.003f + star.Depth * 0.018f;
            var x = (star.Position.X - time * speed) % 1f;
            if (x < 0)
                x += 1f;

            var pos = new Vector2(x * size.X, star.Position.Y * size.Y);
            var twinkle = 0.6f + 0.4f * MathF.Sin(time * (0.7f + star.Depth * 2.5f) + star.Phase);
            var alpha = (0.18f + 0.7f * star.Depth) * twinkle;
            var px = star.Depth > 0.8f ? 2f : 1f;

            screen.DrawRect(UIBox2.FromDimensions(pos, new Vector2(px, px)), star.Tint.WithAlpha(alpha));

            if (star.Depth > 0.9f)
            {
                var c = pos + new Vector2(px / 2);
                DrawGlow(screen, c, 7f, star.Tint.WithAlpha(0.22f * twinkle), 12);
                screen.DrawRect(new UIBox2(c.X - 5, c.Y - 0.5f, c.X + 5, c.Y + 0.5f), star.Tint.WithAlpha(0.18f * twinkle));
                screen.DrawRect(new UIBox2(c.X - 0.5f, c.Y - 5, c.X + 0.5f, c.Y + 5), star.Tint.WithAlpha(0.18f * twinkle));
            }
        }

        DrawShootingStar(screen, size, time);
    }

    private void DrawShootingStar(DrawingHandleScreen screen, Vector2 size, float time)
    {
        var index = (int) (time / ShootingStarPeriod);
        var local = time - index * ShootingStarPeriod;
        if (index == 0 || local > ShootingStarDuration)
            return;

        var random = new Random(index * 7919);
        var start = new Vector2(0.2f + random.NextSingle() * 0.8f, random.NextSingle() * 0.45f) * size;
        var angle = MathF.PI * (0.72f + random.NextSingle() * 0.12f);
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var travel = size.Length() * (0.25f + random.NextSingle() * 0.15f);

        var t = local / ShootingStarDuration;
        var head = start + dir * travel * Ease(t);
        var length = 140f * MathF.Sin(MathF.PI * t);
        var tail = head - dir * length;
        var alpha = MathF.Sin(MathF.PI * t);

        var normal = new Vector2(-dir.Y, dir.X) * 1.2f;
        var headColor = Color.FromSrgb(Color.White.WithAlpha(0.9f * alpha));
        var tailColor = Color.FromSrgb(AccentLight.WithAlpha(0f));

        Span<Vertex> verts =
        [
            new(head + normal, Vector2.Zero, headColor),
            new(head - normal, Vector2.Zero, headColor),
            new(tail, Vector2.Zero, tailColor),
        ];
        screen.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, verts);
        DrawGlow(screen, head, 6f, Color.White.WithAlpha(0.5f * alpha), 12);
    }

    #endregion

    #region Foreground

    private float DrawLogo(DrawingHandleScreen screen, Vector2 size, Vector2 center, float scale, Texture? logo, float time, float fade)
    {
        var offset = new Vector2(0, MathF.Sin(time * 0.9f) * 3f * scale + (1 - fade) * 18f * scale);
        var glowPulse = 0.8f + 0.2f * MathF.Sin(time * 1.3f);

        if (logo != null)
        {
            var logoSize = (Vector2) logo.Size * scale;

            var maxSize = new Vector2(size.X * MaxLogoWidthFraction, size.Y * MaxLogoHeightFraction);
            logoSize *= MathF.Min(1f, MathF.Min(maxSize.X / logoSize.X, maxSize.Y / logoSize.Y));

            var topLeft = center - logoSize / 2 - new Vector2(0, 40 * scale) + offset;
            DrawGlow(screen, topLeft + logoSize / 2, MathF.Max(logoSize.X, logoSize.Y) * 0.75f, AccentColor.WithAlpha(0.16f * glowPulse * fade));
            screen.DrawTextureRect(logo, UIBox2.FromDimensions(topLeft, logoSize), Color.White.WithAlpha(fade));
            return topLeft.Y + logoSize.Y - offset.Y;
        }

        if (_titleFont == null)
            return center.Y;

        var dims = DrawingHandleScreen.MeasureString(_titleFont, FallbackTitle, scale);
        var titlePos = new Vector2(center.X - dims.X / 2, center.Y - dims.Y - 40 * scale) + offset;
        DrawGlow(screen, titlePos + dims / 2, dims.X * 0.7f, AccentColor.WithAlpha(0.14f * glowPulse * fade));
        screen.DrawString(_titleFont, titlePos, FallbackTitle, scale, TextColor.WithAlpha(fade));

        var lineY = titlePos.Y + dims.Y + 6 * scale;
        var half = dims.X / 2 * Ease(Math.Clamp((time - 0.3f) / 0.8f, 0f, 1f));
        screen.DrawRect(new UIBox2(center.X - half, lineY, center.X + half, lineY + 2 * scale), NanoGold.WithAlpha(fade));
        return lineY + 2 * scale - offset.Y;
    }

    private void UpdateProgress(float progress, float sectionEnd, float time, float dt)
    {
        progress = Math.Clamp(progress, 0f, 1f);
        sectionEnd = Math.Clamp(sectionEnd, progress, 1f);

        if (MathF.Abs(progress - _lastProgress) > 0.0001f)
        {
            _lastProgress = progress;
            _progressChangedAt = time;
        }

        var creep = 1f - MathF.Exp(-(time - _progressChangedAt) / 1.5f);
        var target = progress + (sectionEnd - progress) * 0.85f * creep;

        var next = _displayedProgress + (target - _displayedProgress) * (1f - MathF.Exp(-dt * 6f));
        _displayedProgress = MathF.Max(_displayedProgress, next);
    }

    private void DrawProgress(DrawingHandleScreen screen, Vector2 topCenter, float scale, string? status, float time, float fade)
    {
        var width = BarWidth * scale;
        var height = BarHeight * scale;
        var padding = BarPadding * scale;

        var outer = UIBox2.FromDimensions(
            topCenter - new Vector2(width / 2 + padding, 0),
            new Vector2(width + padding * 2, height + padding * 2));

        screen.DrawRect(outer, PanelColor.WithAlpha(0.85f * fade));
        screen.DrawRect(outer, PanelBorder.WithAlpha(fade), filled: false);

        var fillWidth = width * _displayedProgress;
        var inner = UIBox2.FromDimensions(outer.TopLeft + new Vector2(padding, padding), new Vector2(fillWidth, height));
        if (fillWidth > 0.5f)
        {
            Span<Vertex> quad = stackalloc Vertex[6];
            GradientQuad(quad, inner.TopLeft, inner.Size,
                AccentColor.WithAlpha(fade), AccentLight.WithAlpha(fade), AccentDark.WithAlpha(fade), AccentColor.WithAlpha(fade));
            screen.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, quad);

            var band = 60f * scale;
            var sweep = (time * 0.6f % 1.4f) * (fillWidth + band * 2) - band;
            var bandLeft = MathF.Max(inner.Left, inner.Left + sweep - band / 2);
            var bandRight = MathF.Min(inner.Right, inner.Left + sweep + band / 2);
            if (bandRight > bandLeft)
            {
                var mid = Math.Clamp(inner.Left + sweep, bandLeft, bandRight);
                var clear = Color.White.WithAlpha(0f);
                var bright = Color.White.WithAlpha(0.35f * fade);
                GradientQuad(quad, new Vector2(bandLeft, inner.Top), new Vector2(mid - bandLeft, height), clear, bright, clear, bright);
                screen.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, quad);
                GradientQuad(quad, new Vector2(mid, inner.Top), new Vector2(bandRight - mid, height), bright, clear, bright, clear);
                screen.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, quad);
            }

            var pulse = 0.7f + 0.3f * MathF.Sin(time * 5f);
            DrawGlow(screen, new Vector2(inner.Right, inner.Top + height / 2), 14f * scale, AccentLight.WithAlpha(0.45f * pulse * fade), 16);
        }

        if (_textFont == null)
            return;

        var textY = outer.Bottom + 8 * scale;

        var percent = $"{(int) MathF.Floor(_displayedProgress * 100)}%";
        var percentDims = DrawingHandleScreen.MeasureString(_textFont, percent, scale);
        screen.DrawString(_textFont, new Vector2(outer.Right - percentDims.X, textY), percent, scale, TextColor.WithAlpha(fade));

        var label = string.IsNullOrEmpty(status) ? "Loading" : status;
        var dots = (int) (time * 2.5f) % 4;
        label = Ellipsize(_textFont, label, scale, outer.Width - percentDims.X - 30 * scale) + new string('.', dots);
        screen.DrawString(_textFont, new Vector2(outer.Left, textY), label, scale, MutedText.WithAlpha(fade));
    }

    private static void DrawSpinner(DrawingHandleScreen screen, Vector2 size, float scale, float time, float fade)
    {
        const int dots = 10;
        var radius = 11 * scale;
        var margin = 32 * scale;
        var center = new Vector2(size.X - margin - radius, size.Y - margin - radius);

        var head = time * 1.2f % 1f * dots;
        for (var i = 0; i < dots; i++)
        {
            var angle = i / (float) dots * MathF.Tau - MathF.PI / 2;
            var pos = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

            var distance = (head - i + dots) % dots;
            var strength = 1f - distance / dots;
            strength *= strength;
            screen.DrawCircle(pos, (1.4f + 1.2f * strength) * scale, AccentLight.WithAlpha((0.12f + 0.88f * strength) * fade));
        }
    }

    private void DrawBadge(DrawingHandleScreen screen, Vector2 size, float scale, float time, float fade)
    {
        if (_badgeFont == null)
            return;

        var prefixDims = DrawingHandleScreen.MeasureString(_badgeFont, BadgePrefix, scale);
        var nameDims = DrawingHandleScreen.MeasureString(_badgeFont, BadgeName, scale);

        var padX = 10 * scale;
        var padY = 5 * scale;
        var stripe = 3 * scale;
        var margin = 24 * scale;

        var badgeSize = new Vector2(stripe + padX * 2 + prefixDims.X + nameDims.X, MathF.Max(prefixDims.Y, nameDims.Y) + padY * 2);
        var box = UIBox2.FromDimensions(new Vector2(margin, size.Y - margin - badgeSize.Y), badgeSize);

        var pulse = 0.75f + 0.25f * MathF.Sin(time * 1.5f);
        screen.DrawRect(box, PanelColor.WithAlpha(0.85f * fade));
        screen.DrawRect(box, PanelBorder.WithAlpha(fade), filled: false);
        screen.DrawRect(new UIBox2(box.Left, box.Top, box.Left + stripe, box.Bottom), NanoGold.WithAlpha(pulse * fade));

        var textPos = new Vector2(box.Left + stripe + padX, box.Top + padY);
        textPos.X += screen.DrawString(_badgeFont, textPos, BadgePrefix, scale, MutedText.WithAlpha(fade)).X;
        screen.DrawString(_badgeFont, textPos, BadgeName, scale, TextColor.WithAlpha(fade));
    }

    #endregion

    #region Helpers

    private Texture? _glowTexture;
    private Texture? _noiseTexture;
    private bool _texturesFailed;

    private const int GlowTextureSize = 256;
    private const int NoiseTextureSize = 64;
    private const float DitherStrength = 0.5f / 255f;

    private void EnsureTextures()
    {
        if (_glowTexture != null || _texturesFailed)
            return;

        try
        {
            var clyde = IoCManager.Resolve<IClyde>();
            var random = new Random(1);

            using var glow = new Image<Rgba32>(GlowTextureSize, GlowTextureSize);
            var half = GlowTextureSize / 2f;
            for (var y = 0; y < GlowTextureSize; y++)
            {
                for (var x = 0; x < GlowTextureSize; x++)
                {
                    var dx = x + 0.5f - half;
                    var dy = y + 0.5f - half;
                    var d = MathF.Sqrt(dx * dx + dy * dy) / half;
                    var falloff = d >= 1f ? 0f : (1f - d * d) * (1f - d * d);
                    var alpha = Math.Clamp(falloff * 255f + random.NextSingle() - 0.5f, 0f, 255f);
                    glow[x, y] = new Rgba32(255, 255, 255, (byte) MathF.Round(alpha));
                }
            }

            _glowTexture = clyde.LoadTextureFromImage(glow, "starlight-loading-glow", new TextureLoadParameters
            {
                SampleParameters = new TextureSampleParameters { Filter = true },
                Srgb = true,
            });

            using var noise = new Image<Rgba32>(NoiseTextureSize, NoiseTextureSize);
            for (var y = 0; y < NoiseTextureSize; y++)
            {
                for (var x = 0; x < NoiseTextureSize; x++)
                {
                    var v = (byte) random.Next(256);
                    noise[x, y] = new Rgba32(v, v, v, 255);
                }
            }

            _noiseTexture = clyde.LoadTextureFromImage(noise, "starlight-loading-noise", new TextureLoadParameters
            {
                SampleParameters = new TextureSampleParameters { Filter = false, WrapMode = TextureWrapMode.Repeat },
                Srgb = true,
            });
        }
        catch (Exception)
        {
            _texturesFailed = true;
        }
    }

    private void DrawDither(DrawingHandleScreen screen, Vector2 size)
    {
        if (_noiseTexture == null)
            return;

        screen.DrawTextureRectRegion(_noiseTexture,
            new UIBox2(Vector2.Zero, size),
            new UIBox2(Vector2.Zero, size),
            Color.White.WithAlpha(DitherStrength));
    }

    private void DrawGlow(DrawingHandleScreen screen, Vector2 center, float radius, Color color, int segments = 40)
    {
        if (radius <= 0 || color.A <= 0.001f)
            return;

        if (_glowTexture != null)
        {
            screen.DrawTextureRect(_glowTexture, UIBox2.FromDimensions(center - new Vector2(radius), new Vector2(radius * 2)), color);
            return;
        }

        var inner = Color.FromSrgb(color);
        var outer = Color.FromSrgb(color.WithAlpha(0f));

        Span<Vertex> verts = stackalloc Vertex[segments * 3];
        var prev = center + new Vector2(radius, 0);
        for (var i = 0; i < segments; i++)
        {
            var angle = (i + 1) / (float) segments * MathF.Tau;
            var next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            verts[i * 3] = new Vertex(center, Vector2.Zero, inner);
            verts[i * 3 + 1] = new Vertex(prev, Vector2.Zero, outer);
            verts[i * 3 + 2] = new Vertex(next, Vector2.Zero, outer);
            prev = next;
        }

        screen.DrawPrimitives(DrawPrimitiveTopology.TriangleList, Texture.White, verts);
    }

    private static void GradientQuad(Span<Vertex> verts, Vector2 topLeft, Vector2 size,
        Color topLeftColor, Color topRightColor, Color bottomLeftColor, Color bottomRightColor)
    {
        var tl = new Vertex(topLeft, Vector2.Zero, Color.FromSrgb(topLeftColor));
        var tr = new Vertex(topLeft + new Vector2(size.X, 0), Vector2.Zero, Color.FromSrgb(topRightColor));
        var bl = new Vertex(topLeft + new Vector2(0, size.Y), Vector2.Zero, Color.FromSrgb(bottomLeftColor));
        var br = new Vertex(topLeft + size, Vector2.Zero, Color.FromSrgb(bottomRightColor));

        verts[0] = tl;
        verts[1] = tr;
        verts[2] = br;
        verts[3] = tl;
        verts[4] = br;
        verts[5] = bl;
    }

    private static float Ease(float t)
    {
        // Ease out cubic.
        var inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private static string Ellipsize(Font font, string text, float scale, float maxWidth)
    {
        if (DrawingHandleScreen.MeasureString(font, text, scale).X <= maxWidth)
            return text;

        for (var len = text.Length - 1; len > 0; len--)
        {
            var candidate = string.Concat(text.AsSpan(0, len), "...");
            if (DrawingHandleScreen.MeasureString(font, candidate, scale).X <= maxWidth)
                return candidate;
        }

        return "...";
    }

    #endregion
}
