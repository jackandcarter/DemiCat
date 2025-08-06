using Microsoft.AspNetCore.Mvc;

namespace DiscordHelper;

[ApiController]
[Route("[controller]")]
public class EmbedsController : ControllerBase
{
    [HttpPost]
    public IActionResult Post(EmbedDto dto)
    {
        // In a real bot, this would send the embed to Discord.
        return Ok(dto);
    }
}
