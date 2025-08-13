import logging
import os

def setup_logging(debug: bool = False) -> None:
    """Configure root logger with console handler.

    Log level can be specified via the ``DEMIBOT_LOG_LEVEL`` environment variable.
    Passing ``debug=True`` forces the level to ``DEBUG`` regardless of the
    environment variable.
    """
    if debug:
        level = logging.DEBUG
    else:
        level_name = os.getenv("DEMIBOT_LOG_LEVEL", "INFO").upper()
        level = getattr(logging, level_name, logging.INFO)

    handler = logging.StreamHandler()
    formatter = logging.Formatter("%(asctime)s %(name)s [%(levelname)s] %(message)s")
    handler.setFormatter(formatter)

    root = logging.getLogger()
    root.setLevel(level)
    root.handlers.clear()
    root.addHandler(handler)
