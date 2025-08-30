from __future__ import annotations

import os
import json
import asyncio
from urllib.request import Request, urlopen

VAULT_SERVICE_URL = os.getenv("VAULT_SERVICE_URL", "http://vault")

async def presign_upload() -> str:
    loop = asyncio.get_running_loop()

    def _request() -> str:
        req = Request(f"{VAULT_SERVICE_URL}/presign/upload", method="POST")
        with urlopen(req, timeout=10) as resp:
            data = json.load(resp)
            return data["url"]

    return await loop.run_in_executor(None, _request)

async def presign_download(asset_id: str) -> str:
    loop = asyncio.get_running_loop()

    def _request() -> str:
        with urlopen(f"{VAULT_SERVICE_URL}/presign/download/{asset_id}", timeout=10) as resp:
            data = json.load(resp)
            return data["url"]

    return await loop.run_in_executor(None, _request)
