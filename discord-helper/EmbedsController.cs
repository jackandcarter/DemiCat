using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Linq;

namespace DiscordHelper;

[ApiController]
[Route("api/[controller]")]
public class EmbedsController : ControllerBase
{
    private readonly EmbedCache _cache;
    private readonly DiscordSocketClient _client;
    private readonly IOptions<BotConfig> _config;
    private readonly EmbedSocketHandler _sockets;

    public EmbedsController(EmbedCache cache, DiscordSocketClient client, IOptions<BotConfig> config, EmbedSocketHandler sockets)
    {
        _cache = cache;
        _client = client;
        _config = config;
        _sockets = sockets;
    }

    [HttpGet]
    public ActionResult<IEnumerable<EmbedDto>> Get()
    {
        return Ok(_cache.GetAll());
    }

    public class RsvpRequest
    {
        public string Emoji { get; set; } = string.Empty;
    }

    [HttpPost("{id}/rsvp")]
    public async Task<IActionResult> Rsvp(string id, [FromBody] RsvpRequest request)
    {
        if (!ulong.TryParse(id, out var messageId))
        {
            return BadRequest();
        }

        var emoji = new Emoji(request.Emoji);
        var message = await FindMessageAsync(messageId);
        if (message == null)
        {
            return NotFound();
        }

        await message.AddReactionAsync(emoji);

        foreach (var embed in message.Embeds)
        {
            var dto = MapEmbed(embed, message);
            if (_cache.Update(dto))
            {
                await _sockets.BroadcastAsync(dto);
            }
        }

        return NoContent();
    }

    [HttpPost]
    public IActionResult Post(EmbedDto dto)
    {
        // Placeholder for future custom event creation.
        return Ok(dto);
    }

    private async Task<IUserMessage?> FindMessageAsync(ulong messageId)
    {
        foreach (var channelId in _config.Value.ChannelIds)
        {
            if (_client.GetChannel(channelId) is IMessageChannel channel)
            {
                var msg = await channel.GetMessageAsync(messageId) as IUserMessage;
                if (msg != null)
                {
                    return msg;
                }
            }
        }

        return null;
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
