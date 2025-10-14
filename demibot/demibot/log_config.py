from __future__ import annotations

import logging
import os
import sys
from typing import Dict

import structlog


DEFAULT_LOG_LEVEL = logging.INFO

_NOISY_LOGGERS: Dict[str, int] = {
    "discord": logging.WARNING,
    "discord.gateway": logging.ERROR,
    "urllib3": logging.WARNING,
}


def _get_log_level_from_env() -> int:
    """Return the log level specified by ``DEMIBOT_LOG_LEVEL`` (default INFO)."""

    level_name = os.getenv("DEMIBOT_LOG_LEVEL")
    if not level_name:
        return DEFAULT_LOG_LEVEL

    level = logging.getLevelName(level_name.upper())
    if isinstance(level, int):
        return level

    logging.getLogger(__name__).warning(
        "Invalid DEMIBOT_LOG_LEVEL=%s, defaulting to INFO", level_name
    )
    return DEFAULT_LOG_LEVEL


def setup_logging() -> None:
    """Configure structlog with JSON output and sensible defaults."""

    level = _get_log_level_from_env()

    logging.basicConfig(level=level, stream=sys.stdout)

    for logger_name, minimum_level in _NOISY_LOGGERS.items():
        logging.getLogger(logger_name).setLevel(max(level, minimum_level))

    structlog.configure(
        processors=[
            structlog.processors.TimeStamper(fmt="iso"),
            structlog.processors.add_log_level,
            structlog.processors.EventRenamer("message"),
            structlog.processors.JSONRenderer(),
        ],
        wrapper_class=structlog.make_filtering_bound_logger(level),
        context_class=dict,
        logger_factory=structlog.stdlib.LoggerFactory(),
    )
