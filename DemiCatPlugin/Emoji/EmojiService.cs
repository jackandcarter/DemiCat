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
            if (!_tokens.IsReady())
            {
                Custom = new();
                return;
            }

            var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/discord/emojis";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(req, _tokens);

            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                _tokens.Clear("Authentication failed");
                Custom = new();
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
        }
    }
}
