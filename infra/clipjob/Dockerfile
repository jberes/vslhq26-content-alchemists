# Castmill clip-export worker — runs as an Azure Container Apps job scaled by
# the clip-jobs storage queue (KEDA azure-queue scaler). ADR-008: ffmpeg never
# runs on API instances.
FROM python:3.12-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir azure-storage-blob==12.* azure-storage-queue==12.* azure-identity==1.* requests==2.*

WORKDIR /app
COPY worker.py .

# Non-root: the worker only needs /tmp scratch space.
RUN useradd --create-home worker
USER worker

ENTRYPOINT ["python", "worker.py"]
