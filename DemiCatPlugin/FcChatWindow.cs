using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DemiCatPlugin;

public class FcChatWindow : ChatWindow
{
    public FcChatWindow(Config config, HttpClient httpClient, DiscordPresenceService? presence, TokenManager tokenManager)
        : base(config, httpClient, presence, tokenManager)
    {
        _channelId = config.FcChannelId;
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

        var originalChatChannel = _config.ChatChannelId;

        _ = RoleCache.EnsureLoaded(_httpClient, _config);

        ImGui.BeginChild("##fcChat", new Vector2(-150, 0), false);
        base.Draw();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##userList", new Vector2(150, -30), true);

        var presences = _presence?.Presences ?? new List<PresenceDto>();
        foreach (var user in presences)
        {
            var color = user.Status == "online"
                ? new Vector4(0f, 1f, 0f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted("●");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (!string.IsNullOrEmpty(user.AvatarUrl) && user.AvatarTexture == null)
            {
                LoadTexture(user.AvatarUrl, t => user.AvatarTexture = t);
            }
            if (user.AvatarTexture != null)
            {
                var wrap = user.AvatarTexture.GetWrapOrEmpty();
                ImGui.Image(wrap.Handle, new Vector2(24, 24));
            }
            else
            {
                ImGui.Dummy(new Vector2(24, 24));
            }
            ImGui.SameLine();
            var label = user.Name;
            foreach (var roleId in user.Roles)
            {
                foreach (var role in RoleCache.Roles)
                {
                    if (role.Id == roleId)
                    {
                        label += $" [{role.Name}]";
                        break;
                    }
                }
            }
            if (ImGui.Selectable(label))
            {
                _input += $"@{user.Name} ";
            }
        }

        foreach (var role in RoleCache.Roles)
        {
            if (ImGui.Selectable($"@{role.Name}"))
            {
                _input += $"@{role.Name} ";
            }
        }

        ImGui.EndChild();

        if (_config.ChatChannelId != originalChatChannel || _config.FcChannelId != _channelId)
        {
            _config.ChatChannelId = originalChatChannel;
            _config.FcChannelId = _channelId;
            SaveConfig();
        }
        else
        {
            _config.ChatChannelId = originalChatChannel;
        }
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

