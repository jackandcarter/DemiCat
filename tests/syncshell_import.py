from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
import types

# Ensure the demibot package is available on sys.path for route imports.
project_root = Path(__file__).resolve().parents[1]
demibot_root = project_root / "demibot"
if str(demibot_root) not in sys.path:
    sys.path.insert(0, str(demibot_root))

# Stub out structlog to avoid optional dependency requirements when importing the route module.
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

syncshell_path = (
    project_root
    / "demibot"
    / "demibot"
    / "http"
    / "routes"
    / "syncshell.py"
)


def _load_syncshell_module():
    spec = importlib.util.spec_from_file_location(
        "demibot.http.routes.syncshell", syncshell_path
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules["demibot.http.routes.syncshell"] = module
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


syncshell = _load_syncshell_module()
