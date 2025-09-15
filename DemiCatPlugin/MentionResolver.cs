using System;
using System.Collections.Generic;
using System.Text;

namespace DemiCatPlugin;

public static class MentionResolver
{
    private static readonly char[] TrimChars = ['!', '.', ',', '?', ';', ':', ')', ']', '}', '>', '\'', '"'];

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static string Resolve(
        string content,
        IEnumerable<PresenceDto> presences,
        IEnumerable<RoleDto> roles,
        IEnumerable<string>? allowedRoleIds = null)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in presences)
        {
            lookup[Normalize(u.Name)] = $"<@{u.Id}>";
        }

        HashSet<string>? allowed = null;
        if (allowedRoleIds != null)
            allowed = new HashSet<string>(allowedRoleIds);

        foreach (var r in roles)
        {
            if (allowed == null || allowed.Contains(r.Id))
                lookup[Normalize(r.Name)] = $"<@&{r.Id}>";
        }

        // Discord special mentions
        lookup[Normalize("everyone")] = "<@everyone>";
        lookup[Normalize("here")] = "<@here>";

        var sb = new StringBuilder(content.Length);

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
                    sb.Append(replacement);
                    sb.Append(suffix);
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

        return sb.ToString();
    }
}
