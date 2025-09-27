import sys
from pathlib import Path
import types

project_root = Path(__file__).resolve().parents[1]
demibot_root = project_root / "demibot"
if str(demibot_root) not in sys.path:
    sys.path.insert(0, str(demibot_root))

structlog_stub = types.SimpleNamespace(
    processors=types.SimpleNamespace(
        TimeStamper=lambda fmt=None: None,
        add_log_level=lambda *a, **k: None,
        EventRenamer=lambda *a, **k: None,
        JSONRenderer=lambda *a, **k: None,
    ),
    make_filtering_bound_logger=lambda *a, **k: None,
    stdlib=types.SimpleNamespace(LoggerFactory=lambda: None),
    configure=lambda *a, **k: None,
    get_logger=lambda *a, **k: None,
)
sys.modules.setdefault("structlog", structlog_stub)

if "discord" not in sys.modules:
    discord_module = types.ModuleType("discord")
    commands_module = types.ModuleType("discord.ext.commands")

    class _DummyBot:
        def __init__(self, *args, **kwargs):
            pass

    commands_module.Bot = _DummyBot
    commands_module.AutoShardedBot = _DummyBot

    discord_ext = types.ModuleType("discord.ext")
    discord_ext.commands = commands_module
    discord_module.ext = discord_ext
    discord_module.ClientException = type("ClientException", (Exception,), {})

    sys.modules["discord"] = discord_module
    sys.modules["discord.ext"] = discord_ext
    sys.modules["discord.ext.commands"] = commands_module
