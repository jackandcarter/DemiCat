using System;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class FcChatWindow : ChatWindow
{
    private readonly PresenceSidebar? _presenceSidebar;
    private float _presenceWidth = 150f;

    public FcChatWindow(
        Config config,
        HttpClient httpClient,
        DiscordPresenceService? presence,
        TokenManager tokenManager,
        ChannelService channelService,
        ChannelSelectionService channelSelection,
        AvatarCache avatarCache,
        EmojiManager emojiManager)
        : base(
            config,
            httpClient,
            presence,
            tokenManager,
            channelService,
            channelSelection,
            global::DemiCatPlugin.ChannelKind.FcChat,
            avatarCache,
            emojiManager)
    {
        if (presence != null)
        {
            _presenceSidebar = new PresenceSidebar(presence) { TextureLoader = LoadTexture };
        }
    }

    public override void StartNetworking()
    {
        if (!_config.SyncedChat || !_config.EnableFcChat)
        {
            return;
        }
        base.StartNetworking();
    }

    public override void Draw()
    {
        if (!_config.SyncedChat)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }

        if (!_tokenManager.IsReady())
        {
            base.Draw();
            return;
        }

        _ = RoleCache.EnsureLoaded(_httpClient, _config);

        if (_presenceSidebar != null)
        {
            _presenceSidebar.Draw(ref _presenceWidth);
            ImGui.SameLine();
        }

        ImGui.BeginChild("##fcChat", ImGui.GetContentRegionAvail(), false);
        base.Draw();
        ImGui.EndChild();
    }

    public override Task RefreshMessages()
    {
        if (!_config.SyncedChat || !_config.EnableFcChat)
        {
            return Task.CompletedTask;
        }
        return base.RefreshMessages();
    }

    protected override Task FetchChannels(bool refreshed = false)
    {
        if (!_config.SyncedChat || !_config.EnableFcChat)
        {
            return Task.CompletedTask;
        }
        return base.FetchChannels(refreshed);
    }
}

