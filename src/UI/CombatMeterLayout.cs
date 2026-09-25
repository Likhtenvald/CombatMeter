using System;

namespace DiagnosticDamageProbe.UI;

internal readonly struct UiPoint
{
    internal readonly float X, Y;
    internal UiPoint(float x, float y) { X = x; Y = y; }
}

internal readonly struct UiSize
{
    internal readonly float Width, Height;
    internal UiSize(float width, float height) { Width = width; Height = height; }
}

internal static class CombatMeterLayout
{
    internal const float DefaultX = 24f;
    internal const float DefaultY = -150f;
    internal const float DefaultScale = 1f;
    internal const float DefaultWidth = 480f;
    internal const float DefaultOpacity = 0.65f;
    internal const float MinScale = 0.5f;
    internal const float MaxScale = 2f;
    internal const float MinWidth = 350f;
    internal const float MaxWidth = 800f;
    internal const float Margin = 10f;

    internal static UiPoint DefaultPosition() => new UiPoint(DefaultX, DefaultY);
    internal static float SanitizeScale(float value) => ClampFinite(value, DefaultScale, MinScale, MaxScale);
    internal static float SanitizeWidth(float value) => ClampFinite(value, DefaultWidth, MinWidth, MaxWidth);
    internal static float SanitizeOpacity(float value) => ClampFinite(value, DefaultOpacity, 0f, 1f);

    // Top-left anchor/pivot: X grows right, Y grows up (positions below the top edge are negative).
    internal static UiPoint Clamp(UiPoint position, UiSize window, UiSize canvas, float scale)
    {
        scale = SanitizeScale(scale);
        if (!Finite(canvas.Width) || !Finite(canvas.Height) || canvas.Width <= 0f || canvas.Height <= 0f) return DefaultPosition();
        float visibleWidth = Math.Min(Math.Max(0f, window.Width * scale), Math.Max(0f, canvas.Width - 2f * Margin));
        float visibleHeight = Math.Min(Math.Max(0f, window.Height * scale), Math.Max(0f, canvas.Height - 2f * Margin));
        float x = Finite(position.X) ? position.X : DefaultX;
        float y = Finite(position.Y) ? position.Y : DefaultY;
        x = Math.Max(Margin, Math.Min(x, canvas.Width - Margin - visibleWidth));
        y = Math.Min(-Margin, Math.Max(y, -(canvas.Height - Margin - visibleHeight)));
        return new UiPoint(x, y);
    }

    private static float ClampFinite(float value, float fallback, float min, float max)
    {
        if (!Finite(value)) value = fallback;
        return Math.Max(min, Math.Min(max, value));
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
