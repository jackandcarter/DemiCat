using System;
using System.Collections.Generic;
using System.Text;

namespace DemiCatPlugin;

public static class MentionResolver
{
    private static readonly char[] TrimChars = ['!', '.', ',', '?', ';', ':', ')', ']', '}', '>', '\'', '"'];
    private const char MetadataMarker = '\u2063';

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public sealed class MentionResolution
    {
        public MentionResolution(string content, List<DiscordMentionDto> mentions)
        {
            Content = content;
            Mentions = mentions;
        }

        public string Content { get; }
        public List<DiscordMentionDto> Mentions { get; }
    }

    public static MentionResolution ResolveDetailed(
        string content,
        IEnumerable<PresenceDto> presences,
        IEnumerable<RoleDto> roles,
        IEnumerable<string>? allowedRoleIds = null)
    {
        var lookup = new Dictionary<string, (string Replacement, DiscordMentionDto? Mention)>(StringComparer.OrdinalIgnoreCase);
        var mentionsById = new Dictionary<string, DiscordMentionDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in presences)
        {
            if (string.IsNullOrWhiteSpace(u.Name) || string.IsNullOrWhiteSpace(u.Id))
                continue;
            var mention = new DiscordMentionDto { Id = u.Id, Name = u.Name, Type = "user" };
            lookup[Normalize(u.Name)] = ($"<@{u.Id}>", mention);
            mentionsById[u.Id] = mention;
        }

        HashSet<string>? allowed = null;
        if (allowedRoleIds != null)
            allowed = new HashSet<string>(allowedRoleIds);

        foreach (var r in roles)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Id))
                continue;
            if (allowed != null && !allowed.Contains(r.Id))
                continue;
            var mention = new DiscordMentionDto { Id = r.Id, Name = r.Name, Type = "role" };
            lookup[Normalize(r.Name)] = ($"<@&{r.Id}>", mention);
            mentionsById[r.Id] = mention;
        }

        // Discord special mentions
        var everyoneMention = new DiscordMentionDto
        {
            Id = "everyone",
            Name = "everyone",
            Type = "keyword"
        };
        lookup[Normalize("everyone")] = ("<@everyone>", everyoneMention);
        mentionsById[everyoneMention.Id] = everyoneMention;

        var hereMention = new DiscordMentionDto
        {
            Id = "here",
            Name = "here",
            Type = "keyword"
        };
        lookup[Normalize("here")] = ("<@here>", hereMention);
        mentionsById[hereMention.Id] = hereMention;

        var sb = new StringBuilder(content.Length);
        var mentions = new List<DiscordMentionDto>();
        var seen = new HashSet<string>();

        for (var i = 0; i < content.Length;)
        {
            if (TryConsumeStructuredMention(content, ref i, sb, mentions, seen, mentionsById))
            {
                continue;
            }

            if (content[i] == '@')
            {
                if (TryConsumeAtMention(content, lookup, mentionsById, sb, mentions, seen, ref i))
                {
                    continue;
                }
            }

            sb.Append(content[i]);
            i++;
        }

        return new MentionResolution(sb.ToString(), mentions);
    }

    public static string Resolve(
        string content,
        IEnumerable<PresenceDto> presences,
        IEnumerable<RoleDto> roles,
        IEnumerable<string>? allowedRoleIds = null)
    {
        return ResolveDetailed(content, presences, roles, allowedRoleIds).Content;
    }

    private static bool TryConsumeStructuredMention(
        string content,
        ref int index,
        StringBuilder sb,
        List<DiscordMentionDto> mentions,
        HashSet<string> seen,
        Dictionary<string, DiscordMentionDto> mentionsById)
    {
        var span = content.AsSpan(index);
        if (!TryParseStructuredMention(span, mentionsById, out var consumed, out var mention))
        {
            return false;
        }

        sb.Append(span.Slice(0, consumed));
        if (mention != null && seen.Add(mention.Id))
        {
            mentions.Add(mention);
        }

        index += consumed;
        return true;
    }

    private static bool TryConsumeAtMention(
        string content,
        Dictionary<string, (string Replacement, DiscordMentionDto? Mention)> lookup,
        Dictionary<string, DiscordMentionDto> mentionsById,
        StringBuilder sb,
        List<DiscordMentionDto> mentions,
        HashSet<string> seen,
        ref int index)
    {
        var start = index + 1;
        var j = start;
        while (j < content.Length && !char.IsWhiteSpace(content[j]))
        {
            j++;
        }

        var token = content.Substring(start, j - start);

        if (TryResolveMetadataMention(token, mentionsById, sb, mentions, seen))
        {
            index = j;
            return true;
        }

        var name = token.TrimEnd(TrimChars);
        var suffix = token.Substring(name.Length);
        var key = Normalize(name);

        if (lookup.TryGetValue(key, out var replacement))
        {
            sb.Append(replacement.Replacement);
            sb.Append(suffix);

            if (replacement.Mention != null && seen.Add(replacement.Mention.Id))
            {
                mentions.Add(replacement.Mention);
            }
        }
        else
        {
            sb.Append("@\u200B");
            sb.Append(token);
        }

        index = j;
        return true;
    }

    private static bool TryResolveMetadataMention(
        string token,
        Dictionary<string, DiscordMentionDto> mentionsById,
        StringBuilder sb,
        List<DiscordMentionDto> mentions,
        HashSet<string> seen)
    {
        var metadataIndex = token.IndexOf(MetadataMarker);
        if (metadataIndex < 0)
        {
            return false;
        }

        var metadataSpan = token.AsSpan(metadataIndex + 1);
        if (!TryParseStructuredMention(metadataSpan, mentionsById, out var consumed, out var mention))
        {
            return false;
        }

        sb.Append(metadataSpan.Slice(0, consumed));
        var suffix = metadataSpan.Slice(consumed);
        if (!suffix.IsEmpty)
        {
            sb.Append(suffix);
        }

        if (mention != null && seen.Add(mention.Id))
        {
            mentions.Add(mention);
        }

        return true;
    }

    private static bool TryParseStructuredMention(
        ReadOnlySpan<char> span,
        Dictionary<string, DiscordMentionDto> mentionsById,
        out int consumed,
        out DiscordMentionDto? mention)
    {
        consumed = 0;
        mention = null;

        if (span.Length < 3 || span[0] != '<' || span[1] != '@')
        {
            return false;
        }

        var index = 2;
        var prefix = '\0';
        if (index < span.Length && (span[index] == '&' || span[index] == '!'))
        {
            prefix = span[index];
            index++;
        }

        var idStart = index;
        while (index < span.Length && span[index] != '>')
        {
            index++;
        }

        if (index >= span.Length)
        {
            return false;
        }

        var idSpan = span.Slice(idStart, index - idStart);
        if (idSpan.IsEmpty)
        {
            return false;
        }

        consumed = index + 1;
        var id = idSpan.ToString();

        if (!mentionsById.TryGetValue(id, out var resolved))
        {
            var type = prefix == '&'
                ? "role"
                : id.Equals("everyone", StringComparison.OrdinalIgnoreCase) || id.Equals("here", StringComparison.OrdinalIgnoreCase)
                    ? "keyword"
                    : "user";

            resolved = new DiscordMentionDto
            {
                Id = id,
                Name = id,
                Type = type
            };

            mentionsById[id] = resolved;
        }

        mention = resolved;
        return true;
    }
}
