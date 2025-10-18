using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using DemiCatPlugin.Avatars;
using DemiCatPlugin.Emoji;

namespace DemiCatPlugin;

public class FcChatWindow : ChatWindow
{
    protected override bool MentionsEnabled => true;

    public FcChatWindow(
        Config config,
        HttpClient httpClient,
        DiscordPresenceService? presence,
        TokenManager tokenManager,
        ChannelService channelService,
        ChannelSelectionService channelSelection,
        MessageCache messageCache,
        AvatarCache avatarCache,
        EmojiManager emojiManager,
        IChatBridge? chatBridge = null)
        : base(
            config,
            httpClient,
            presence,
            tokenManager,
            channelService,
            channelSelection,
            messageCache,
            global::DemiCatPlugin.ChannelKind.FcChat,
            avatarCache,
            emojiManager,
            chatBridge)
    {
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
        if (!_config.SyncedChat || !_config.EnableFcChat)
        {
            ImGui.TextUnformatted("Feature disabled");
            return;
        }

        if (!_tokenManager.IsReady())
        {
            base.Draw();
            return;
        }

        ImGui.BeginChild("##fcChat", ImGui.GetContentRegionAvail(), false);
        base.Draw();
        ImGui.EndChild();
    }

    public override Task RefreshMessages(CancellationToken cancellationToken = default)
    {
        if (!_config.SyncedChat || !_config.EnableFcChat)
        {
            return Task.CompletedTask;
        }
        return base.RefreshMessages(cancellationToken);
    }

    protected override Task FetchChannels(bool refreshed = false, CancellationToken cancellationToken = default)
    {
        if (!_config.SyncedChat || !_config.EnableFcChat)
        {
            return Task.CompletedTask;
        }
        return base.FetchChannels(refreshed, cancellationToken);
    }

    public void DrawThemedWindow(ref bool isOpen, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => base.DrawThemedWindow("FC Chat", ref isOpen, flags);
}

