# Python ML scoring service (FastAPI) and training tooling.
# Python 3.12 matches the local training environment so registered joblib artifacts stay loadable.
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    ML_PROJECT_ROOT=/app \
    MPLBACKEND=Agg

WORKDIR /app/service
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app/ app/
RUN useradd --create-home --uid 10001 mlsvc && mkdir -p /app/artifacts/models /app/artifacts/evaluations /app/data/generated /app/config \
    && chown -R mlsvc /app
USER mlsvc

EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8000/health/ready', timeout=4).status == 200 else 1)"
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
