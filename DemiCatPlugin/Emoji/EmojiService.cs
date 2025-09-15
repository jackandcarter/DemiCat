using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DemiCatPlugin.Emoji
{
    public sealed class EmojiService
    {
        private readonly HttpClient _http;
        private readonly TokenManager _tokens;
        private readonly Config _config;

        public List<CustomEmoji> Custom { get; private set; } = new();

        public EmojiService(HttpClient http, TokenManager tokens, Config config)
        { _http = http; _tokens = tokens; _config = config; }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            if (!_tokens.IsReady() || string.IsNullOrEmpty(_tokens.Token))
            {
                PluginServices.Instance?.ToastGui.ShowError("Emoji auth failed");
                Custom = new();
                return;
            }

            Custom = new();
            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/discord/emojis";
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                HttpResponseMessage? res = null;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    ApiHelpers.AddAuthHeader(req, _tokens);

                    res = await _http.SendAsync(req, ct);
                    if (res.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        PluginServices.Instance?.ToastGui.ShowError("Emoji auth failed");
                        _tokens.Clear("Authentication failed");
                        return;
                    }

                    res.EnsureSuccessStatusCode();
                    using var stream = await res.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    var list = new List<CustomEmoji>();
                    if (doc.RootElement.TryGetProperty("emojis", out var arr))
                    {
                        foreach (var e in arr.EnumerateArray())
                        {
                            var id = e.GetProperty("id").GetString()!;
                            var name = e.GetProperty("name").GetString()!;
                            var animated = e.GetProperty("animated").GetBoolean();
                            var available = e.TryGetProperty("available", out var a) ? a.GetBoolean() : true;
                            if (available)
                                list.Add(new CustomEmoji(id, name, animated));
                        }
                    }
                    Custom = list;
                    return;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < maxAttempts)
                {
                    PluginServices.Instance?.Log.Warning(ex, $"Emoji refresh failed with 503, attempt {attempt}/{maxAttempts}");
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, ct);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    PluginServices.Instance?.Log.Warning(ex, $"Emoji refresh failed, attempt {attempt}/{maxAttempts}");
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, ct);
                }
                catch (HttpRequestException ex)
                {
                    PluginServices.Instance?.Log.Error(ex, "Emoji refresh failed");
                    return;
                }
                finally
                {
                    res?.Dispose();
                }
            }

            PluginServices.Instance?.Log.Error("Emoji refresh failed after maximum retries");
        }
    }
}
