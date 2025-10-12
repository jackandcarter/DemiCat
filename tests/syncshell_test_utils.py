from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Tuple


def _write_sample_mod(root: Path, *, content: bytes) -> tuple[Path, str, int]:
    mod_root = root / "mods" / "SampleMod"
    mod_root.mkdir(parents=True, exist_ok=True)
    file_path = mod_root / "character.mtrl"
    file_path.write_bytes(content)
    file_hash = hashlib.sha256(content).hexdigest()
    return file_path, file_hash, len(content)


def build_manifest_payload(base: Path) -> Tuple[dict, str]:
    """Construct a SyncShell manifest payload mirroring the plugin schema.

    Parameters
    ----------
    base:
        Temporary directory used to materialise sample files whose hashes are
        referenced by the manifest.

    Returns
    -------
    tuple
        The manifest payload dictionary alongside the computed blob hash.
    """

    file_path, file_hash, size = _write_sample_mod(base, content=b"syncshell-test-payload")

    aggregate = hashlib.sha256(bytes.fromhex(file_hash)).hexdigest()
    last_updated = datetime.now(timezone.utc).isoformat()

    manifest = {
        "protocolVersion": 1,
        "clientId": "test-client",
        "appearance": {
            "collectionId": "default",
            "activeMods": ["sample-mod"],
            "customState": {},
            "lastUpdated": last_updated,
        },
        "collections": [
            {
                "collectionId": "default",
                "mods": [
                    {
                        "modId": "sample-mod",
                        "name": "Sample Mod",
                        "hash": aggregate,
                        "size": size,
                        "files": [
                            {
                                "path": file_path.name,
                                "hash": file_hash,
                                "size": size,
                            }
                        ],
                        "options": {},
                        "tags": ["tests"],
                        "enabled": True,
                        "patches": [],
                        "meta": [],
                    }
                ],
                "removedMods": [],
                "patches": [],
                "removedPatches": [],
                "meta": [],
            }
        ],
        "sizeHints": [
            {
                "path": "default",
                "size": size,
            }
        ],
        "meta": [],
        "wantBlobs": {
            "blobs": [file_hash],
            "chunks": [],
            "sizeHints": [],
        },
        "presence": {
            "status": "online",
        },
    }

    # Normalise through JSON to avoid non-serialisable objects leaking into tests.
    normalised = json.loads(json.dumps(manifest))
    return normalised, file_hash


def build_publish_payload(base: Path, *, discord_id: str = "1234") -> tuple[dict, Path, str]:
    file_path = base / "appearance.bin"
    content = b"syncshell-publish"
    file_path.parent.mkdir(parents=True, exist_ok=True)
    file_path.write_bytes(content)
    file_hash = hashlib.sha256(content).hexdigest()

    payload = {
        "discordId": discord_id,
        "appearance": {
            "actorHash": "actor-1",
            "glamourer": "{\"foo\":1}",
            "blobs": [
                {
                    "name": file_path.name,
                    "sha256": file_hash,
                    "size": len(content),
                }
            ],
        },
        "complete": False,
    }

    return payload, file_path, file_hash
