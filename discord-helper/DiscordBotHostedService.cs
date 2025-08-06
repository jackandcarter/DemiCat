using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Linq;

namespace DiscordHelper;

public class DiscordBotHostedService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly EmbedCache _cache;

    public DiscordBotHostedService(DiscordSocketClient client, IOptions<BotConfig> config, EmbedCache cache)
    {
        _client = client;
        _config = config.Value;
        _cache = cache;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.MessageReceived += OnMessageReceived;
        _client.ReactionAdded += OnReactionUpdated;
        _client.ReactionRemoved += OnReactionUpdated;

        await _client.LoginAsync(TokenType.Bot, _config.BotToken);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.MessageReceived -= OnMessageReceived;
        _client.ReactionAdded -= OnReactionUpdated;
        _client.ReactionRemoved -= OnReactionUpdated;

        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private Task OnMessageReceived(SocketMessage msg)
    {
        if (msg is not IUserMessage message)
        {
            return Task.CompletedTask;
        }

        if (message.Author.Id != _config.BotId || message.Embeds.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var embed in message.Embeds)
        {
            var dto = MapEmbed(embed, message);
            _cache.Add(dto);
        }

        return Task.CompletedTask;
    }

    private async Task OnReactionUpdated(Cacheable<IUserMessage, ulong> messageCache,
        Cacheable<IMessageChannel, ulong> channelCache, SocketReaction reaction)
    {
        var message = await messageCache.GetOrDownloadAsync();
        if (message.Author.Id != _config.BotId || message.Embeds.Count == 0)
        {
            return;
        }

        foreach (var embed in message.Embeds)
        {
            var dto = MapEmbed(embed, message);
            _cache.Update(dto);
        }
    }

    private static EmbedDto MapEmbed(IEmbed embed, IUserMessage message)
    {
        return new EmbedDto
        {
            Id = message.Id.ToString(),
            Timestamp = embed.Timestamp,
            Color = embed.Color?.RawValue,
            AuthorName = embed.Author?.Name,
            AuthorIconUrl = embed.Author?.IconUrl,
            Title = embed.Title,
            Description = embed.Description,
            Fields = embed.Fields.Select(f => new EmbedFieldDto
            {
                Name = f.Name,
                Value = f.Value
            }).ToList(),
            ThumbnailUrl = embed.Thumbnail?.Url,
            ImageUrl = embed.Image?.Url,
            Mentions = message.MentionedUserIds.Count > 0 ? message.MentionedUserIds.ToList() : null
        };
    }
}
