import hashlib
import os

from fastapi import FastAPI
from fastapi.testclient import TestClient

from demibot.http.routes.assets import router as assets_router


def _make_app():
    app = FastAPI()
    app.include_router(assets_router)
    return app


def test_download_asset_validates_hash(tmp_path, monkeypatch):
    content = b"hello world"
    file_path = tmp_path / "test.bin"
    file_path.write_bytes(content)
    monkeypatch.setenv("ASSET_STORAGE_PATH", str(tmp_path))
    expected = hashlib.sha256(content).hexdigest()
    client = TestClient(_make_app())
    resp = client.get(f"/assets/test.bin?hash={expected}")
    assert resp.status_code == 200
    assert resp.content == content


def test_download_asset_rejects_bad_hash(tmp_path, monkeypatch):
    content = b"bad"
    file_path = tmp_path / "bad.bin"
    file_path.write_bytes(content)
    monkeypatch.setenv("ASSET_STORAGE_PATH", str(tmp_path))
    client = TestClient(_make_app())
    resp = client.get("/assets/bad.bin?hash=deadbeef")
    assert resp.status_code == 400
