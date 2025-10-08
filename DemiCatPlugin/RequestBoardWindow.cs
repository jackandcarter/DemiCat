using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace DemiCatPlugin;

public class RequestBoardWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, bool> _conflicts = new();
    private readonly GameDataCache _gameData;
    private readonly TokenManager _tokenManager;
    private readonly ConcurrentDictionary<string, byte> _itemLoads = new();
    private readonly ConcurrentDictionary<string, byte> _dutyLoads = new();

    private string _newTitle = string.Empty;
    private string _newDescription = string.Empty;
    private RequestType _newType = RequestType.Item;
    private RequestUrgency _newUrgency = RequestUrgency.Low;
    private string _createStatus = string.Empty;

    private readonly Dictionary<string, string> _messageDrafts = new();
    private string? _messagePopupRequestId;

    private enum SortMode
    {
        Type,
        Name,
        MostRecent
    }

    private SortMode _sortMode = SortMode.MostRecent;
    private static readonly string[] SortLabels = { "Type", "Name", "Most Recent" };

    public RequestBoardWindow(Config config, HttpClient httpClient, TokenManager tokenManager)
    {
        _config = config;
        _httpClient = httpClient;
        _gameData = new GameDataCache(httpClient);
        _tokenManager = tokenManager;
    }

    public void Draw()
    {
        if (ImGui.GetCurrentContext() == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!_config.Requests)
            {
                ImGui.TextUnformatted("Feature disabled");
                return;
            }
            if (!_tokenManager.IsReady())
            {
                ImGui.TextUnformatted("Link DemiCat to view requests");
                return;
            }
            if (ImGui.Button("Create Request"))
                ImGui.OpenPopup("createRequest");

            var mode = (int)_sortMode;
            if (ImGui.Combo("Sort By", ref mode, SortLabels, SortLabels.Length))
                _sortMode = (SortMode)mode;

            var requests = RequestStateService.All;
            requests = _sortMode switch
            {
                SortMode.Type => requests.OrderBy(r => r.Type),
                SortMode.Name => requests.OrderBy(r => r.Title),
                _ => requests.OrderByDescending(r => r.CreatedAt)
            };
            var requestList = requests.ToList();

            if (requestList.Count == 0)
            {
                ImGui.TextUnformatted("No requests available.");
            }
            else
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
                ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.35f, 0.45f, 0.55f, 0.5f));
                try
                {
                    foreach (var req in requestList)
                    {
                        ImGui.PushID(req.Id);
                        ImGui.PushStyleColor(ImGuiCol.ChildBg, UrgencyColor(req.Urgency));
                        ImGui.BeginChild("card", new Vector2(-1, 0), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
                        try
                        {
                            ImGui.TextUnformatted(req.Title);
                            ImGui.Spacing();
                            ImGui.TextUnformatted($"Status: {req.Status}   Type: {req.Type}   Urgency: {req.Urgency}");
                            if (!string.IsNullOrWhiteSpace(req.CreatedBy))
                                ImGui.TextUnformatted($"Created by {req.CreatedBy}");
                            if (req.CreatedAt != DateTime.MinValue)
                                ImGui.TextUnformatted($"Created at {req.CreatedAt:u}");

                            ImGui.Separator();
                            var desc = string.IsNullOrWhiteSpace(req.Description) ? "No description provided." : req.Description;
                            ImGui.TextWrapped(desc);

                            if (_conflicts.TryGetValue(req.Id, out var conflicted) && conflicted)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                                try
                                {
                                    ImGui.TextWrapped("Unable to update request because it was modified elsewhere. Refresh and try again.");
                                }
                                finally
                                {
                                    ImGui.PopStyleColor();
                                }
                            }

                            ImGui.Spacing();
                            DrawActionButtons(req);

                            ImGui.Separator();
                            DrawRequirements(req);
                        }
                        finally
                        {
                            ImGui.EndChild();
                        }
                        ImGui.PopStyleColor();
                        ImGui.PopID();
                        ImGui.Spacing();
                    }
                }
                finally
                {
                    ImGui.PopStyleColor();
                    ImGui.PopStyleVar();
                    ImGui.PopStyleVar();
                }
            }

            DrawMessagePopup();

            if (ImGui.BeginPopup("createRequest"))
            {
                try
                {
                    ImGui.InputText("Title", ref _newTitle, 100);
                    ImGui.InputTextMultiline("Description", ref _newDescription, 1000, new Vector2(300, 80));
                    var typeIdx = (int)_newType;
                    var typeLabels = Enum.GetNames<RequestType>();
                    if (ImGui.Combo("Type", ref typeIdx, typeLabels, typeLabels.Length))
                        _newType = (RequestType)typeIdx;
                    var urgIdx = (int)_newUrgency;
                    var urgLabels = Enum.GetNames<RequestUrgency>();
                    if (ImGui.Combo("Urgency", ref urgIdx, urgLabels, urgLabels.Length))
                        _newUrgency = (RequestUrgency)urgIdx;
                    if (!string.IsNullOrEmpty(_createStatus))
                        ImGui.TextUnformatted(_createStatus);
                    if (ImGui.Button("Create"))
                    {
                        if (ValidateNewRequest())
                        {
                            _ = CreateRequest();
                            _createStatus = string.Empty;
                            ImGui.CloseCurrentPopup();
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                    {
                        ImGui.CloseCurrentPopup();
                    }
                }
                finally
                {
                    ImGui.EndPopup();
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                PluginServices.Instance?.Log.Error(ex, "RequestBoardWindow.Draw()");
            }
            catch
            {
                // ignored
            }
        }
    }

    private void DrawActionButtons(RequestState req)
    {
        var disabled = !_tokenManager.IsReady();
        if (disabled)
            ImGui.BeginDisabled();

        var primaryAction = req.Status switch
        {
            RequestStatus.Open => (Label: "Claim", Next: RequestStatus.Claimed),
            RequestStatus.Claimed => (Label: "Start", Next: RequestStatus.InProgress),
            RequestStatus.InProgress => (Label: "Complete", Next: RequestStatus.AwaitingConfirm),
            RequestStatus.AwaitingConfirm => (Label: "Confirm", Next: RequestStatus.Completed),
            _ => ((string Label, RequestStatus Next)?)null
        };

        if (primaryAction.HasValue)
        {
            if (ImGui.Button(primaryAction.Value.Label))
                _ = UpdateAndRefresh(req, primaryAction.Value.Next);
            SameLineIfRoom("Message");
        }

        if (ImGui.Button("Message"))
        {
            _messagePopupRequestId = req.Id;
            if (!_messageDrafts.ContainsKey(req.Id))
                _messageDrafts[req.Id] = string.Empty;
            ImGui.OpenPopup("requestMessage");
        }

        SameLineIfRoom("Requirements");
        if (ImGui.Button("Requirements"))
        {
            if (req.ItemId.HasValue && req.ItemData == null && _itemLoads.TryAdd(req.Id, 0))
                _ = LoadItem(req);

            if (req.DutyId.HasValue && req.DutyData == null && _dutyLoads.TryAdd(req.Id, 0))
                _ = LoadDuty(req);
        }

        if (disabled)
            ImGui.EndDisabled();

        if (req.ItemData == null && !req.ItemId.HasValue && req.DutyData == null && !req.DutyId.HasValue)
        {
            ImGui.TextUnformatted("No additional requirements specified.");
        }
    }

    private static void SameLineIfRoom(string nextLabel)
    {
        var style = ImGui.GetStyle();
        var nextWidth = ImGui.CalcTextSize(nextLabel).X + style.FramePadding.X * 2f;
        if (ImGui.GetContentRegionAvail().X > nextWidth + style.ItemSpacing.X)
            ImGui.SameLine();
    }

    private static Vector4 UrgencyColor(RequestUrgency urgency) => urgency switch
    {
        RequestUrgency.Low => new Vector4(0.13f, 0.16f, 0.21f, 0.35f),
        RequestUrgency.Medium => new Vector4(0.2f, 0.17f, 0.13f, 0.4f),
        RequestUrgency.High => new Vector4(0.26f, 0.12f, 0.12f, 0.45f),
        _ => new Vector4(0.16f, 0.16f, 0.16f, 0.35f)
    };

    private void DrawRequirements(RequestState req)
    {
        if (req.ItemData != null)
        {
            ImGui.TextUnformatted($"Item: {req.ItemData.Name}");
            if (req.Quantity > 0)
                ImGui.TextUnformatted($"Quantity: {req.Quantity}{(req.Hq ? " (HQ)" : string.Empty)}");
        }
        else if (req.ItemId.HasValue)
        {
            ImGui.TextUnformatted("Item data loading...");
        }

        if (req.DutyData != null)
        {
            ImGui.TextUnformatted($"Duty: {req.DutyData.Name}");
        }
        else if (req.DutyId.HasValue)
        {
            ImGui.TextUnformatted("Duty data loading...");
        }
    }

    private void DrawMessagePopup()
    {
        if (!ImGui.BeginPopup("requestMessage"))
            return;

        try
        {
            if (_messagePopupRequestId == null || !RequestStateService.TryGet(_messagePopupRequestId, out var req))
            {
                ImGui.TextUnformatted("No request selected.");
                if (ImGui.Button("Close"))
                {
                    ImGui.CloseCurrentPopup();
                    _messagePopupRequestId = null;
                }
                return;
            }

            var draft = _messageDrafts.TryGetValue(req.Id, out var existing) ? existing : string.Empty;
            ImGui.InputTextMultiline("##message", ref draft, 1000, new Vector2(320, 120));
            _messageDrafts[req.Id] = draft;

            if (ImGui.Button("Send") && !string.IsNullOrWhiteSpace(draft))
            {
                _ = CommentAndRefresh(req, draft);
                _messageDrafts[req.Id] = string.Empty;
                ImGui.CloseCurrentPopup();
                _messagePopupRequestId = null;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
                _messagePopupRequestId = null;
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private async Task UpdateAndRefresh(RequestState req, RequestStatus newStatus)
    {
        _conflicts.Remove(req.Id);
        await Update(req, newStatus);
        await Refresh(req.Id);
    }

    private async Task CommentAndRefresh(RequestState req, string message)
    {
        await Comment(req, message);
        await Refresh(req.Id);
    }

    private async Task Comment(RequestState req, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !ApiHelpers.ValidateApiBaseUrl(_config))
            return;

        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{req.Id}/comment";
            var body = new { message };
            var json = JsonSerializer.Serialize(body);
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(msg, _tokenManager);
            msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(msg);
            if (!resp.IsSuccessStatusCode)
                return;
        }
        catch
        {
            // ignore
        }
    }

    private async Task LoadItem(RequestState req)
    {
        try
        {
            var data = await _gameData.GetItem(req.ItemId!.Value);
            if (data != null)
                req.ItemData = data;
        }
        finally
        {
            _itemLoads.TryRemove(req.Id, out _);
        }
    }

    private async Task LoadDuty(RequestState req)
    {
        try
        {
            var data = await _gameData.GetDuty(req.DutyId!.Value);
            if (data != null)
                req.DutyData = data;
        }
        finally
        {
            _dutyLoads.TryRemove(req.Id, out _);
        }
    }

    private async Task Update(RequestState req, RequestStatus newStatus)
    {
        var action = newStatus switch
        {
            RequestStatus.Claimed => "accept",
            RequestStatus.InProgress => "start",
            RequestStatus.AwaitingConfirm => "complete",
            RequestStatus.Completed => "confirm",
            _ => null
        };
        if (action == null) return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{req.Id}/{action}";
                var json = JsonSerializer.Serialize(new { version = req.Version });
                var msg = new HttpRequestMessage(HttpMethod.Post, url);
                ApiHelpers.AddAuthHeader(msg, _tokenManager);
                msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(msg);
                if (resp.IsSuccessStatusCode)
                {
                    var respJson = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respJson);
                    var payload = doc.RootElement;
                    if (!payload.TryGetProperty("version", out var vEl))
                    {
                        _conflicts[req.Id] = true;
                        return;
                    }
                    var version = vEl.GetInt32();
                    if (version != req.Version + 1)
                    {
                        _conflicts[req.Id] = true;
                        return;
                    }
                    var id = payload.GetProperty("id").GetString() ?? req.Id;
                    var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? req.Title : req.Title;
                    var statusStr = payload.GetProperty("status").GetString() ?? StatusToString(newStatus);
                    var typeStr = payload.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? TypeToString(req.Type) : TypeToString(req.Type);
                    var urgencyStr = payload.TryGetProperty("urgency", out var uEl) ? uEl.GetString() ?? UrgencyToString(req.Urgency) : UrgencyToString(req.Urgency);
                    var itemId = payload.TryGetProperty("item_id", out var iEl) ? iEl.GetUInt32() : (uint?)req.ItemId;
                    var dutyId = payload.TryGetProperty("duty_id", out var dEl) ? dEl.GetUInt32() : (uint?)req.DutyId;
                    var hq = payload.TryGetProperty("hq", out var hEl) ? hEl.GetBoolean() : req.Hq;
                    var quantity = payload.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : req.Quantity;
                    var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)req.AssigneeId;
                    var description = payload.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? req.Description : req.Description;
                    var createdBy = payload.TryGetProperty("created_by", out var cbEl) ? cbEl.GetString() ?? req.CreatedBy : req.CreatedBy;
                    DateTime createdAt = req.CreatedAt;
                    if (payload.TryGetProperty("created", out var cEl))
                        cEl.TryGetDateTime(out createdAt);
                    RequestStateService.Upsert(new RequestState
                    {
                        Id = id,
                        Title = title,
                        Status = ParseStatus(statusStr),
                        Type = ParseType(typeStr),
                        Urgency = ParseUrgency(urgencyStr),
                        Version = version,
                        ItemId = itemId,
                        DutyId = dutyId,
                        Hq = hq,
                        Quantity = quantity,
                        AssigneeId = assigneeId,
                        Description = description,
                        CreatedBy = createdBy,
                        CreatedAt = createdAt
                    });
                    return;
                }
                if ((int)resp.StatusCode == 409)
                {
                    await Refresh(req.Id);
                    if (RequestStateService.TryGet(req.Id, out var refreshed))
                    {
                        req = refreshed;
                        continue;
                    }
                }
            }
            catch
            {
                // ignore and mark conflict below
            }
            break;
        }
        _conflicts[req.Id] = true;
    }

    private async Task Refresh(string id)
    {
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests/{id}";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(msg, _tokenManager);
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var payload = doc.RootElement;
                var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Request" : "Request";
                var statusStr = payload.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "open" : "open";
                var typeStr = payload.TryGetProperty("type", out var tType) ? tType.GetString() ?? "item" : "item";
                var urgencyStr = payload.TryGetProperty("urgency", out var uEl) ? uEl.GetString() ?? "low" : "low";
                var version = payload.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 0;
                var itemId = payload.TryGetProperty("item_id", out var iEl) ? iEl.GetUInt32() : (uint?)null;
                var dutyId = payload.TryGetProperty("duty_id", out var dEl) ? dEl.GetUInt32() : (uint?)null;
                var hq = payload.TryGetProperty("hq", out var hEl) && hEl.GetBoolean();
                var quantity = payload.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : 0;
                var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)null;
                var description = payload.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? string.Empty : string.Empty;
                var createdBy = payload.TryGetProperty("created_by", out var cbEl) ? cbEl.GetString() ?? string.Empty : string.Empty;
                DateTime createdAt = DateTime.MinValue;
                if (payload.TryGetProperty("created", out var cEl))
                    cEl.TryGetDateTime(out createdAt);
                RequestStateService.Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusStr),
                    Type = ParseType(typeStr),
                    Urgency = ParseUrgency(urgencyStr),
                    Version = version,
                    ItemId = itemId,
                    DutyId = dutyId,
                    Hq = hq,
                    Quantity = quantity,
                    AssigneeId = assigneeId,
                    Description = description,
                    CreatedBy = createdBy,
                    CreatedAt = createdAt
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string StatusToString(RequestStatus status) => status switch
    {
        RequestStatus.Open => "open",
        RequestStatus.Claimed => "claimed",
        RequestStatus.InProgress => "in_progress",
        RequestStatus.AwaitingConfirm => "awaiting_confirm",
        RequestStatus.Completed => "completed",
        RequestStatus.Cancelled => "cancelled",
        RequestStatus.Approved => "approved",
        RequestStatus.Denied => "denied",
        _ => "open"
    };

    private static RequestStatus ParseStatus(string status) => status switch
    {
        "open" => RequestStatus.Open,
        "claimed" => RequestStatus.Claimed,
        "in_progress" => RequestStatus.InProgress,
        "awaiting_confirm" => RequestStatus.AwaitingConfirm,
        "completed" => RequestStatus.Completed,
        "cancelled" => RequestStatus.Cancelled,
        "approved" => RequestStatus.Approved,
        "denied" => RequestStatus.Denied,
        _ => RequestStatus.Open
    };

    private static string TypeToString(RequestType type) => type switch
    {
        RequestType.Item => "item",
        RequestType.Run => "run",
        RequestType.Event => "event",
        _ => "item"
    };

    private static RequestType ParseType(string type) => type switch
    {
        "item" => RequestType.Item,
        "run" => RequestType.Run,
        "event" => RequestType.Event,
        _ => RequestType.Item
    };

    private static string UrgencyToString(RequestUrgency urgency) => urgency switch
    {
        RequestUrgency.Low => "low",
        RequestUrgency.Medium => "medium",
        RequestUrgency.High => "high",
        _ => "low"
    };

    private static RequestUrgency ParseUrgency(string urgency) => urgency switch
    {
        "low" => RequestUrgency.Low,
        "medium" => RequestUrgency.Medium,
        "high" => RequestUrgency.High,
        _ => RequestUrgency.Low
    };

    private bool ValidateNewRequest()
    {
        if (_newTitle.Length > 2000 || _newDescription.Length > 2000)
        {
            _createStatus = "Title or description exceeds 2000 characters";
            return false;
        }
        _createStatus = string.Empty;
        return true;
    }

    private async Task CreateRequest()
    {
        if (!ApiHelpers.ValidateApiBaseUrl(_config))
            return;
        try
        {
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/requests";
            var body = new
            {
                title = _newTitle,
                description = string.IsNullOrWhiteSpace(_newDescription) ? null : _newDescription,
                type = TypeToString(_newType),
                urgency = UrgencyToString(_newUrgency)
            };
            var json = JsonSerializer.Serialize(body);
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            ApiHelpers.AddAuthHeader(msg, _tokenManager);
            msg.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var id = doc.RootElement.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                    await Refresh(id!);
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _newTitle = string.Empty;
            _newDescription = string.Empty;
            _newType = RequestType.Item;
            _newUrgency = RequestUrgency.Low;
        }
    }
}
