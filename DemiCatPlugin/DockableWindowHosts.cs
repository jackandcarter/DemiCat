using System;
using System.Numerics;
using ImGuiNET;

namespace DemiCatPlugin;

public sealed class EventsDockableWindow : DockableWindow
{
    private readonly UiRenderer _ui;
    private readonly Func<bool> _isLinked;

    public EventsDockableWindow(Config config, UiRenderer ui, Func<bool> isLinked)
        : base(config, "DemiCat Events")
    {
        _ui = ui;
        _isLinked = isLinked;
    }

    protected override string? FadePreferenceKey => Config.FadePreferenceKeys.Events;
    protected override Vector2? InitialSize => new(960f, 680f);

    protected override void OnOpened()
    {
        _ = _ui.StartNetworking();
    }

    protected override void OnClosed()
    {
        _ui.StopNetworking();
    }

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!_isLinked())
        {
            DrawLinkPrompt("Link DemiCat to view events.");
            return;
        }

        _ui.Draw();
    }
}

public sealed class EventCreateDockableWindow : DockableWindow
{
    private readonly EventCreateWindow _window;
    private readonly Func<bool> _isLinked;

    public EventCreateDockableWindow(Config config, EventCreateWindow window, Func<bool> isLinked)
        : base(config, "Create Event")
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override string? FadePreferenceKey => Config.FadePreferenceKeys.EventCreate;
    protected override Vector2? InitialSize => new(980f, 720f);

    protected override void OnOpened()
    {
        _window.StartNetworking();
    }

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!_isLinked())
        {
            DrawLinkPrompt("Link DemiCat to create events.");
            return;
        }

        _window.Draw();
    }
}

public sealed class TemplatesDockableWindow : DockableWindow
{
    private readonly TemplatesWindow _window;
    private readonly Func<bool> _isLinked;
    private bool _activated;

    public TemplatesDockableWindow(Config config, TemplatesWindow window, Func<bool> isLinked)
        : base(config, "Templates")
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override string? FadePreferenceKey => Config.FadePreferenceKeys.Templates;
    protected override Vector2? InitialSize => new(1040f, 720f);

    protected override void OnOpened()
    {
        _window.StartNetworking();
        _window.OnTabActivated();
        _activated = true;
    }

    protected override void OnClosed()
    {
        _activated = false;
        _window.StopNetworking();
    }

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!_isLinked())
        {
            DrawLinkPrompt("Link DemiCat to use templates.");
            _activated = false;
            return;
        }

        if (!_activated)
        {
            _window.OnTabActivated();
            _activated = true;
        }

        _window.Draw();
    }
}

public sealed class NotePadDockableWindow : DockableWindow
{
    private readonly NotePadWindow _window;

    public NotePadDockableWindow(Config config, NotePadWindow window)
        : base(config, "NotePad")
    {
        _window = window;
    }

    protected override string? FadePreferenceKey => Config.FadePreferenceKeys.NotePad;
    protected override Vector2? InitialSize => new(1100f, 720f);

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!Config.NotePadEnabled)
        {
            ImGui.TextWrapped("NotePad is disabled. Enable it from the settings accessed via the DemiCat dock icon.");
            return;
        }

        _window.Draw();
    }
}

public sealed class RequestBoardDockableWindow : DockableWindow
{
    private readonly RequestBoardWindow _window;
    private readonly Func<bool> _isLinked;

    public RequestBoardDockableWindow(Config config, RequestBoardWindow window, Func<bool> isLinked)
        : base(config, "Request Board")
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override string? FadePreferenceKey => Config.FadePreferenceKeys.Requests;
    protected override Vector2? InitialSize => new(940f, 680f);

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!_isLinked())
        {
            DrawLinkPrompt("Link DemiCat to view requests.");
            return;
        }

        _window.Draw();
    }
}

public sealed class SyncshellDockableWindow : DockableWindow
{
    private readonly SyncshellWindow _window;
    private readonly Func<bool> _isLinked;

    public SyncshellDockableWindow(Config config, SyncshellWindow window, Func<bool> isLinked)
        : base(config, "Syncshell")
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override string? FadePreferenceKey => Config.FadePreferenceKeys.Syncshell;
    protected override Vector2? InitialSize => new(1100f, 740f);

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!_isLinked())
        {
            DrawLinkPrompt("Link DemiCat to use syncshell.");
            return;
        }

        _window.Draw();
    }
}

public class ChatDockableWindow : DockableWindow
{
    private readonly ChatWindow _chatWindow;
    private readonly Func<bool> _isLinked;
    private readonly Func<float> _opacityProvider;
    private readonly string _linkPrompt;
    private readonly string _fadePreferenceKey;

    public ChatDockableWindow(
        Config config,
        string windowTitle,
        ChatWindow chatWindow,
        Func<bool> isLinked,
        Func<float> opacityProvider,
        string linkPrompt,
        string fadePreferenceKey)
        : base(config, windowTitle)
    {
        _chatWindow = chatWindow;
        _isLinked = isLinked;
        _opacityProvider = opacityProvider;
        _linkPrompt = linkPrompt;
        _fadePreferenceKey = fadePreferenceKey;
    }

    protected override string? FadePreferenceKey => _fadePreferenceKey;
    protected override Vector2? InitialSize => new(840f, 720f);

    protected override bool FadeEnabledByDefault => true;

    protected override float BaseOpacity => Math.Clamp(_opacityProvider(), 0f, 1f);

    protected override void OnOpened()
    {
        if (!_chatWindow.IsNetworkingActive)
        {
            _chatWindow.StartNetworking();
        }
    }

    protected override void DrawContents()
    {
        DrawHeaderWithSettingsButton();

        if (!_isLinked())
        {
            DrawLinkPrompt(_linkPrompt);
            return;
        }

        DrawWhenLinked();
    }

    protected virtual void DrawWhenLinked()
    {
        _chatWindow.Draw();
    }

    protected ChatWindow Chat => _chatWindow;

    public override void OnAppearanceSettingsChanged()
    {
        base.OnAppearanceSettingsChanged();
        _chatWindow.OnAppearanceSettingsChanged();
    }
}

public sealed class OfficerChatDockableWindow : ChatDockableWindow
{
    private readonly OfficerChatWindow _officerChat;
    private readonly Func<bool> _hasOfficerAccess;

    public OfficerChatDockableWindow(
        Config config,
        OfficerChatWindow officerChat,
        Func<bool> isLinked,
        Func<bool> hasOfficerAccess)
        : base(
            config,
            "Officer Chat",
            officerChat,
            isLinked,
            () => config.OfficerChatOpacity,
            "Link DemiCat to use officer chat.",
            Config.FadePreferenceKeys.OfficerChat)
    {
        _officerChat = officerChat;
        _hasOfficerAccess = hasOfficerAccess;
    }

    protected override void DrawWhenLinked()
    {
        if (!_hasOfficerAccess())
        {
            ImGui.TextUnformatted("No officer access for this key.");
            return;
        }

        _officerChat.Draw();
    }
}
