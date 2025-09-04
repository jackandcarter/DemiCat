using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DemiCatPlugin;

public static class MentionResolver
{
    public static string Resolve(string content, IEnumerable<PresenceDto> presences, IEnumerable<RoleDto> roles)
    {
        foreach (var u in presences)
        {
            content = Regex.Replace(content, $"@{Regex.Escape(u.Name)}\\b", $"<@{u.Id}>");
        }
        foreach (var r in roles)
        {
            content = Regex.Replace(content, $"@{Regex.Escape(r.Name)}\\b", $"<@&{r.Id}>");
        }
        return content;
    }
}
