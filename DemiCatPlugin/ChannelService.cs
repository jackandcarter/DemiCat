using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public class ChannelService
{
    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token
                );
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
                var channels = await JsonSerializer.DeserializeAsync<List<ChannelDto>>(stream, JsonOpts, linkedCts.Token) ?? new List<ChannelDto>();
                foreach (var channel in channels)
                {
                    channel.EnsureKind(kind);
                    channel.Name = DecodeJsonUnicodeEscapes(channel.Name);
                }
                return ChannelDtoExtensions.SortForDisplay(channels);
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

    public async Task<ChannelValidationResponse?> ValidateAsync(
        string kind,
        string channelId,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        if (!ApiHelpers.ValidateApiBaseUrl(_config))
        {
            return null;
        }

        var normalizedKind = ChannelKeyHelper.NormalizeKind(kind);
        var url =
            $"{_config.ApiBaseUrl.TrimEnd('/')}/api/channels/{channelId}/validate?kind={normalizedKind}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token
            );
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
            var result = await JsonSerializer.DeserializeAsync<ChannelValidationResponse>(
                stream,
                JsonOpts,
                linkedCts.Token
            );

            if (result != null && !string.IsNullOrEmpty(result.Kind))
            {
                result.Kind = ChannelKeyHelper.NormalizeKind(result.Kind);
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            PluginServices.Instance?.Log.Warning(
                ex,
                "Channel validation request failed for {ChannelId} ({Kind})",
                channelId,
                kind
            );
        }
        catch (TaskCanceledException ex)
        {
            PluginServices.Instance?.Log.Warning(
                ex,
                "Channel validation timed out for {ChannelId} ({Kind})",
                channelId,
                kind
            );
        }
        catch (JsonException ex)
        {
            PluginServices.Instance?.Log.Warning(
                ex,
                "Channel validation parse failed for {ChannelId} ({Kind})",
                channelId,
                kind
            );
        }

        return null;
    }

    private static string DecodeJsonUnicodeEscapes(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == 'u' && i + 5 < value.Length)
            {
                var hexSpan = value.AsSpan(i + 2, 4);
                if (ushort.TryParse(hexSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit))
                {
                    i += 6;

                    if (char.IsHighSurrogate((char)codeUnit)
                        && i + 5 < value.Length
                        && value[i] == '\\'
                        && value[i + 1] == 'u'
                        && ushort.TryParse(value.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lowCodeUnit)
                        && char.IsLowSurrogate((char)lowCodeUnit))
                    {
                        var rune = char.ConvertToUtf32((char)codeUnit, (char)lowCodeUnit);
                        sb.Append(char.ConvertFromUtf32(rune));
                        i += 6;
                        continue;
                    }

                    sb.Append((char)codeUnit);
                    continue;
                }
            }

            sb.Append(value[i]);
            i++;
        }

        return sb.ToString();
    }
}

