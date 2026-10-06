import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.api.routes import router
from app.database.db import SessionLocal, init_db
from app.models import AudioFile

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")


@asynccontextmanager
async def lifespan(_: FastAPI):
    init_db()
    # An analysis interrupted by a server restart cannot resume - mark it so the UI offers a re-run.
    with SessionLocal() as db:
        db.query(AudioFile).filter(AudioFile.status == "processing").update(
            {"status": "failed", "stage": "Interrupted", "error": "Server restarted during analysis"})
        db.commit()
    yield


app = FastAPI(title="EchoSense AI", version="1.0.0",
              description="Intelligent Audio Analytics & Spatial Sound Platform", lifespan=lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["http://localhost:5173", "http://127.0.0.1:5173"],
                   allow_methods=["*"], allow_headers=["*"])
app.include_router(router)


@app.get("/api/health")
def health():
    return {"status": "ok"}
