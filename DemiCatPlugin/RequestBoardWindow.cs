using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DemiCatPlugin;

public class RequestBoardWindow
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, bool> _conflicts = new();
    private readonly GameDataCache _gameData;

    public RequestBoardWindow(Config config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _gameData = new GameDataCache(httpClient);
    }

    public void Draw()
    {
        foreach (var req in RequestStateService.All)
        {
            ImGui.PushID(req.Id);
            if (req.ItemId.HasValue)
            {
                var item = _gameData.GetItem(req.ItemId.Value).GetAwaiter().GetResult();
                if (item != null)
                {
                    var tex = PluginServices.Instance!.TextureProvider.GetFromFile(item.IconPath);
                    if (tex != null)
                    {
                        ImGui.Image(tex.ImGuiHandle, new Vector2(32));
                        ImGui.SameLine();
                    }
                    var text = item.Name;
                    if (req.Hq) text += " (HQ)";
                    if (req.Quantity > 1) text += $" x{req.Quantity}";
                    ImGui.TextUnformatted($"{text} [{req.Status}]");
                }
                else
                {
                    ImGui.TextUnformatted($"Item {req.ItemId} [{req.Status}]");
                }
            }
            else if (req.DutyId.HasValue)
            {
                var duty = _gameData.GetDuty(req.DutyId.Value).GetAwaiter().GetResult();
                if (duty != null)
                {
                    var tex = PluginServices.Instance!.TextureProvider.GetFromFile(duty.IconPath);
                    if (tex != null)
                    {
                        ImGui.Image(tex.ImGuiHandle, new Vector2(32));
                        ImGui.SameLine();
                    }
                    ImGui.TextUnformatted($"{duty.Name} [{req.Status}]");
                }
                else
                {
                    ImGui.TextUnformatted($"Duty {req.DutyId} [{req.Status}]");
                }
            }
            else
            {
                ImGui.TextUnformatted($"{req.Title} [{req.Status}]");
            }
            ImGui.SameLine();
            if (_conflicts.ContainsKey(req.Id))
            {
                if (ImGui.Button("Retry"))
                {
                    _ = Refresh(req.Id);
                    _conflicts.Remove(req.Id);
                }
            }
            else
            {
                switch (req.Status)
                {
                    case RequestStatus.Open:
                        if (ImGui.Button("Accept"))
                            _ = Update(req, RequestStatus.Claimed);
                        break;
                    case RequestStatus.Claimed:
                        if (ImGui.Button("In-Progress"))
                            _ = Update(req, RequestStatus.InProgress);
                        break;
                    case RequestStatus.InProgress:
                        if (ImGui.Button("Deliver"))
                            _ = Update(req, RequestStatus.AwaitingConfirm);
                        break;
                    case RequestStatus.AwaitingConfirm:
                        if (ImGui.Button("Confirm"))
                            _ = Update(req, RequestStatus.Completed);
                        break;
                }
            }
            ImGui.PopID();
            ImGui.Separator();
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
                msg.Headers.Add("X-Api-Key", _config.AuthToken);
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
                    var itemId = payload.TryGetProperty("item_id", out var iEl) ? iEl.GetUInt32() : (uint?)req.ItemId;
                    var dutyId = payload.TryGetProperty("duty_id", out var dEl) ? dEl.GetUInt32() : (uint?)req.DutyId;
                    var hq = payload.TryGetProperty("hq", out var hEl) ? hEl.GetBoolean() : req.Hq;
                    var quantity = payload.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : req.Quantity;
                    var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)req.AssigneeId;
                    RequestStateService.Upsert(new RequestState
                    {
                        Id = id,
                        Title = title,
                        Status = ParseStatus(statusStr),
                        Version = version,
                        ItemId = itemId,
                        DutyId = dutyId,
                        Hq = hq,
                        Quantity = quantity,
                        AssigneeId = assigneeId
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
            msg.Headers.Add("X-Api-Key", _config.AuthToken);
            var resp = await _httpClient.SendAsync(msg);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var payload = doc.RootElement;
                var title = payload.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Request" : "Request";
                var statusStr = payload.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "open" : "open";
                var version = payload.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 0;
                var itemId = payload.TryGetProperty("item_id", out var iEl) ? iEl.GetUInt32() : (uint?)null;
                var dutyId = payload.TryGetProperty("duty_id", out var dEl) ? dEl.GetUInt32() : (uint?)null;
                var hq = payload.TryGetProperty("hq", out var hEl) && hEl.GetBoolean();
                var quantity = payload.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : 0;
                var assigneeId = payload.TryGetProperty("assignee_id", out var aEl) ? aEl.GetUInt32() : (uint?)null;
                RequestStateService.Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusStr),
                    Version = version,
                    ItemId = itemId,
                    DutyId = dutyId,
                    Hq = hq,
                    Quantity = quantity,
                    AssigneeId = assigneeId
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
        _ => RequestStatus.Open
    };
}
