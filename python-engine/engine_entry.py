"""Entry point for the packaged engine. Serves the correction engine on 127.0.0.1 only, on the port the app chose
(TYPRYT_ENGINE_PORT). The app also passes a token (TYPRYT_ENGINE_TOKEN) that every request must carry."""

import os
import sys

import uvicorn

from main import app

if __name__ == "__main__":
    try:
        port = int(os.environ["TYPRYT_ENGINE_PORT"])
    except (KeyError, ValueError):
        sys.exit("TYPRYT_ENGINE_PORT is not set: the engine is started by the Typryt app")
    uvicorn.run(app, host="127.0.0.1", port=port)
