from __future__ import annotations

import importlib
import pkgutil
import sys
import types


def _install_alembic_stub() -> None:
    if "alembic" in sys.modules:
        return

    alembic_pkg = types.ModuleType("alembic")
    alembic_pkg.__path__ = []  # type: ignore[attr-defined]

    command_module = types.ModuleType("alembic.command")

    def _upgrade(*args: object, **kwargs: object) -> None:  # pragma: no cover - stub
        return None

    command_module.upgrade = _upgrade  # type: ignore[attr-defined]

    config_module = types.ModuleType("alembic.config")

    class _Config:  # pragma: no cover - stub
        def __init__(self) -> None:
            self._options: dict[str, str] = {}

        def set_main_option(self, key: str, value: str) -> None:
            self._options[key] = value

    config_module.Config = _Config  # type: ignore[attr-defined]

    sys.modules["alembic"] = alembic_pkg
    sys.modules["alembic.command"] = command_module
    sys.modules["alembic.config"] = config_module
    alembic_pkg.command = command_module  # type: ignore[attr-defined]
    alembic_pkg.config = config_module  # type: ignore[attr-defined]


def test_routes_module_exports_all_router_modules() -> None:
    _install_alembic_stub()

    import demibot.http.routes as routes

    expected_modules = sorted(
        module_info.name
        for module_info in pkgutil.iter_modules(routes.__path__)  # type: ignore[attr-defined]
        if not module_info.name.startswith("_")
    )

    exported_modules = sorted(routes.__all__)
    assert exported_modules == expected_modules

    for module_name in expected_modules:
        module_via_attr = getattr(routes, module_name)
        module_via_import = importlib.import_module(f"{routes.__name__}.{module_name}")
        assert module_via_attr is module_via_import
