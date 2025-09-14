using System;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DemiCatPlugin;

public class FcChatWindow : ChatWindow
{
    private readonly PresenceSidebar? _presenceSidebar;
    private float _presenceWidth = 150f;
    private readonly Emoji.EmojiService _emojiService;
    private readonly Emoji.EmojiPicker _emojiPicker;
    private string _chatInput = string.Empty;

    public FcChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence, TokenManager tokenManager, ChannelService channelService, ChannelSelectionService channelSelection)
        : base(config, httpClient, presence, tokenManager, channelService, channelSelection, ChannelKind.FcChat)
    {
        if (presence != null)
        {
            _presenceSidebar = new PresenceSidebar(presence) { TextureLoader = LoadTexture };
        }
        _emojiService = new Emoji.EmojiService(httpClient, tokenManager, config);
        _emojiPicker = new Emoji.EmojiPicker(_emojiService);
        _ = _emojiService.RefreshAsync();
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

        _chatInput = _input;
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 36f);
        ImGui.InputText("##chat_input", ref _chatInput, 2000);
        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (ImGui.Button("😊")) ImGui.OpenPopup("##dc_emoji_picker");
        if (ImGui.BeginPopup("##dc_emoji_picker"))
        {
            _emojiPicker.Draw(ref _chatInput);
            ImGui.EndPopup();
        }
        _input = _chatInput;
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

