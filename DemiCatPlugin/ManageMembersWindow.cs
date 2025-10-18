using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace DemiCatPlugin;

public sealed class ManageMembersWindow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string ConfirmPopupId = "Confirm Member Action##dc_manage_members";

    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private readonly object _sync = new();
    private readonly List<MemberEntry> _members = new();

    private bool _loading;
    private bool _actionInProgress;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private PendingActionKind _pendingAction = PendingActionKind.None;
    private MemberEntry? _pendingMember;
    private bool _needsInitialRefresh = true;

    private enum PendingActionKind
    {
        None,
        Remove,
        Ban
    }

    private sealed record MemberEntry
    {
        public string DiscordUserId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Nickname { get; init; }
        public string? GlobalName { get; init; }
        public string? CharacterName { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public bool IsBanned { get; init; }
    }

    private sealed record MemberDto
    {
        [JsonPropertyName("discordUserId")]
        public string? DiscordUserId { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("nickname")]
        public string? Nickname { get; init; }

        [JsonPropertyName("globalName")]
        public string? GlobalName { get; init; }

        [JsonPropertyName("characterName")]
        public string? CharacterName { get; init; }

        [JsonPropertyName("lastUsedAt")]
        public string? LastUsedAt { get; init; }

        [JsonPropertyName("isBanned")]
        public bool IsBanned { get; init; }
    }

    private readonly struct FetchResult
    {
        public FetchResult(List<MemberEntry> members, string message, bool isError)
        {
            Members = members;
            Message = message;
            IsError = isError;
        }

        public List<MemberEntry> Members { get; }
        public string Message { get; }
        public bool IsError { get; }
    }

    public bool IsOpen { get; set; }

    public ManageMembersWindow(Config config, HttpClient httpClient, TokenManager tokenManager)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
    }

    public void Open()
    {
        IsOpen = true;
        _needsInitialRefresh = true;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _members.Clear();
            _loading = false;
            _actionInProgress = false;
            _statusMessage = string.Empty;
            _statusIsError = false;
            _pendingAction = PendingActionKind.None;
            _pendingMember = null;
            _needsInitialRefresh = true;
        }

        IsOpen = false;
    }

    public void Draw()
    {
        if (!IsOpen)
        {
            return;
        }

        using var scope = new UiStyleScope(_config);
        ImGui.SetNextWindowSize(new Vector2(560f, 480f), ImGuiCond.FirstUseEver);

        var open = IsOpen;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar;
        var began = ImGui.Begin("Manage Members##dc_manage_members", ref open, flags);
        if (began)
        {
            var style = ImGui.GetStyle();
            var chromeHeight = Math.Max(22f * ImGuiHelpers.GlobalScale, ImGui.GetFrameHeight() + style.FramePadding.Y * 1.5f);
            ImGui.SetCursorPos(new Vector2(style.WindowPadding.X, style.WindowPadding.Y));
            ImGui.Dummy(new Vector2(1f, chromeHeight));
            ImGui.SetCursorPos(new Vector2(style.WindowPadding.X, style.WindowPadding.Y + chromeHeight));

            DrawContent();
            UiTheme.DrawWindowChrome(_config, "Manage Members", () => open = false, chromeHeight);
        }
        ImGui.End();
        ImGui.PopStyleVar();

        IsOpen = (!open || UiTheme.RequestCloseThisFrame) ? false : open;
    }

    private void DrawContent()
    {
        if (!OfficerPermissions.HasAccess(_config))
        {
            ImGui.TextUnformatted("Officer permissions are required to manage DemiCat members.");
            return;
        }

        if (!_tokenManager.IsReady())
        {
            ImGui.TextUnformatted("Link DemiCat before managing members.");
            return;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            ImGui.TextUnformatted("Set a valid API base URL in settings to manage members.");
            return;
        }

        if (_needsInitialRefresh)
        {
            _needsInitialRefresh = false;
            StartRefresh();
        }

        bool loading;
        bool actionInProgress;
        string statusMessage;
        bool statusIsError;
        MemberEntry[] members;

        lock (_sync)
        {
            loading = _loading;
            actionInProgress = _actionInProgress;
            statusMessage = _statusMessage;
            statusIsError = _statusIsError;
            members = _members.ToArray();
        }

        if (ImGui.Button("Refresh"))
        {
            StartRefresh(force: true);
        }

        if (loading)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Refreshing…");
        }
        else if (actionInProgress)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Applying changes…");
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            var color = statusIsError
                ? new Vector4(1f, 0.45f, 0.45f, 1f)
                : new Vector4(0.6f, 0.85f, 1f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextWrapped(statusMessage);
            ImGui.PopStyleColor();
        }

        UiTheme.DrawSectionSeparator();

        if (ImGui.BeginChild("##manageMembersList", ImGui.GetContentRegionAvail(), false))
        {
            if (members.Length == 0)
            {
                ImGui.TextUnformatted(loading
                    ? "Loading members…"
                    : "No linked DemiCat users currently have access.");
            }
            else
            {
                DrawMemberTable(members, actionInProgress);
            }
        }
        ImGui.EndChild();

        DrawConfirmationPopup();
    }

    private void DrawMemberTable(MemberEntry[] members, bool disableActions)
    {
        var tableFlags = ImGuiTableFlags.RowBg
            | ImGuiTableFlags.BordersOuterH
            | ImGuiTableFlags.BordersOuterV
            | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##manageMembersTable", 3, tableFlags))
        {
            return;
        }

        ImGui.TableSetupColumn("Member", ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableSetupColumn("Last Activity", ImGuiTableColumnFlags.WidthFixed, 160f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 230f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var member in members)
        {
            ImGui.PushID(member.DiscordUserId);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawMemberName(member);

            ImGui.TableNextColumn();
            DrawLastSeen(member);

            ImGui.TableNextColumn();
            DrawMemberActions(member, disableActions);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawMemberName(MemberEntry member)
    {
        ImGui.TextUnformatted(member.DisplayName);

        if (!string.IsNullOrWhiteSpace(member.Nickname)
            && !string.Equals(member.Nickname, member.DisplayName, StringComparison.Ordinal))
        {
            ImGui.TextDisabled($"Nickname: {member.Nickname}");
        }

        if (!string.IsNullOrWhiteSpace(member.GlobalName)
            && !string.Equals(member.GlobalName, member.DisplayName, StringComparison.Ordinal))
        {
            ImGui.TextDisabled($"Global: {member.GlobalName}");
        }

        if (!string.IsNullOrWhiteSpace(member.CharacterName))
        {
            ImGui.TextDisabled($"Character: {member.CharacterName}");
        }

        ImGui.TextDisabled($"Discord ID: {member.DiscordUserId}");

        if (member.IsBanned)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "Banned");
        }
    }

    private static void DrawLastSeen(MemberEntry member)
    {
        if (member.LastUsedAt is not { } lastUsed)
        {
            ImGui.TextDisabled("No activity");
            return;
        }

        var localized = lastUsed.ToLocalTime();
        ImGui.TextUnformatted(localized.ToString("g", CultureInfo.CurrentCulture));
    }

    private void DrawMemberActions(MemberEntry member, bool disableActions)
    {
        var style = ImGui.GetStyle();
        var buttonWidth = Math.Max(
            120f * ImGuiHelpers.GlobalScale,
            ImGui.CalcTextSize("Remove Access").X + style.FramePadding.X * 2f);

        var disabled = disableActions || member.IsBanned;
        if (disabled)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Remove Access", new Vector2(buttonWidth, 0f)))
        {
            _pendingAction = PendingActionKind.Remove;
            _pendingMember = member;
            ImGui.OpenPopup(ConfirmPopupId);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Disable every DemiCat API key this member has linked so they lose access immediately.");
        }

        if (disabled)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        var banDisabled = disableActions || member.IsBanned;
        if (banDisabled)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Ban User", new Vector2(buttonWidth, 0f)))
        {
            _pendingAction = PendingActionKind.Ban;
            _pendingMember = member;
            ImGui.OpenPopup(ConfirmPopupId);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Revoke access and prevent this Discord user from linking DemiCat again.");
        }

        if (banDisabled)
        {
            ImGui.EndDisabled();
        }
    }

    private void DrawConfirmationPopup()
    {
        if (_pendingAction == PendingActionKind.None || _pendingMember == null)
        {
            return;
        }

        var open = true;
        if (!ImGui.BeginPopupModal(ConfirmPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open)
            {
                ClearPendingAction();
            }
            return;
        }

        var member = _pendingMember;
        var action = _pendingAction;
        var highlight = new Vector4(1f, 0.6f, 0.4f, 1f);

        ImGui.TextWrapped(action == PendingActionKind.Ban
            ? $"Ban {member.DisplayName}? This revokes their access and blocks future links until they are manually unbanned."
            : $"Remove access for {member.DisplayName}? They will need to relink DemiCat to regain access.");

        ImGui.Spacing();

        if (_actionInProgress)
        {
            ImGui.BeginDisabled();
        }

        var confirmLabel = action == PendingActionKind.Ban ? "Ban" : "Remove";
        ImGui.PushStyleColor(ImGuiCol.Button, highlight);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, highlight);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, highlight);
        if (ImGui.Button(confirmLabel))
        {
            ImGui.CloseCurrentPopup();
            ClearPendingAction();
            StartAction(action, member);
        }
        ImGui.PopStyleColor(3);

        if (_actionInProgress)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
            ClearPendingAction();
        }

        ImGui.EndPopup();
    }

    private void ClearPendingAction()
    {
        _pendingAction = PendingActionKind.None;
        _pendingMember = null;
    }

    private void StartRefresh(bool force = false)
    {
        lock (_sync)
        {
            if (_loading)
            {
                return;
            }

            _loading = true;
            if (force)
            {
                _statusMessage = string.Empty;
            }
        }

        _ = Task.Run(async () =>
        {
            var result = await FetchMembersAsync().ConfigureAwait(false);

            lock (_sync)
            {
                _members.Clear();
                _members.AddRange(result.Members);
                _loading = false;
                if (!string.IsNullOrEmpty(result.Message))
                {
                    _statusMessage = result.Message;
                    _statusIsError = result.IsError;
                }
                else if (!_statusIsError)
                {
                    _statusMessage = string.Empty;
                }
            }
        });
    }

    private async Task<FetchResult> FetchMembersAsync()
    {
        if (!EnsureReady(out var error))
        {
            return new FetchResult(new List<MemberEntry>(), error, true);
        }

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/officer/members";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await ReadErrorAsync(response).ConfigureAwait(false);
                var message = $"Failed to load members ({(int)response.StatusCode} {response.StatusCode}{errorText}).";
                return new FetchResult(new List<MemberEntry>(), message, true);
            }

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<List<MemberDto>>(stream, JsonOptions).ConfigureAwait(false)
                ?? new List<MemberDto>();

            var members = dto
                .Select(Convert)
                .Where(m => m != null)
                .Select(m => m!)
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new FetchResult(members, string.Empty, false);
        }
        catch (Exception ex)
        {
            return new FetchResult(new List<MemberEntry>(), $"Failed to load members: {ex.Message}", true);
        }
    }

    private void StartAction(PendingActionKind action, MemberEntry member)
    {
        lock (_sync)
        {
            if (_actionInProgress)
            {
                return;
            }

            _actionInProgress = true;
        }

        _ = Task.Run(async () =>
        {
            var result = await ExecuteActionAsync(action, member).ConfigureAwait(false);

            lock (_sync)
            {
                _actionInProgress = false;
                _statusMessage = result.Message;
                _statusIsError = result.IsError;
                if (result.Success)
                {
                    _members.RemoveAll(m => m.DiscordUserId == member.DiscordUserId);
                }
            }
        });
    }

    private async Task<(bool Success, string Message, bool IsError)> ExecuteActionAsync(PendingActionKind action, MemberEntry member)
    {
        if (!EnsureReady(out var error))
        {
            return (false, error, true);
        }

        var suffix = action == PendingActionKind.Ban ? "ban" : "remove";
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/officer/members/{member.DiscordUserId}/{suffix}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var successMessage = action == PendingActionKind.Ban
                    ? $"{member.DisplayName} has been banned from DemiCat."
                    : $"Removed DemiCat access for {member.DisplayName}.";
                return (true, successMessage, false);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var fallbackMessage = action == PendingActionKind.Ban
                    ? $"{member.DisplayName} is already banned."
                    : $"{member.DisplayName} no longer has DemiCat access.";
                return (true, fallbackMessage, false);
            }

            var detail = await ReadErrorAsync(response).ConfigureAwait(false);
            var failureMessage = action == PendingActionKind.Ban
                ? $"Failed to ban {member.DisplayName} ({(int)response.StatusCode} {response.StatusCode}{detail})."
                : $"Failed to remove access for {member.DisplayName} ({(int)response.StatusCode} {response.StatusCode}{detail}).";
            return (false, failureMessage, true);
        }
        catch (Exception ex)
        {
            var message = action == PendingActionKind.Ban
                ? $"Failed to ban {member.DisplayName}: {ex.Message}"
                : $"Failed to remove access for {member.DisplayName}: {ex.Message}";
            return (false, message, true);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return $": {text.Trim()}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool EnsureReady(out string message)
    {
        if (!OfficerPermissions.HasAccess(_config))
        {
            message = "Officer permissions are required.";
            return false;
        }

        if (!_tokenManager.IsReady())
        {
            message = "Link DemiCat before managing members.";
            return false;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            message = "Set a valid API base URL in settings.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static MemberEntry? Convert(MemberDto dto)
    {
        if (dto.DiscordUserId is not { Length: > 0 } id)
        {
            return null;
        }

        DateTimeOffset? lastUsed = null;
        if (!string.IsNullOrWhiteSpace(dto.LastUsedAt)
            && DateTimeOffset.TryParse(dto.LastUsedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            lastUsed = parsed;
        }

        var displayName = dto.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = dto.Nickname;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = dto.GlobalName;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = dto.CharacterName;
        }
        displayName ??= id;

        return new MemberEntry
        {
            DiscordUserId = id,
            DisplayName = displayName,
            Nickname = dto.Nickname,
            GlobalName = dto.GlobalName,
            CharacterName = dto.CharacterName,
            LastUsedAt = lastUsed,
            IsBanned = dto.IsBanned
        };
    }
}
