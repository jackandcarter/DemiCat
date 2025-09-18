using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin.Emoji;

public sealed class EmojiManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokens;
    private readonly Config _config;
    private readonly object _loadLock = new();
    private readonly object _statusLock = new();

    private CancellationTokenSource _disposeCts = new();
    private Task? _unicodeTask;
    private Task? _customTask;

    private UnicodeEmoji[] _unicode = Array.Empty<UnicodeEmoji>();
    private CustomEmoji[] _custom = Array.Empty<CustomEmoji>();
    private Dictionary<string, CustomEmoji> _customLookup = new(StringComparer.Ordinal);

    private LoadState _unicodeState;
    private LoadState _customState;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public EmojiManager(HttpClient httpClient, TokenManager tokens, Config config)
    {
        _httpClient = httpClient;
        _tokens = tokens;
        _config = config;
    }

    public event Action? Changed;

    public IReadOnlyList<UnicodeEmoji> Unicode => Volatile.Read(ref _unicode);
    public IReadOnlyList<CustomEmoji> Custom => Volatile.Read(ref _custom);

    public bool HasGuildConfiguration => !string.IsNullOrWhiteSpace(_config.GuildId);
    public bool CanLoadStandard => ApiHelpers.ValidateApiBaseUrl(_config) && _tokens.IsReady();
    public bool CanLoadCustom => HasGuildConfiguration && CanLoadStandard;

    public EmojiLoadStatus UnicodeStatus
    {
        get
        {
            lock (_statusLock)
            {
                return new EmojiLoadStatus(_unicodeState.Loading, _unicodeState.Loaded, _unicodeState.Error);
            }
        }
    }

    public EmojiLoadStatus CustomStatus
    {
        get
        {
            lock (_statusLock)
            {
                return new EmojiLoadStatus(_customState.Loading, _customState.Loaded, _customState.Error);
            }
        }
    }

    public Task EnsureUnicodeAsync(CancellationToken ct = default) => StartUnicodeLoad(ct, false);
    public Task RefreshUnicodeAsync(CancellationToken ct = default) => StartUnicodeLoad(ct, true);
    public Task EnsureCustomAsync(CancellationToken ct = default) => StartCustomLoad(ct, false);
    public Task RefreshCustomAsync(CancellationToken ct = default) => StartCustomLoad(ct, true);

    public bool TryGetCustomEmoji(string id, out CustomEmoji? emoji)
    {
        var lookup = Volatile.Read(ref _customLookup);
        if (lookup != null && lookup.TryGetValue(id, out var found))
        {
            emoji = found;
            return true;
        }

        emoji = null;
        return false;
    }

    private Task StartUnicodeLoad(CancellationToken ct, bool force)
    {
        if (!CanLoadStandard)
        {
            return Task.CompletedTask;
        }

        lock (_loadLock)
        {
            if (!force && (_unicodeState.Loading || _unicodeState.Loaded))
            {
                return _unicodeTask ?? Task.CompletedTask;
            }

            UpdateUnicodeState(new LoadState(true, false, null));
            PublishChanged();

            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var task = LoadUnicodeAsync(linked.Token);
            _unicodeTask = task;
            return task;
        }
    }

    private Task StartCustomLoad(CancellationToken ct, bool force)
    {
        if (!CanLoadCustom)
        {
            return Task.CompletedTask;
        }

        lock (_loadLock)
        {
            if (!force && (_customState.Loading || _customState.Loaded))
            {
                return _customTask ?? Task.CompletedTask;
            }

            UpdateCustomState(new LoadState(true, false, null));
            PublishChanged();

            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var task = LoadCustomAsync(linked.Token);
            _customTask = task;
            return task;
        }
    }

    private async Task LoadUnicodeAsync(CancellationToken ct)
    {
        try
        {
            var result = await FetchUnicodeAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _unicode, result);
            UpdateUnicodeState(new LoadState(false, true, null));
        }
        catch (OperationCanceledException)
        {
            UpdateUnicodeState(new LoadState(false, false, null));
            throw;
        }
        catch (Exception ex)
        {
            UpdateUnicodeState(new LoadState(false, false, ex.Message));
        }
        finally
        {
            lock (_loadLock)
            {
                _unicodeTask = null;
            }
            PublishChanged();
        }
    }

    private async Task LoadCustomAsync(CancellationToken ct)
    {
        try
        {
            var result = await FetchCustomAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _custom, result.Items);
            Volatile.Write(ref _customLookup, result.Lookup);
            UpdateCustomState(new LoadState(false, true, null));
        }
        catch (OperationCanceledException)
        {
            UpdateCustomState(new LoadState(false, false, null));
            throw;
        }
        catch (Exception ex)
        {
            UpdateCustomState(new LoadState(false, false, ex.Message));
        }
        finally
        {
            lock (_loadLock)
            {
                _customTask = null;
            }
            PublishChanged();
        }
    }

    private async Task<UnicodeEmoji[]> FetchUnicodeAsync(CancellationToken ct)
    {
        if (!CanLoadStandard)
            return Array.Empty<UnicodeEmoji>();

        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis/unicode";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApiHelpers.AddAuthHeader(req, _tokens);

        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            _tokens.Clear("Invalid API key");
            throw new HttpRequestException("Unauthorized", null, res.StatusCode);
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Unicode emoji request failed with {(int)res.StatusCode}", null, res.StatusCode);
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<List<UnicodeEmoji>>(stream, JsonOptions, ct).ConfigureAwait(false)
                   ?? new List<UnicodeEmoji>();
        return list.FindAll(e => !string.IsNullOrWhiteSpace(e.Emoji)).ToArray();
    }

    private async Task<(CustomEmoji[] Items, Dictionary<string, CustomEmoji> Lookup)> FetchCustomAsync(CancellationToken ct)
    {
        if (!CanLoadCustom)
        {
            return (Array.Empty<CustomEmoji>(), new Dictionary<string, CustomEmoji>(StringComparer.Ordinal));
        }

        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/emojis/guilds/{_config.GuildId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApiHelpers.AddAuthHeader(req, _tokens);

        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            _tokens.Clear("Invalid API key");
            throw new HttpRequestException("Unauthorized", null, res.StatusCode);
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Custom emoji request failed with {(int)res.StatusCode}", null, res.StatusCode);
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<List<GuildEmojiDto>>(stream, JsonOptions, ct).ConfigureAwait(false)
                   ?? new List<GuildEmojiDto>();

        var items = new List<CustomEmoji>(list.Count);
        foreach (var entry in list)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var imageUrl = string.IsNullOrWhiteSpace(entry.ImageUrl)
                ? BuildCdnUrl(entry.Id!, entry.IsAnimated)
                : entry.ImageUrl!;
            items.Add(new CustomEmoji(entry.Id!, entry.Name!, entry.IsAnimated, imageUrl));
        }

        var lookup = new Dictionary<string, CustomEmoji>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            lookup[item.Id] = item;
        }

        return (items.ToArray(), lookup);
    }

    private static string BuildCdnUrl(string id, bool animated)
        => $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}";

    private void UpdateUnicodeState(LoadState state)
    {
        lock (_statusLock)
        {
            _unicodeState = state;
        }
    }

    private void UpdateCustomState(LoadState state)
    {
        lock (_statusLock)
        {
            _customState = state;
        }
    }

    private void PublishChanged()
    {
        var handlers = Changed;
        if (handlers == null)
        {
            return;
        }

        var framework = PluginServices.Instance?.Framework;
        if (framework != null)
        {
            _ = framework.RunOnTick(() =>
            {
                try
                {
                    handlers.Invoke();
                }
                catch
                {
                    // ignored
                }
            });
        }
        else
        {
            try
            {
                handlers.Invoke();
            }
            catch
            {
                // ignored
            }
        }
    }

    public void Dispose()
    {
        lock (_loadLock)
        {
            if (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            _disposeCts.Cancel();
            _disposeCts.Dispose();
        }
    }

    private struct LoadState
    {
        public bool Loading { get; }
        public bool Loaded { get; }
        public string? Error { get; }

        public LoadState(bool loading, bool loaded, string? error)
        {
            Loading = loading;
            Loaded = loaded;
            Error = error;
        }
    }

    private sealed class GuildEmojiDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool IsAnimated { get; set; }
        public string? ImageUrl { get; set; }
    }
}
