using System;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Stopwatch = Robust.Shared.Timing.Stopwatch;


namespace Robust.Client._Starlight.Graphics;

/// <summary>
/// A custom loading screen for Starlight.Launcher, with a star field, progress bar, and optional server-provided splash logo.
/// </summary>
internal sealed class StarlightLoadingScreen
{
    private const string FontPath = "/EngineFonts/NotoSans/NotoSans-Regular.ttf";

    private const string BadgePrefix = "Powered by ";
    private const string BadgeName = "Starlight.Launcher";
    private const string FallbackTitle = "SPACE STATION 14";

    private static readonly Color BackgroundTop = Color.FromHex("#0F0F14");
    private static readonly Color BackgroundBottom = Color.FromHex("#1B1B21");
    private static readonly Color PanelColor = Color.FromHex("#25252A");
    private static readonly Color PanelBorder = Color.FromHex("#3E3E46");
    private static readonly Color AccentColor = Color.FromHex("#4C6FB5");
    private static readonly Color AccentGlow = Color.FromHex("#4C6FB5").WithAlpha(0.25f);
    private static readonly Color NanoGold = Color.FromHex("#A88B5E");
    private static readonly Color TextColor = Color.FromHex("#E0E0E6");
    private static readonly Color MutedText = Color.FromHex("#8A8A96");

    private const int TitleSize = 28;
    private const int TextSize = 11;
    private const int BadgeSize = 10;

    private const float BarWidth = 360;
    private const float BarHeight = 6;
    private const float BarPadding = 3;
    private const float MaxLogoWidthFraction = 0.6f;
    private const float MaxLogoHeightFraction = 0.4f;

    private const int StarCount = 160;
    private const int GradientSteps = 48;

    private VectorFont? _titleFont;
    private VectorFont? _textFont;
    private VectorFont? _badgeFont;

    private readonly Stopwatch _animationSw = new();
    private readonly Vector3[] _stars = new Vector3[StarCount];

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
            _stars[i] = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
        }

        _animationSw.Restart();
    }

    public void Draw(
        IRenderHandle handle,
        Vector2i screenSize,
        float scale,
        Texture? logo,
        float progress,
        string? status)
    {
        var screen = handle.DrawingHandleScreen;
        screen.SetTransform(Matrix3x2.Identity);

        var time = (float) _animationSw.Elapsed.TotalSeconds;

        DrawBackground(screen, screenSize, time);

        var center = new Vector2(screenSize.X / 2f, screenSize.Y / 2f);
        var bottomOfLogo = DrawLogo(screen, screenSize, center, scale, logo);

        DrawProgress(screen, center with { Y = bottomOfLogo + 32 * scale }, scale, progress, status);
        DrawSpinner(screen, screenSize, scale, time);
        DrawBadge(screen, screenSize, scale);
    }

    private void DrawBackground(DrawingHandleScreen screen, Vector2i screenSize, float time)
    {
        var stepHeight = screenSize.Y / (float) GradientSteps;
        for (var i = 0; i < GradientSteps; i++)
        {
            var color = Color.InterpolateBetween(BackgroundTop, BackgroundBottom, i / (float) (GradientSteps - 1));
            screen.DrawRect(new UIBox2(0, i * stepHeight, screenSize.X, (i + 1) * stepHeight + 1), color);
        }

        foreach (var star in _stars)
        {
            var depth = star.Z;
            var x = (star.X - time * 0.004f * (0.3f + depth)) % 1f;
            if (x < 0)
                x += 1f;

            var twinkle = 0.55f + 0.45f * MathF.Sin(time * (0.8f + depth * 2f) + star.X * 50f);
            var alpha = (0.15f + 0.5f * depth) * twinkle;
            var size = depth > 0.85f ? 2f : 1f;

            var pos = new Vector2(x * screenSize.X, star.Y * screenSize.Y);
            screen.DrawRect(UIBox2.FromDimensions(pos, new Vector2(size, size)), Color.White.WithAlpha(alpha));
        }
    }

    private float DrawLogo(DrawingHandleScreen screen, Vector2i screenSize, Vector2 center, float scale, Texture? logo)
    {
        if (logo != null)
        {
            var size = (Vector2) logo.Size * scale;

            var maxSize = new Vector2(screenSize.X * MaxLogoWidthFraction, screenSize.Y * MaxLogoHeightFraction);
            var fit = MathF.Min(1f, MathF.Min(maxSize.X / size.X, maxSize.Y / size.Y));
            size *= fit;

            var topLeft = center - size / 2 - new Vector2(0, 40 * scale);
            screen.DrawTextureRect(logo, UIBox2.FromDimensions(topLeft, size));
            return topLeft.Y + size.Y;
        }

        if (_titleFont == null)
            return center.Y;

        var dims = screen.GetDimensions(_titleFont, FallbackTitle, scale);
        var titlePos = new Vector2(center.X - dims.X / 2, center.Y - dims.Y - 40 * scale);
        screen.DrawString(_titleFont, titlePos, FallbackTitle, scale, TextColor);

        var lineY = titlePos.Y + dims.Y + 6 * scale;
        screen.DrawRect(new UIBox2(center.X - dims.X / 2, lineY, center.X + dims.X / 2, lineY + 2 * scale), NanoGold);
        return lineY + 2 * scale;
    }

    private void DrawProgress(DrawingHandleScreen screen, Vector2 topCenter, float scale, float progress, string? status)
    {
        progress = Math.Clamp(progress, 0f, 1f);

        var width = BarWidth * scale;
        var height = BarHeight * scale;
        var padding = BarPadding * scale;

        var outer = UIBox2.FromDimensions(
            topCenter - new Vector2(width / 2 + padding, 0),
            new Vector2(width + padding * 2, height + padding * 2));

        screen.DrawRect(outer, PanelColor);
        screen.DrawRect(outer, PanelBorder, filled: false);

        var inner = UIBox2.FromDimensions(outer.TopLeft + new Vector2(padding, padding), new Vector2(width * progress, height));
        if (inner.Width > 0)
        {
            screen.DrawRect(new UIBox2(inner.Left, inner.Top - 1, inner.Right, inner.Bottom + 1), AccentGlow);
            screen.DrawRect(inner, AccentColor);
        }

        if (_textFont == null)
            return;

        var textY = outer.Bottom + 8 * scale;

        var percent = $"{(int) MathF.Round(progress * 100)}%";
        var percentDims = screen.GetDimensions(_textFont, percent, scale);
        screen.DrawString(_textFont, new Vector2(outer.Right - percentDims.X, textY), percent, scale, TextColor);

        var label = string.IsNullOrEmpty(status) ? "Loading..." : status;
        var maxLabelWidth = outer.Width - percentDims.X - 12 * scale;
        label = Ellipsize(screen, _textFont, label, scale, maxLabelWidth);
        screen.DrawString(_textFont, new Vector2(outer.Left, textY), label, scale, MutedText);
    }

    private static void DrawSpinner(DrawingHandleScreen screen, Vector2i screenSize, float scale, float time)
    {
        const int dots = 8;
        var radius = 10 * scale;
        var margin = 32 * scale;
        var center = new Vector2(screenSize.X - margin - radius, screenSize.Y - margin - radius);

        var head = time * 1.5f % 1f * dots;
        for (var i = 0; i < dots; i++)
        {
            var angle = i / (float) dots * MathF.Tau - MathF.PI / 2;
            var pos = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

            var distance = (head - i + dots) % dots;
            var alpha = 1f - distance / dots;
            screen.DrawCircle(pos, 2f * scale, AccentColor.WithAlpha(0.15f + 0.85f * alpha * alpha));
        }
    }

    private void DrawBadge(DrawingHandleScreen screen, Vector2i screenSize, float scale)
    {
        if (_badgeFont == null)
            return;

        var prefixDims = screen.GetDimensions(_badgeFont, BadgePrefix, scale);
        var nameDims = screen.GetDimensions(_badgeFont, BadgeName, scale);

        var padX = 10 * scale;
        var padY = 5 * scale;
        var stripe = 3 * scale;
        var margin = 24 * scale;

        var size = new Vector2(stripe + padX * 2 + prefixDims.X + nameDims.X, MathF.Max(prefixDims.Y, nameDims.Y) + padY * 2);
        var box = UIBox2.FromDimensions(new Vector2(margin, screenSize.Y - margin - size.Y), size);

        screen.DrawRect(box, PanelColor.WithAlpha(0.9f));
        screen.DrawRect(box, PanelBorder, filled: false);
        screen.DrawRect(new UIBox2(box.Left, box.Top, box.Left + stripe, box.Bottom), NanoGold);

        var textPos = new Vector2(box.Left + stripe + padX, box.Top + padY);
        textPos.X += screen.DrawString(_badgeFont, textPos, BadgePrefix, scale, MutedText).X;
        screen.DrawString(_badgeFont, textPos, BadgeName, scale, TextColor);
    }

    private static string Ellipsize(DrawingHandleScreen screen, Font font, string text, float scale, float maxWidth)
    {
        if (screen.GetDimensions(font, text, scale).X <= maxWidth)
            return text;

        for (var len = text.Length - 1; len > 0; len--)
        {
            var candidate = string.Concat(text.AsSpan(0, len), "...");
            if (screen.GetDimensions(font, candidate, scale).X <= maxWidth)
                return candidate;
        }

        return "...";
    }
}
