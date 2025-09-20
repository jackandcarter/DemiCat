using System;
using System.Collections.Generic;
using System.Text;

namespace DemiCatPlugin;

public static class MentionResolver
{
    private static readonly char[] TrimChars = ['!', '.', ',', '?', ';', ':', ')', ']', '}', '>', '\'', '"'];

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

        foreach (var u in presences)
        {
            if (string.IsNullOrWhiteSpace(u.Name) || string.IsNullOrWhiteSpace(u.Id))
                continue;
            var mention = new DiscordMentionDto { Id = u.Id, Name = u.Name, Type = "user" };
            lookup[Normalize(u.Name)] = ($"<@{u.Id}>", mention);
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
        }

        // Discord special mentions
        lookup[Normalize("everyone")] = ("<@everyone>", new DiscordMentionDto
        {
            Id = "everyone",
            Name = "everyone",
            Type = "keyword"
        });
        lookup[Normalize("here")] = ("<@here>", new DiscordMentionDto
        {
            Id = "here",
            Name = "here",
            Type = "keyword"
        });

        var sb = new StringBuilder(content.Length);
        var mentions = new List<DiscordMentionDto>();
        var seen = new HashSet<string>();

        for (var i = 0; i < content.Length;)
        {
            if (content[i] == '@')
            {
                var start = i + 1;
                var j = start;
                while (j < content.Length && !char.IsWhiteSpace(content[j]))
                {
                    j++;
                }

                var token = content.Substring(start, j - start);
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

                i = j;
            }
            else
            {
                sb.Append(content[i]);
                i++;
            }
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
}
