using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public abstract class DockableWindow
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(1.5);

    private readonly Config _config;
    private bool _hasDrawnHeaderThisFrame;
    private DateTime _lastFadeReset = DateTime.UtcNow;
    private bool _isOpen;

    protected DockableWindow(Config config, string windowTitle)
    {
        _config = config;
        WindowTitle = windowTitle;
    }

    protected Config Config => _config;
    protected string WindowTitle { get; }

    protected virtual ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.NoCollapse;
    protected virtual bool UseThemedColors => true;
    protected virtual string? FadePreferenceKey => null;
    protected virtual bool FadeEnabledByDefault => false;
    public virtual bool SupportsFade => _config.IsWindowFadeEnabled(FadePreferenceKey, FadeEnabledByDefault);
    protected virtual float BaseOpacity => 1f;
    protected virtual Vector2? InitialSize => null;
    protected virtual float WindowCornerRadius => 14f;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;

            _isOpen = value;
            if (_isOpen)
            {
                ResetFadeTimer();
                OnOpened();
            }
            else
            {
                OnClosed();
            }
        }
    }

    protected virtual void OnOpened()
    {
    }

    protected virtual void OnClosed()
    {
    }

    public void Draw()
    {
        if (!IsOpen)
            return;

        if (InitialSize is { } initialSize)
        {
            var sanitizedSize = new Vector2(
                MathF.Max(initialSize.X, 1f),
                MathF.Max(initialSize.Y, 1f));
            ImGui.SetNextWindowSize(sanitizedSize, ImGuiCond.FirstUseEver);
        }

        var open = IsOpen;
        var colorsPushed = 0;
        _hasDrawnHeaderThisFrame = false;
        if (UseThemedColors)
        {
            colorsPushed = PushWindowThemeColors();
        }

        var pushedCornerRadius = TryPushWindowCornerRadius();
        var pushedAlpha = TryPushWindowOpacityOverride(ComputeEffectiveOpacity());

        if (ImGui.Begin(WindowTitle, ref open, WindowFlags))
        {
            if (SupportsFade && (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)
                || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                || ImGui.IsAnyItemActive()))
            {
                ResetFadeTimer();
            }

            DrawHeaderWithSettingsButton();
            DrawContents();
        }
        ImGui.End();

        if (pushedAlpha)
        {
            ImGui.PopStyleVar();
        }

        if (pushedCornerRadius)
        {
            ImGui.PopStyleVar();
        }

        if (UseThemedColors)
        {
            ImGui.PopStyleColor(colorsPushed);
        }

        if (open != IsOpen)
        {
            IsOpen = open;
        }
    }

    public virtual void ResetFadeTimer()
    {
        _lastFadeReset = DateTime.UtcNow;
    }

    public virtual void OnAppearanceSettingsChanged()
    {
    }

    private float ComputeEffectiveOpacity()
    {
        var fadeAlpha = SupportsFade ? ComputeFadeAlpha() : 1f;
        return Math.Clamp(BaseOpacity * fadeAlpha, 0f, 1f);
    }

    private float ComputeFadeAlpha()
    {
        if (!_config.ChatFadeOutEnabled)
            return 1f;

        var delaySeconds = Math.Max(0, _config.ChatFadeOutDelaySeconds);
        var delay = TimeSpan.FromSeconds(delaySeconds);
        var elapsed = DateTime.UtcNow - _lastFadeReset;
        if (elapsed <= delay)
            return 1f;

        var fadeElapsed = elapsed - delay;
        if (fadeElapsed >= FadeDuration)
            return Math.Clamp(_config.ChatFadeOutMinimumAlpha, 0f, 1f);

        var durationSeconds = Math.Max(FadeDuration.TotalSeconds, 0.001);
        var t = (float)(fadeElapsed.TotalSeconds / durationSeconds);
        return Lerp(1f, Math.Clamp(_config.ChatFadeOutMinimumAlpha, 0f, 1f), Math.Clamp(t, 0f, 1f));
    }

    private static float Lerp(float a, float b, float t)
        => a + (b - a) * t;

    private bool TryPushWindowCornerRadius()
    {
        var radius = MathF.Max(WindowCornerRadius, 0f);
        if (radius <= 0f)
        {
            return false;
        }

        var scale = ImGui.GetIO().FontGlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, radius * MathF.Max(scale, 0.01f));
        return true;
    }

    private static bool TryPushWindowOpacityOverride(float alpha)
    {
        if (Math.Abs(alpha - 1f) < 0.0001f)
            return false;

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(alpha, 0f, 1f));
        return true;
    }

    protected int PushWindowThemeColors()
    {
        var primary = Config.SanitizeColor(_config.PrimaryWindowColor, Config.DefaultPrimaryWindowColor);
        var child = AdjustBrightness(primary, 0.9f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(primary, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, WithAlpha(child, 1f));
        return 2;
    }

    protected void DrawLinkPrompt(string message)
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0f, 1f), message);
        ImGui.Spacing();
        ImGui.TextWrapped("Use the DemiCat dock icon to open the settings window when configuration is required.");
    }

    protected void DrawHeaderWithSettingsButton()
    {
        if (_hasDrawnHeaderThisFrame)
            return;

        _hasDrawnHeaderThisFrame = true;

        var style = ImGui.GetStyle();
        var cursorPos = ImGui.GetCursorPos();
        var headerHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y;
        ImGui.SetCursorPos(new Vector2(cursorPos.X, cursorPos.Y + headerHeight));
        ImGui.Separator();
        ImGui.Spacing();
    }

    internal static Vector4 AdjustBrightness(Vector4 color, float factor)
    {
        return new Vector4(
            Math.Clamp(color.X * factor, 0f, 1f),
            Math.Clamp(color.Y * factor, 0f, 1f),
            Math.Clamp(color.Z * factor, 0f, 1f),
            color.W);
    }

    internal static Vector4 WithAlpha(Vector4 color, float alphaMultiplier)
    {
        return new Vector4(color.X, color.Y, color.Z, Math.Clamp(color.W * alphaMultiplier, 0f, 1f));
    }

    protected abstract void DrawContents();
}
