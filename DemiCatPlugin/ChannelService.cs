using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class ChannelService
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;

    public ChannelService(Config config, HttpClient httpClient, TokenManager tokenManager)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
    }

    public async Task<IReadOnlyList<ChannelDto>> FetchAsync(string kind, CancellationToken ct)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels?kind={kind}";
        var delay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiHelpers.AddAuthHeader(request, _tokenManager);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var response = await _httpClient.SendAsync(request, linkedCts.Token);
                response.EnsureSuccessStatusCode();
                var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
                var channels = await JsonSerializer.DeserializeAsync<List<ChannelDto>>(stream, cancellationToken: linkedCts.Token) ?? new List<ChannelDto>();
                return channels;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
            catch (TaskCanceledException) when (attempt < 2)
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
        }
    }
}

