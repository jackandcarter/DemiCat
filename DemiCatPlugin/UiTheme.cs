using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace DemiCatPlugin;

public static class UiTheme
{
    private struct FrameState
    {
        public uint Frame;
        public Vector4 Primary;
        public Vector4 Secondary;
        public Vector4 Accent;
        public Vector4 BorderColor;
        public Vector4 BorderHoverColor;
        public Vector4 FocusGlowColor;
        public Vector4 SeparatorColor;
        public Vector4 HeaderColor;
        public Vector4 HeaderHoveredColor;
        public Vector4 HeaderActiveColor;
        public Vector4 FrameBgColor;
        public Vector4 FrameBgHoveredColor;
        public Vector4 FrameBgActiveColor;
        public Vector4 ButtonColor;
        public Vector4 ButtonHoveredColor;
        public Vector4 ButtonActiveColor;
        public Vector4 TabColor;
        public Vector4 TabHoveredColor;
        public Vector4 TabActiveColor;
        public Vector4 TextColor;
        public Vector4 CloseButtonColor;
        public Vector4 CloseButtonHoveredColor;
        public Vector4 TitlePillColor;
        public Vector4 TitlePillBorderColor;
    }

    private static readonly Stack<(int Vars, int Colors)> ScopeStack = new();
    private static FrameState _frameState;
    private static bool _draggingWindow;
    private static uint _dragWindowId;
    private static Vector2 _dragOffset;

    public static bool RequestCloseThisFrame { get; private set; }

    public static (int VarCount, int ColorCount) Apply(Config config)
    {
        RequestCloseThisFrame = false;

        var style = ImGui.GetStyle();
        var sanitizedPrimary = Config.SanitizeColor(config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
        var sanitizedSecondary = Config.SanitizeColor(config.SecondaryAccentColor, Config.DefaultSecondaryAccentColor);
        var sanitizedAccent = sanitizedSecondary; // Accent currently derives from secondary until a dedicated color exists.

        sanitizedPrimary.W = ClampAlpha(sanitizedPrimary.W, 0.75f);
        sanitizedSecondary.W = Math.Clamp(sanitizedSecondary.W, 0.2f, 1f);
        sanitizedAccent.W = Math.Clamp(sanitizedAccent.W <= 0f ? sanitizedSecondary.W : sanitizedAccent.W, 0.3f, 1f);

        CacheFrameState(sanitizedPrimary, sanitizedSecondary, sanitizedAccent, style);

        var varCount = 0;
        var colorCount = 0;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f * ImGuiHelpers.GlobalScale);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f * ImGuiHelpers.GlobalScale);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f * ImGuiHelpers.GlobalScale);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 8f * ImGuiHelpers.GlobalScale);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 10f * ImGuiHelpers.GlobalScale);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        varCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        varCount++;

        colorCount += PushColor(ImGuiCol.WindowBg, _frameState.Primary);
        colorCount += PushColor(ImGuiCol.Border, _frameState.BorderColor);
        colorCount += PushColor(ImGuiCol.BorderShadow, new Vector4(_frameState.BorderColor.X, _frameState.BorderColor.Y, _frameState.BorderColor.Z, 0f));
        colorCount += PushColor(ImGuiCol.Separator, _frameState.SeparatorColor);
        colorCount += PushColor(ImGuiCol.SeparatorHovered, _frameState.HeaderHoveredColor);
        colorCount += PushColor(ImGuiCol.SeparatorActive, _frameState.HeaderActiveColor);
        colorCount += PushColor(ImGuiCol.Header, _frameState.HeaderColor);
        colorCount += PushColor(ImGuiCol.HeaderHovered, _frameState.HeaderHoveredColor);
        colorCount += PushColor(ImGuiCol.HeaderActive, _frameState.HeaderActiveColor);
        colorCount += PushColor(ImGuiCol.FrameBg, _frameState.FrameBgColor);
        colorCount += PushColor(ImGuiCol.FrameBgHovered, _frameState.FrameBgHoveredColor);
        colorCount += PushColor(ImGuiCol.FrameBgActive, _frameState.FrameBgActiveColor);
        colorCount += PushColor(ImGuiCol.Button, _frameState.ButtonColor);
        colorCount += PushColor(ImGuiCol.ButtonHovered, _frameState.ButtonHoveredColor);
        colorCount += PushColor(ImGuiCol.ButtonActive, _frameState.ButtonActiveColor);
        colorCount += PushColor(ImGuiCol.SliderGrab, _frameState.ButtonHoveredColor);
        colorCount += PushColor(ImGuiCol.SliderGrabActive, _frameState.ButtonActiveColor);
        colorCount += PushColor(ImGuiCol.CheckMark, _frameState.ButtonActiveColor);
        colorCount += PushColor(ImGuiCol.ResizeGrip, _frameState.ButtonColor);
        colorCount += PushColor(ImGuiCol.ResizeGripHovered, _frameState.ButtonHoveredColor);
        colorCount += PushColor(ImGuiCol.ResizeGripActive, _frameState.ButtonActiveColor);
        colorCount += PushColor(ImGuiCol.Tab, _frameState.TabColor);
        colorCount += PushColor(ImGuiCol.TabHovered, _frameState.TabHoveredColor);
        colorCount += PushColor(ImGuiCol.TabActive, _frameState.TabActiveColor);
        colorCount += PushColor(ImGuiCol.TabUnfocused, _frameState.TabColor);
        colorCount += PushColor(ImGuiCol.TabUnfocusedActive, _frameState.TabActiveColor);
        colorCount += PushColor(ImGuiCol.TitleBg, _frameState.Primary);
        colorCount += PushColor(ImGuiCol.TitleBgActive, _frameState.HeaderActiveColor);
        colorCount += PushColor(ImGuiCol.TitleBgCollapsed, _frameState.Primary);

        var baseText = style.Colors[(int)ImGuiCol.Text];
        var textColor = _frameState.TextColor.W > 0f ? _frameState.TextColor : baseText;
        colorCount += PushColor(ImGuiCol.Text, textColor);

        ScopeStack.Push((varCount, colorCount));
        return (varCount, colorCount);
    }

    public static void Pop()
    {
        if (ScopeStack.Count == 0)
        {
            return;
        }

        var (vars, colors) = ScopeStack.Pop();
        if (colors > 0)
        {
            ImGui.PopStyleColor(colors);
        }

        if (vars > 0)
        {
            ImGui.PopStyleVar(vars);
        }
    }

    public static void DrawWindowChrome(Config config, string? titleOverride = null, Action? onClose = null, float reservedHeight = 0f)
    {
        RequestCloseThisFrame = false;

        var drawList = ImGui.GetWindowDrawList();
        // drawList is valid inside a begun window; no NativePtr check needed

        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        if (windowSize.X <= 0f || windowSize.Y <= 0f)
        {
            return;
        }

        var style = ImGui.GetStyle();
        var rounding = style.WindowRounding;
        var scale = ImGuiHelpers.GlobalScale;
        var highlightHeight = MathF.Min(windowSize.Y * 0.3f, 26f * scale);
        if (highlightHeight > 0.5f)
        {
            var topColor = new Vector4(1f, 1f, 1f, 0.08f);
            var bottomColor = new Vector4(1f, 1f, 1f, 0f);
            var gradientMin = windowPos + new Vector2(style.WindowBorderSize, style.WindowBorderSize);
            var gradientMax = windowPos + new Vector2(windowSize.X - style.WindowBorderSize, style.WindowBorderSize + highlightHeight);
            drawList.AddRectFilledMultiColor(
                gradientMin,
                gradientMax,
                ImGui.ColorConvertFloat4ToU32(topColor),
                ImGui.ColorConvertFloat4ToU32(topColor),
                ImGui.ColorConvertFloat4ToU32(bottomColor),
                ImGui.ColorConvertFloat4ToU32(bottomColor));
        }

        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootWindow | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var borderColor = hovered
            ? AdjustAlpha(_frameState.BorderHoverColor, Math.Clamp(_frameState.BorderHoverColor.W + 0.1f, 0f, 1f))
            : _frameState.BorderColor;
        drawList.AddRect(windowPos, windowPos + windowSize, ImGui.ColorConvertFloat4ToU32(borderColor), rounding, ImDrawFlags.RoundCornersAll, 1.5f);

        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (focused)
        {
            var glowThickness = 3f * scale;
            drawList.AddRect(windowPos - new Vector2(1.5f * scale), windowPos + windowSize + new Vector2(1.5f * scale),
                ImGui.ColorConvertFloat4ToU32(_frameState.FocusGlowColor), rounding + 3f * scale, ImDrawFlags.RoundCornersAll, glowThickness);
        }

        var buttonRadius = 7f * scale;
        var buttonSize = new Vector2(buttonRadius * 2f, buttonRadius * 2f);
        var buttonOffsetY = MathF.Max(2f * scale, style.FramePadding.Y * 0.25f);
        var buttonPos = new Vector2(windowPos.X + style.WindowPadding.X, windowPos.Y + style.WindowPadding.Y + buttonOffsetY);
        var closeRectMin = buttonPos;
        var closeRectMax = buttonPos + buttonSize;

        ImGui.PushID("dc_theme_close");
        var clicked = ImGui.InvisibleButton("##close", buttonSize);
        var buttonHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        ImGui.PopID();

        var buttonCenter = buttonPos + new Vector2(buttonRadius);
        var baseColor = buttonHovered ? _frameState.CloseButtonHoveredColor : _frameState.CloseButtonColor;
        drawList.AddCircleFilled(buttonCenter, buttonRadius, ImGui.ColorConvertFloat4ToU32(baseColor));

        var crossExtent = buttonRadius * 0.5f;
        var crossThickness = Math.Max(1.5f, 1.5f * scale);
        var crossColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));
        drawList.AddLine(buttonCenter + new Vector2(-crossExtent, -crossExtent), buttonCenter + new Vector2(crossExtent, crossExtent), crossColor, crossThickness);
        drawList.AddLine(buttonCenter + new Vector2(-crossExtent, crossExtent), buttonCenter + new Vector2(crossExtent, -crossExtent), crossColor, crossThickness);

        var hasTitle = !string.IsNullOrEmpty(titleOverride);
        Vector2 textPos = default;
        Vector2 pillMin = default;
        Vector2 pillMax = default;

        if (hasTitle)
        {
            var textSize = ImGui.CalcTextSize(titleOverride);
            var pillPadding = new Vector2(style.FramePadding.X * 0.8f, MathF.Max(style.FramePadding.Y * 0.45f, 4f * scale));
            textPos = windowPos + new Vector2(style.WindowPadding.X + buttonSize.X + 10f * scale, style.WindowPadding.Y + style.FramePadding.Y * 0.25f * scale);
            pillMin = textPos - pillPadding;
            pillMax = textPos + textSize + pillPadding;
            pillMin.X = MathF.Max(pillMin.X, windowPos.X + style.WindowPadding.X + buttonSize.X + style.ItemSpacing.X);
            pillMax.X = MathF.Min(pillMax.X, windowPos.X + windowSize.X - style.WindowPadding.X);
            pillMin.Y = MathF.Max(pillMin.Y, windowPos.Y + style.WindowPadding.Y);
            pillMax.Y = MathF.Min(pillMax.Y, windowPos.Y + style.WindowPadding.Y + MathF.Max(reservedHeight, textSize.Y + pillPadding.Y * 2f));

            var roundingRadius = (pillMax.Y - pillMin.Y) * 0.5f;
            drawList.AddRectFilled(pillMin, pillMax, ImGui.ColorConvertFloat4ToU32(_frameState.TitlePillColor), roundingRadius);
            drawList.AddRect(pillMin, pillMax, ImGui.ColorConvertFloat4ToU32(_frameState.TitlePillBorderColor), roundingRadius, ImDrawFlags.RoundCornersAll, 1.5f);

            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(_frameState.TextColor.W > 0f ? _frameState.TextColor : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), titleOverride);
        }

        if (clicked)
        {
            if (onClose != null)
            {
                onClose();
            }
            else
            {
                RequestCloseThisFrame = true;
            }
        }

        var dragHeight = reservedHeight > 0f ? reservedHeight : Math.Max(buttonSize.Y, (hasTitle ? pillMax.Y - pillMin.Y : buttonSize.Y) + style.FramePadding.Y * 0.5f);
        var dragMin = new Vector2(closeRectMax.X + style.ItemSpacing.X, windowPos.Y + style.WindowPadding.Y);
        var dragMax = new Vector2(windowPos.X + windowSize.X - style.WindowPadding.X, dragMin.Y + dragHeight);
        if (hasTitle)
        {
            dragMin.X = MathF.Min(dragMin.X, pillMin.X);
        }

        var windowId = ImGui.GetID("dc_theme_drag_zone");
        var hoveringDragZone = ImGui.IsMouseHoveringRect(dragMin, dragMax, false);
        if (_draggingWindow && _dragWindowId == windowId)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var mousePos = ImGui.GetMousePos();
                ImGui.SetWindowPos(mousePos - _dragOffset);
            }
            else
            {
                _draggingWindow = false;
            }
        }
        else if (hoveringDragZone && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _draggingWindow = true;
            _dragWindowId = windowId;
            _dragOffset = ImGui.GetMousePos() - windowPos;
        }
    }

    public static void DrawSectionSeparator()
    {
        var drawList = ImGui.GetWindowDrawList();
        // drawList is valid inside a begun window; no NativePtr check needed

        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var startPos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var spacing = style.ItemSpacing.Y * 0.5f;
        var thickness = MathF.Max(1f, MathF.Round(1.2f * scale));
        var centerY = startPos.Y + spacing + thickness * 0.5f;
        var min = new Vector2(startPos.X, centerY - thickness * 0.5f);
        var max = new Vector2(startPos.X + width, centerY + thickness * 0.5f);

        var color = _frameState.Frame == (uint)ImGui.GetFrameCount()
            ? _frameState.SeparatorColor
            : ImGui.GetStyle().Colors[(int)ImGuiCol.Separator];
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(color), thickness * 0.5f);

        ImGui.Dummy(new Vector2(0f, spacing * 2f + thickness));
    }

    private static float ClampAlpha(float alpha, float fallback)
    {
        if (alpha <= 0f)
        {
            return fallback;
        }

        return Math.Clamp(alpha, 0.15f, 0.95f);
    }

    private static int PushColor(ImGuiCol idx, Vector4 color)
    {
        ImGui.PushStyleColor(idx, color);
        return 1;
    }

    private static void CacheFrameState(Vector4 primary, Vector4 secondary, Vector4 accent, ImGuiStylePtr style)
    {
        var frame = (uint)ImGui.GetFrameCount();
        if (_frameState.Frame == frame)
        {
            return;
        }

        _frameState.Frame = frame;
        _frameState.Primary = primary;
        _frameState.Secondary = secondary;
        _frameState.Accent = accent;

        var border = Desaturate(secondary, 0.25f);
        border.W = 0.75f;
        _frameState.BorderColor = AdjustAlpha(border, 0.78f);
        _frameState.BorderHoverColor = AdjustAlpha(border, 0.88f);
        _frameState.FocusGlowColor = AdjustAlpha(accent, 0.18f);
        _frameState.SeparatorColor = AdjustAlpha(secondary, 0.6f);

        _frameState.HeaderColor = Blend(primary, accent, 0.2f, Math.Clamp(primary.W + 0.05f, 0f, 1f));
        _frameState.HeaderHoveredColor = Blend(primary, accent, 0.35f, Math.Clamp(primary.W + 0.1f, 0f, 1f));
        _frameState.HeaderActiveColor = Blend(primary, accent, 0.45f, Math.Clamp(primary.W + 0.12f, 0f, 1f));

        _frameState.FrameBgColor = Blend(primary, secondary, 0.12f, Math.Clamp(primary.W + 0.05f, 0f, 1f));
        _frameState.FrameBgHoveredColor = Blend(primary, accent, 0.28f, Math.Clamp(primary.W + 0.08f, 0f, 1f));
        _frameState.FrameBgActiveColor = Blend(primary, accent, 0.4f, Math.Clamp(primary.W + 0.1f, 0f, 1f));

        _frameState.ButtonColor = Blend(primary, secondary, 0.18f, Math.Clamp(primary.W + 0.04f, 0f, 1f));
        _frameState.ButtonHoveredColor = Blend(primary, accent, 0.35f, Math.Clamp(primary.W + 0.08f, 0f, 1f));
        _frameState.ButtonActiveColor = Blend(primary, accent, 0.45f, Math.Clamp(primary.W + 0.12f, 0f, 1f));

        _frameState.TabColor = Blend(primary, secondary, 0.1f, primary.W);
        _frameState.TabHoveredColor = Blend(primary, accent, 0.25f, Math.Clamp(primary.W + 0.05f, 0f, 1f));
        _frameState.TabActiveColor = Blend(primary, accent, 0.4f, Math.Clamp(primary.W + 0.08f, 0f, 1f));

        var textColor = style.Colors[(int)ImGuiCol.Text];
        if (primary.W < 0.25f)
        {
            textColor = AdjustBrightness(textColor, 1.1f);
        }
        _frameState.TextColor = textColor;

        _frameState.CloseButtonColor = AdjustAlpha(secondary, 0.7f);
        _frameState.CloseButtonHoveredColor = AdjustAlpha(accent, 0.85f);

        var pillBase = Blend(primary, accent, 0.28f, Math.Clamp(primary.W + 0.12f, 0f, 1f));
        _frameState.TitlePillColor = AdjustAlpha(pillBase, Math.Clamp(pillBase.W, 0.55f, 0.85f));
        var pillBorder = Blend(_frameState.BorderColor, accent, 0.2f, Math.Clamp(_frameState.BorderHoverColor.W + 0.1f, 0f, 1f));
        _frameState.TitlePillBorderColor = AdjustAlpha(pillBorder, Math.Clamp(pillBorder.W, 0.6f, 0.95f));
    }

    private static Vector4 Blend(Vector4 baseColor, Vector4 accent, float t, float alpha)
    {
        var amount = Math.Clamp(t, 0f, 1f);
        var blended = Vector4.Lerp(
            new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 1f),
            new Vector4(accent.X, accent.Y, accent.Z, 1f),
            amount);
        blended.W = Math.Clamp(alpha, 0f, 1f);
        return blended;
    }

    private static Vector4 AdjustAlpha(Vector4 color, float alpha)
    {
        return new Vector4(color.X, color.Y, color.Z, Math.Clamp(alpha, 0f, 1f));
    }

    private static Vector4 AdjustBrightness(Vector4 color, float factor)
    {
        return new Vector4(
            Math.Clamp(color.X * factor, 0f, 1f),
            Math.Clamp(color.Y * factor, 0f, 1f),
            Math.Clamp(color.Z * factor, 0f, 1f),
            color.W);
    }

    private static Vector4 Desaturate(Vector4 color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        var luma = color.X * 0.299f + color.Y * 0.587f + color.Z * 0.114f;
        return new Vector4(
            color.X + (luma - color.X) * amount,
            color.Y + (luma - color.Y) * amount,
            color.Z + (luma - color.Z) * amount,
            color.W);
    }
}
