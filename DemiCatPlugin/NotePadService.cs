using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DemiCatPlugin;

public sealed class NotePadService : IDisposable
{
    internal const string BuiltInAboutSectionId = "builtin-about";
    private const string BuiltInAboutPageId = "builtin-about-page";

    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly TokenManager _tokenManager;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly List<NotePadSection> _sections = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _retryAttempt;
    private DateTime _lastToast;
    private string? _lastToastSignature;

    private static readonly TimeSpan ToastThrottle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutosyncDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BuiltInTimestampEpsilon = TimeSpan.FromMinutes(1);

    public event Action? Changed;

    public NotePadService(Config config, HttpClient httpClient, TokenManager tokenManager)
    {
        _config = config;
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _serializerOptions.Converters.Add(new NotePadColorJsonConverter());
    }

    public IReadOnlyList<NotePadSection> Sections
    {
        get
        {
            lock (_sections)
            {
                var result = _sections.Select(CloneSection).ToList();
                InsertBuiltInSection(result);
                return result;
            }
        }
    }

    public bool IsRunning => _cts != null;

    public void Start()
    {
        Stop().GetAwaiter().GetResult();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task Stop()
    {
        _cts?.Cancel();
        if (_task != null)
        {
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _cts = null;
        _task = null;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
        {
            await UpdateSectionsAsync(Array.Empty<NotePadSection>());
            return;
        }

        await MembershipCache.EnsureLoaded(_httpClient, _config).ConfigureAwait(false);

        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "Failed to refresh notepad state");
            ShowThrottledToast("Unable to load NotePad data.", ex.Message);
            return;
        }

        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                var data = await JsonSerializer.DeserializeAsync<NotePadListResponse>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? new NotePadListResponse();

                var sections = data.Sections?.Select(CloneSection).ToArray() ?? Array.Empty<NotePadSection>();
                await UpdateSectionsAsync(sections).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Failed to synchronize NotePad", ex.Message);
        }
    }

    public async Task<NotePadSection?> CreateSectionAsync(string name, string colorHex, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name
        };

        if (NotePadColorHelper.TryParseColorString(colorHex, out var colorValue))
        {
            payload["color"] = colorValue & 0xFFFFFF;
        }
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/sections";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to create notepad section");
            ShowThrottledToast("Failed to create section", ex.Message);
            return null;
        }

        NotePadSection? created = null;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                created = await JsonSerializer.DeserializeAsync<NotePadSection>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (created != null)
                {
                    await AddOrUpdateSectionAsync(created).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Section name already exists", ex.Message);
        }

        return created;
    }

    public async Task<bool> RenameSectionAsync(string sectionId, string name, CancellationToken cancellationToken)
    {
        var existing = await GetSectionSnapshotAsync(sectionId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            PluginServices.Instance?.Log.Warning(
                "Unable to rename section {SectionId}: not found",
                sectionId);
            return false;
        }

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["version"] = existing.Version
        };
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/sections/{sectionId}";
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to rename notepad section");
            ShowThrottledToast("Failed to rename section", ex.Message);
            return false;
        }

        var success = false;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                var updated = await JsonSerializer.DeserializeAsync<NotePadSection>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (updated != null)
                {
                    success = true;
                    await AddOrUpdateSectionAsync(updated).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Section name already exists", ex.Message);
        }

        return success;
    }

    public async Task<bool> DeleteSectionAsync(string sectionId, CancellationToken cancellationToken)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/sections/{sectionId}";
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to delete notepad section");
            ShowThrottledToast("Failed to delete section", ex.Message);
            return false;
        }

        var success = false;
        try
        {
            await HandleResponseAsync(response, _ =>
            {
                success = true;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Unable to delete section", ex.Message);
        }

        if (success)
        {
            await RemoveSectionAsync(sectionId).ConfigureAwait(false);
        }

        return success;
    }

    public async Task<bool> ReorderSectionsAsync(IReadOnlyList<string> sectionIds, CancellationToken cancellationToken)
    {
        var actualIds = sectionIds.Where(id => !IsBuiltInSectionId(id)).ToArray();
        if (actualIds.Length == 0)
        {
            return true;
        }

        var payload = new { sectionIds = actualIds };
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/sections/reorder";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to reorder sections");
            ShowThrottledToast("Failed to reorder sections", ex.Message);
            return false;
        }

        var success = false;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                success = true;
                var data = await JsonSerializer.DeserializeAsync<NotePadListResponse>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? new NotePadListResponse();
                await UpdateSectionsAsync(data.Sections ?? new List<NotePadSection>()).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Unable to reorder sections", ex.Message);
        }

        return success;
    }

    public async Task<NotePadPage?> CreatePageAsync(string sectionId, string title, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["sectionId"] = sectionId,
            ["title"] = title,
            ["content"] = string.Empty
        };

        var color = await GetSectionColorAsync(sectionId, cancellationToken).ConfigureAwait(false);
        if (NotePadColorHelper.TryParseColorString(color, out var colorValue))
        {
            payload["color"] = colorValue & 0xFFFFFF;
        }
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/pages";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to create notepad page");
            ShowThrottledToast("Failed to create page", ex.Message);
            return null;
        }

        NotePadPage? created = null;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                created = await JsonSerializer.DeserializeAsync<NotePadPage>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (created != null)
                {
                    await AddOrUpdatePageAsync(sectionId, created).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Page title already exists", ex.Message);
        }

        return created;
    }

    public async Task<bool> RenamePageAsync(string sectionId, string pageId, string title, CancellationToken cancellationToken)
    {
        var existing = await GetPageSnapshotAsync(sectionId, pageId, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            PluginServices.Instance?.Log.Warning("Unable to rename page {PageId}: not found in section {SectionId}", pageId, sectionId);
            return false;
        }

        var payload = new Dictionary<string, object?>
        {
            ["version"] = existing.Version,
            ["title"] = title
        };
        if (NotePadColorHelper.TryParseColorString(existing.Color, out var color))
        {
            payload["color"] = color & 0xFFFFFF;
        }

        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/pages/{pageId}";
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to rename notepad page");
            ShowThrottledToast("Failed to rename page", ex.Message);
            return false;
        }

        var success = false;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                var updated = await JsonSerializer.DeserializeAsync<NotePadPage>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (updated != null)
                {
                    success = true;
                    await AddOrUpdatePageAsync(sectionId, updated).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Page title already exists", ex.Message);
        }

        return success;
    }

    public async Task<bool> DeletePageAsync(string sectionId, string pageId, CancellationToken cancellationToken)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/pages/{pageId}";
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to delete notepad page");
            ShowThrottledToast("Failed to delete page", ex.Message);
            return false;
        }

        var success = false;
        try
        {
            await HandleResponseAsync(response, _ =>
            {
                success = true;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Unable to delete page", ex.Message);
        }

        if (success)
        {
            await RemovePageAsync(sectionId, pageId).ConfigureAwait(false);
        }

        return success;
    }

    public async Task<bool> ReorderPagesAsync(string sectionId, IReadOnlyList<string> pageIds, CancellationToken cancellationToken)
    {
        var payload = new
        {
            sections = new[]
            {
                new
                {
                    sectionId,
                    pageIds = pageIds.ToArray()
                }
            }
        };
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/pages/reorder";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to reorder notepad pages");
            ShowThrottledToast("Failed to reorder pages", ex.Message);
            return false;
        }

        var success = false;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                success = true;
                var data = await JsonSerializer.DeserializeAsync<NotePadListResponse>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? new NotePadListResponse();
                await UpdateSectionsAsync(data.Sections ?? new List<NotePadSection>()).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException ex)
        {
            ShowThrottledToast("Unable to reorder pages", ex.Message);
        }

        return success;
    }

    public async Task<NotePadPage?> SavePageAsync(string sectionId, string pageId, NotePadPageUpdate update, CancellationToken cancellationToken)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}/api/notepad/pages/{pageId}/content";
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json")
        };
        ApiHelpers.AddAuthHeader(request, _tokenManager);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Error(ex, "Failed to save notepad page");
            ShowThrottledToast("Failed to save page", ex.Message);
            return null;
        }

        NotePadPage? page = null;
        try
        {
            await HandleResponseAsync(response, async stream =>
            {
                page = await JsonSerializer.DeserializeAsync<NotePadPage>(stream, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (page != null)
                {
                    await AddOrUpdatePageAsync(sectionId, page).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (NotePadConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "Unexpected NotePad save failure");
        }

        return page;
    }

    private async Task<NotePadPage?> GetPageSnapshotAsync(string sectionId, string pageId, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var section = _sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.Ordinal));
            var page = section?.Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
            return page != null ? ClonePage(page) : null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<NotePadSection?> GetSectionSnapshotAsync(string sectionId, CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var section = _sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.Ordinal));
            return section != null ? CloneSection(section) : null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<string?> GetSectionColorAsync(string sectionId, CancellationToken cancellationToken)
    {
        var section = await GetSectionSnapshotAsync(sectionId, cancellationToken).ConfigureAwait(false);
        return section?.Color;
    }

    private async Task HandleResponseAsync(HttpResponseMessage response, Func<Stream, Task> onSuccess)
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _tokenManager.Clear("Authentication failed");
            ShowThrottledToast("Authentication failed", string.Empty);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            ShowThrottledToast("Forbidden – check API key/roles", string.Empty);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new NotePadConflictException(await ReadErrorAsync(stream).ConfigureAwait(false));
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(stream).ConfigureAwait(false);
            ShowThrottledToast("NotePad request failed", error);
            return;
        }

        try
        {
            await onSuccess(stream).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            PluginServices.Instance?.Log.Error(ex, "NotePad JSON parse error");
        }
    }

    private static async Task<string> ReadErrorAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task RunAsync(CancellationToken token)
    {
        var baseDelay = TimeSpan.FromSeconds(5);
        while (!token.IsCancellationRequested)
        {
            if (!_config.Enabled || !_config.NotePadEnabled || !_tokenManager.IsReady() || !ApiHelpers.ValidateApiBaseUrl(_config))
            {
                await Task.Delay(baseDelay, token).ConfigureAwait(false);
                _retryAttempt = 0;
                continue;
            }

            ClientWebSocket? ws = null;
            try
            {
                await RefreshAsync(token).ConfigureAwait(false);

                ws = new ClientWebSocket();
                ApiHelpers.AddAuthHeader(ws, _tokenManager);

                var uri = BuildWebSocketUri();
                if (uri == null)
                {
                    ShowThrottledToast("Invalid NotePad websocket URI", string.Empty);
                    await Task.Delay(baseDelay, token).ConfigureAwait(false);
                    continue;
                }

                await ws.ConnectAsync(uri, token).ConfigureAwait(false);
                _retryAttempt = 0;

                var buffer = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var (message, type) = await ChannelWatcher.ReceiveMessageAsync(ws, buffer, token).ConfigureAwait(false);
                        if (type == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (!string.IsNullOrEmpty(message))
                        {
                            _ = TriggerRefreshAsync();
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (NotePadConflictException conflict)
            {
                ShowThrottledToast("NotePad conflict", conflict.Message);
            }
            catch (HttpRequestException ex)
            {
                PluginServices.Instance?.Log.Warning(ex, "NotePad websocket transport error");
                var status = ApiHelpers.ExtractStatusCode(ex);
                if (status == HttpStatusCode.Unauthorized && _tokenManager.IsReady())
                {
                    _tokenManager.Clear("Authentication failed");
                    ShowThrottledToast("NotePad connection failed", "Authentication failed");
                }
                else if (status == HttpStatusCode.Forbidden)
                {
                    ShowThrottledToast("NotePad connection failed", "Forbidden – check API key/roles");
                }
                else
                {
                    ShowThrottledToast("NotePad connection failed", ex.Message);
                }
            }
            catch (WebSocketException ex)
            {
                PluginServices.Instance?.Log.Warning(ex, "NotePad websocket error");
                var status = ApiHelpers.ExtractStatusCode(ex);
                if (status == HttpStatusCode.Unauthorized && _tokenManager.IsReady())
                {
                    _tokenManager.Clear("Authentication failed");
                    ShowThrottledToast("NotePad connection failed", "Authentication failed");
                }
                else if (status == HttpStatusCode.Forbidden)
                {
                    ShowThrottledToast("NotePad connection failed", "Forbidden – check API key/roles");
                }
                else
                {
                    ShowThrottledToast("NotePad connection failed", ex.Message);
                }
            }
            catch (Exception ex)
            {
                PluginServices.Instance?.Log.Error(ex, "Unexpected NotePad watcher error");
                ShowThrottledToast("NotePad watcher crashed", ex.Message);
            }
            finally
            {
                ws?.Dispose();
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            _retryAttempt++;
            var retryDelay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, _retryAttempt)));
            await Task.Delay(retryDelay, token).ConfigureAwait(false);
        }
    }

    private Uri? BuildWebSocketUri()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiBaseUrl))
        {
            return null;
        }

        try
        {
            var builder = new UriBuilder(_config.ApiBaseUrl)
            {
                Path = "/ws/notepad"
            };
            if (builder.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                builder.Scheme = "ws";
            }
            else if (builder.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                builder.Scheme = "wss";
            }

            return builder.Uri;
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "Failed to build NotePad websocket URI");
            return null;
        }
    }

    private async Task TriggerRefreshAsync()
    {
        if (_refreshLock.CurrentCount == 0)
        {
            return;
        }

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(AutosyncDelay).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "Failed to refresh NotePad state after notification");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task UpdateSectionsAsync(IReadOnlyList<NotePadSection> sections)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _sections.Clear();
            _sections.AddRange(sections.Select(CloneSection));
            InsertBuiltInSection(_sections);
        }
        finally
        {
            _stateLock.Release();
        }

        RaiseChanged();
    }

    private async Task AddOrUpdateSectionAsync(NotePadSection section)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = _sections.FindIndex(s => string.Equals(s.Id, section.Id, StringComparison.Ordinal));
            if (existing >= 0)
            {
                _sections[existing] = CloneSection(section);
            }
            else
            {
                _sections.Add(CloneSection(section));
            }
            InsertBuiltInSection(_sections);
        }
        finally
        {
            _stateLock.Release();
        }

        RaiseChanged();
    }

    private async Task RemoveSectionAsync(string sectionId)
    {
        if (IsBuiltInSectionId(sectionId))
        {
            return;
        }

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _sections.RemoveAll(s => string.Equals(s.Id, sectionId, StringComparison.Ordinal));
            InsertBuiltInSection(_sections);
        }
        finally
        {
            _stateLock.Release();
        }

        RaiseChanged();
    }

    private async Task AddOrUpdatePageAsync(string sectionId, NotePadPage page)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var section = _sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.Ordinal));
            if (section == null)
            {
                return;
            }

            var index = section.Pages.FindIndex(p => string.Equals(p.Id, page.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                section.Pages[index] = ClonePage(page);
            }
            else
            {
                section.Pages.Add(ClonePage(page));
            }
        }
        finally
        {
            _stateLock.Release();
        }

        RaiseChanged();
    }

    private async Task RemovePageAsync(string sectionId, string pageId)
    {
        if (IsBuiltInSectionId(sectionId))
        {
            return;
        }

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var section = _sections.FirstOrDefault(s => string.Equals(s.Id, sectionId, StringComparison.Ordinal));
            section?.Pages.RemoveAll(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
        }
        finally
        {
            _stateLock.Release();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PluginServices.Instance?.Log.Warning(ex, "NotePadService change handler failed");
        }
    }

    private void ShowThrottledToast(string message, string details)
    {
        var signature = $"{message}:{details}";
        if (_lastToastSignature == signature && DateTime.UtcNow - _lastToast < ToastThrottle)
        {
            return;
        }

        _lastToastSignature = signature;
        _lastToast = DateTime.UtcNow;

        PluginServices.Instance?.ToastGui.ShowError(string.IsNullOrEmpty(details) ? message : $"{message}\n{details}");
    }

    public void Dispose()
    {
        _ = Stop();
        _stateLock.Dispose();
        _refreshLock.Dispose();
    }

    internal static bool IsBuiltInSectionId(string? id)
        => string.Equals(id, BuiltInAboutSectionId, StringComparison.Ordinal);

    private static NotePadSection CloneSection(NotePadSection source)
    {
        if (IsBuiltInSectionId(source.Id))
        {
            return CreateBuiltInSection();
        }

        return new NotePadSection
        {
            Id = source.Id,
            Name = source.Name,
            Color = source.Color,
            CreatedById = source.CreatedById,
            CreatedByDiscordId = source.CreatedByDiscordId,
            CreatedByDisplayName = source.CreatedByDisplayName,
            UpdatedById = source.UpdatedById,
            UpdatedByDiscordId = source.UpdatedByDiscordId,
            UpdatedByDisplayName = source.UpdatedByDisplayName,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            Version = source.Version,
            Pages = source.Pages.Select(ClonePage).ToList()
        };
    }

    private static NotePadPage ClonePage(NotePadPage source)
    {
        return new NotePadPage
        {
            Id = source.Id,
            Title = source.Title,
            Content = source.Content,
            Version = source.Version,
            UpdatedAt = source.UpdatedAt,
            CreatedAt = source.CreatedAt,
            Color = source.Color,
            CreatedById = source.CreatedById,
            CreatedByDiscordId = source.CreatedByDiscordId,
            CreatedByDisplayName = source.CreatedByDisplayName,
            UpdatedById = source.UpdatedById,
            UpdatedByDiscordId = source.UpdatedByDiscordId,
            UpdatedByDisplayName = source.UpdatedByDisplayName
        };
    }

    private static void InsertBuiltInSection(IList<NotePadSection> sections)
    {
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            if (IsBuiltInSectionId(sections[i].Id))
            {
                sections.RemoveAt(i);
            }
        }

        sections.Insert(0, CreateBuiltInSection());
    }

    private static NotePadSection CreateBuiltInSection()
    {
        var now = DateTime.UtcNow;
        return new NotePadSection
        {
            Id = BuiltInAboutSectionId,
            Name = "About NotePad",
            Color = NotePadColorHelper.DefaultHexColor,
            CreatedAt = now,
            UpdatedAt = now + BuiltInTimestampEpsilon,
            Version = 1,
            Pages = new List<NotePadPage>
            {
                new()
                {
                    Id = BuiltInAboutPageId,
                    Title = "Test Page",
                    Content = BuildAboutContent(),
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now + BuiltInTimestampEpsilon,
                    Color = NotePadColorHelper.DefaultHexColor
                }
            }
        };
    }

    private static string BuildAboutContent()
    {
        return "Welcome to DemiCat NotePad!\n\n" +
               "• Create tabs to organize your notes. Tabs automatically wrap into additional rows when space runs out." +
               "\n• Use the + button to add tabs and the New Page button to populate them." +
               "\n• Password protect a tab to hide its pages until a password is entered for the current session." +
               "\n• Page authors can delete their own work. Tab owners can delete their tab, and officers can manage all content." +
               "\n• Paste or upload images up to 20 MB. They automatically scale to fit inside the editor while respecting appearance settings." +
               "\n• Changes autosave after a short pause or when you press Ctrl+S." +
               "\n• Locked tabs must be unlocked again after restarting the plugin.";
    }
}

public sealed class NotePadSection
{
    private string _color = NotePadColorHelper.DefaultHexColor;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CreatedById { get; set; }
    public string? CreatedByDiscordId { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public string? UpdatedById { get; set; }
    public string? UpdatedByDiscordId { get; set; }
    public string? UpdatedByDisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; } = 1;

    [JsonConverter(typeof(NotePadColorJsonConverter))]
    public string Color
    {
        get => _color;
        set => _color = NotePadColorHelper.Normalize(value);
    }

    public List<NotePadPage> Pages { get; set; } = new();

    [JsonIgnore]
    public bool IsBuiltIn => NotePadService.IsBuiltInSectionId(Id);
}

public sealed class NotePadPage
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    [JsonConverter(typeof(NotePadColorJsonConverter))]
    public string? Color { get; set; }
    public string? CreatedById { get; set; }
    public string? CreatedByDiscordId { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public string? UpdatedById { get; set; }
    public string? UpdatedByDiscordId { get; set; }
    public string? UpdatedByDisplayName { get; set; }
}

public sealed class NotePadListResponse
{
    public List<NotePadSection>? Sections { get; set; }
}

public sealed class NotePadPageUpdate
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }
}

public sealed class NotePadConflictException : Exception
{
    public NotePadConflictException(string message)
        : base(string.IsNullOrWhiteSpace(message) ? "NotePad content conflict" : message)
    {
    }
}
