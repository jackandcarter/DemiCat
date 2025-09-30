using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public abstract class DockableWindow
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(1.5);

    private readonly Config _config;
    private readonly Action _openSettings;
    private DateTime _lastFadeReset = DateTime.UtcNow;
    private bool _isOpen;

    protected DockableWindow(Config config, string windowTitle, Action openSettings)
    {
        _config = config;
        WindowTitle = windowTitle;
        _openSettings = openSettings;
    }

    protected Config Config => _config;
    protected string WindowTitle { get; }
    protected Action OpenSettings => _openSettings;

    protected virtual ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.NoCollapse;
    protected virtual bool UseThemedColors => true;
    public virtual bool SupportsFade => false;
    protected virtual float BaseOpacity => 1f;

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

        var open = IsOpen;
        var colorsPushed = 0;
        if (UseThemedColors)
        {
            colorsPushed = PushWindowThemeColors();
        }

        var pushedAlpha = TryPushWindowOpacityOverride(ComputeEffectiveOpacity());

        if (ImGui.Begin(WindowTitle, ref open, WindowFlags))
        {
            if (SupportsFade && (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)
                || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                || ImGui.IsAnyItemActive()))
            {
                ResetFadeTimer();
            }

            DrawContents();
        }
        ImGui.End();

        if (pushedAlpha)
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
        ImGui.SameLine();
        if (ImGui.Button("Open Settings"))
        {
            _openSettings();
        }
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
