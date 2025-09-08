using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemiCatPlugin;

internal static class RequestStateService
{
    private static readonly Dictionary<string, RequestState> RequestsMap = new();
    private static readonly object LockObj = new();
    private static Config? _config;

    public static IEnumerable<RequestState> All
    {
        get
        {
            lock (LockObj)
                return RequestsMap.Values.ToList();
        }
    }

    public static void Load(Config config)
    {
        _config = config;
        lock (LockObj)
        {
            RequestsMap.Clear();
            foreach (var s in config.RequestStates)
            {
                RequestsMap[s.Id] = s;
            }
        }
    }

    private static void Save()
    {
        if (_config == null) return;
        lock (LockObj)
        {
            _config.RequestStates = RequestsMap.Values.ToList();
        }
        PluginServices.Instance?.PluginInterface.SavePluginConfig(_config);
    }

    public static void Prune()
    {
        lock (LockObj)
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            var remove = RequestsMap
                .Where(kvp =>
                    (kvp.Value.Status == RequestStatus.Completed || kvp.Value.Status == RequestStatus.Cancelled) &&
                    kvp.Value.CreatedAt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var id in remove)
                RequestsMap.Remove(id);
        }
        Save();
    }

    public static void Upsert(RequestState state)
    {
        lock (LockObj)
        {
            if (RequestsMap.TryGetValue(state.Id, out var existing))
            {
                if (state.Version > existing.Version)
                {
                    state.ItemData ??= existing.ItemData;
                    state.DutyData ??= existing.DutyData;
                    RequestsMap[state.Id] = state;
                }
            }
            else
            {
                RequestsMap[state.Id] = state;
            }
        }
        Save();
    }

    public static void Remove(string id)
    {
        lock (LockObj)
        {
            RequestsMap.Remove(id);
        }
        Save();
    }

    public static bool TryGet(string id, out RequestState state)
    {
        lock (LockObj)
        {
            return RequestsMap.TryGetValue(id, out state!);
        }
    }

    public static async Task RefreshAll(HttpClient httpClient, Config config)
    {
        if (!ApiHelpers.ValidateApiBaseUrl(config)) return;
        _config = config;
        try
        {
            string? newToken = null;
            try
            {
                var tokenMsg = new HttpRequestMessage(HttpMethod.Get, $"{config.ApiBaseUrl.TrimEnd('/')}/api/delta-token");
                ApiHelpers.AddAuthHeader(tokenMsg, TokenManager.Instance!);
                var tokenResp = await httpClient.SendAsync(tokenMsg);
                if (tokenResp.IsSuccessStatusCode)
                {
                    var tokenStream = await tokenResp.Content.ReadAsStreamAsync();
                    using var tokenDoc = await JsonDocument.ParseAsync(tokenStream);
                    newToken = tokenDoc.RootElement.GetProperty("since").GetString();
                }
                else
                {
                    PluginServices.Instance!.Log.Warning($"Failed to retrieve delta token. URL: {tokenMsg.RequestUri}, Status: {tokenResp.StatusCode}");
                }
            }
            catch
            {
                // ignore token failure
            }

            var baseUrl = config.ApiBaseUrl.TrimEnd('/');
            var url = string.IsNullOrEmpty(config.RequestsDeltaToken)
                ? $"{baseUrl}/api/requests"
                : $"{baseUrl}/api/requests/delta?since={Uri.EscapeDataString(config.RequestsDeltaToken)}";
            var msg = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(msg, TokenManager.Instance!);
            var resp = await httpClient.SendAsync(msg);
            if (!resp.IsSuccessStatusCode)
            {
                PluginServices.Instance!.Log.Warning($"Failed to refresh request states. URL: {url}, Status: {resp.StatusCode}");
                return;
            }
            var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            IEnumerable<JsonElement> list;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                list = doc.RootElement.EnumerateArray();
            else if (doc.RootElement.TryGetProperty("requests", out var arr) && arr.ValueKind == JsonValueKind.Array)
                list = arr.EnumerateArray();
            else
                list = Array.Empty<JsonElement>();
            foreach (var payload in list)
            {
                var id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var deleted = payload.TryGetProperty("deleted", out var delEl) && delEl.GetBoolean();
                if (deleted)
                {
                    if (id != null) Remove(id);
                    continue;
                }
                var title = payload.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "Request" : "Request";
                var statusString = payload.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                var typeString = payload.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                var urgencyString = payload.TryGetProperty("urgency", out var urgEl) ? urgEl.GetString() : null;
                var version = payload.TryGetProperty("version", out var verEl) ? verEl.GetInt32() : 0;
                var itemId = payload.TryGetProperty("itemId", out var itemEl) ? itemEl.GetUInt32() : (uint?)null;
                var dutyId = payload.TryGetProperty("dutyId", out var dutyEl) ? dutyEl.GetUInt32() : (uint?)null;
                var hq = payload.TryGetProperty("hq", out var hqEl) && hqEl.GetBoolean();
                var quantity = payload.TryGetProperty("quantity", out var qtyEl) ? qtyEl.GetInt32() : 0;
                var assigneeId = payload.TryGetProperty("assigneeId", out var aEl) ? aEl.GetUInt32() : (uint?)null;
                var description = payload.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? string.Empty : string.Empty;
                var createdBy = payload.TryGetProperty("createdBy", out var cbEl) ? cbEl.GetString() ?? string.Empty : string.Empty;
                DateTime createdAt = DateTime.MinValue;
                if (payload.TryGetProperty("created", out var cEl))
                    cEl.TryGetDateTime(out createdAt);
                if (id == null || statusString == null) continue;
                Upsert(new RequestState
                {
                    Id = id,
                    Title = title,
                    Status = ParseStatus(statusString),
                    Type = typeString != null ? ParseType(typeString) : RequestType.Item,
                    Urgency = urgencyString != null ? ParseUrgency(urgencyString) : RequestUrgency.Low,
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
            if (newToken != null)
            {
                config.RequestsDeltaToken = newToken;
            }
            Prune();
        }
        catch
        {
            // ignored
        }
    }

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

    private static RequestType ParseType(string type) => type switch
    {
        "item" => RequestType.Item,
        "run" => RequestType.Run,
        "event" => RequestType.Event,
        _ => RequestType.Item
    };

    private static RequestUrgency ParseUrgency(string urgency) => urgency switch
    {
        "low" => RequestUrgency.Low,
        "medium" => RequestUrgency.Medium,
        "high" => RequestUrgency.High,
        _ => RequestUrgency.Low
    };
}
