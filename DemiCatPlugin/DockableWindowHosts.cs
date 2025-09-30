using System;
using Dalamud.Bindings.ImGui;

namespace DemiCatPlugin;

public sealed class EventsDockableWindow : DockableWindow
{
    private readonly UiRenderer _ui;
    private readonly Func<bool> _isLinked;

    public EventsDockableWindow(Config config, UiRenderer ui, Func<bool> isLinked, Action openSettings)
        : base(config, "DemiCat Events", openSettings)
    {
        _ui = ui;
        _isLinked = isLinked;
    }

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

    public EventCreateDockableWindow(Config config, EventCreateWindow window, Func<bool> isLinked, Action openSettings)
        : base(config, "Create Event", openSettings)
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override void OnOpened()
    {
        _window.StartNetworking();
    }

    protected override void DrawContents()
    {
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

    public TemplatesDockableWindow(Config config, TemplatesWindow window, Func<bool> isLinked, Action openSettings)
        : base(config, "Templates", openSettings)
    {
        _window = window;
        _isLinked = isLinked;
    }

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

    public NotePadDockableWindow(Config config, NotePadWindow window, Action openSettings)
        : base(config, "NotePad", openSettings)
    {
        _window = window;
    }

    protected override void DrawContents()
    {
        if (!Config.NotePadEnabled)
        {
            ImGui.TextUnformatted("NotePad is disabled.");
            return;
        }

        _window.Draw();
    }
}

public sealed class RequestBoardDockableWindow : DockableWindow
{
    private readonly RequestBoardWindow _window;
    private readonly Func<bool> _isLinked;

    public RequestBoardDockableWindow(Config config, RequestBoardWindow window, Func<bool> isLinked, Action openSettings)
        : base(config, "Request Board", openSettings)
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override void DrawContents()
    {
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

    public SyncshellDockableWindow(Config config, SyncshellWindow window, Func<bool> isLinked, Action openSettings)
        : base(config, "Syncshell", openSettings)
    {
        _window = window;
        _isLinked = isLinked;
    }

    protected override void DrawContents()
    {
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

    public ChatDockableWindow(
        Config config,
        string windowTitle,
        ChatWindow chatWindow,
        Func<bool> isLinked,
        Func<float> opacityProvider,
        string linkPrompt,
        Action openSettings)
        : base(config, windowTitle, openSettings)
    {
        _chatWindow = chatWindow;
        _isLinked = isLinked;
        _opacityProvider = opacityProvider;
        _linkPrompt = linkPrompt;
    }

    public override bool SupportsFade => true;

    protected override float BaseOpacity => Math.Clamp(_opacityProvider(), 0f, 1f);

    protected override void OnOpened()
    {
        _chatWindow.StartNetworking();
    }

    protected override void OnClosed()
    {
        _chatWindow.StopNetworking();
    }

    protected override void DrawContents()
    {
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
}

public sealed class OfficerChatDockableWindow : ChatDockableWindow
{
    private readonly OfficerChatWindow _officerChat;
    private readonly Func<bool> _hasOfficerAccess;

    public OfficerChatDockableWindow(
        Config config,
        OfficerChatWindow officerChat,
        Func<bool> isLinked,
        Func<bool> hasOfficerAccess,
        Action openSettings)
        : base(
            config,
            "Officer Chat",
            officerChat,
            isLinked,
            () => config.OfficerChatOpacity,
            "Link DemiCat to use officer chat.",
            openSettings)
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
