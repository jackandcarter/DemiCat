from __future__ import annotations

import importlib
import pkgutil
from types import ModuleType
from typing import Dict, Iterable, List


def _iter_module_names() -> Iterable[str]:
    for module_info in pkgutil.iter_modules(__path__):  # type: ignore[name-defined]
        name = module_info.name
        if not name.startswith("_"):
            yield name


_module_names: List[str] = sorted(_iter_module_names())

__all__ = list(_module_names)

_loaded_modules: Dict[str, ModuleType] = {}


def __getattr__(name: str) -> ModuleType:
    if name in _loaded_modules:
        return _loaded_modules[name]
    if name in __all__:
        module = importlib.import_module(f"{__name__}.{name}")
        _loaded_modules[name] = module
        globals()[name] = module
        return module
    raise AttributeError(name)


def __dir__() -> List[str]:
    return sorted(list(globals().keys()) + __all__)
