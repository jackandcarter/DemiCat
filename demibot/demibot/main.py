
from __future__ import annotations
import uvicorn
from .config import AppConfig
from .http.api import create_app
from .db.session import create_engine

def main():
    cfg = AppConfig()
    create_engine(cfg.database.url)
    app = create_app(cfg)
    uvicorn.run(app, host=cfg.server.host, port=cfg.server.port)

if __name__ == "__main__":
    main()
