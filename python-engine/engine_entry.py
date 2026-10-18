"""Entry point for the packaged engine. Serves the correction engine on localhost:8000."""

import uvicorn

from main import app

if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8000)
