"""Castmill clip-export worker.

Consumes one message from the clip-jobs storage queue, downloads the source
blob, cuts/crops/captions with ffmpeg, uploads the result, and reports status
through the token-authenticated API callback. Designed for Container Apps jobs:
one message per execution, scale-to-zero when the queue is empty.

Environment:
  STORAGE_ACCOUNT   storage account name (uses managed identity), or
  STORAGE_CONNECTION_STRING as the fallback
  PRIVATE_CONTAINER default "private"
  QUEUE_NAME        default "clip-jobs"
"""

import json
import os
import subprocess
import sys
import tempfile

import requests
from azure.identity import DefaultAzureCredential
from azure.storage.blob import BlobServiceClient
from azure.storage.queue import QueueClient


def clients():
    conn = os.environ.get("STORAGE_CONNECTION_STRING")
    account = os.environ.get("STORAGE_ACCOUNT")
    queue_name = os.environ.get("QUEUE_NAME", "clip-jobs")
    if conn:
        return (
            BlobServiceClient.from_connection_string(conn),
            QueueClient.from_connection_string(conn, queue_name, message_decode_policy=None),
        )
    cred = DefaultAzureCredential()
    return (
        BlobServiceClient(f"https://{account}.blob.core.windows.net", credential=cred),
        QueueClient(f"https://{account}.queue.core.windows.net", queue_name, credential=cred),
    )


def report(job, status, error=None):
    try:
        requests.post(
            job["callbackUrl"],
            json={"token": job["callbackToken"], "status": status, "error": error},
            timeout=30,
        ).raise_for_status()
    except Exception as exc:  # noqa: BLE001 — status reporting must never crash the worker
        print(f"callback failed: {exc}", file=sys.stderr)


def build_ffmpeg_cmd(job, source, captions, output):
    duration = job["outSeconds"] - job["inSeconds"]
    cmd = ["ffmpeg", "-y", "-ss", str(job["inSeconds"]), "-t", str(duration), "-i", source]
    filters = []
    if job.get("cropVertical"):
        # Center-crop to 9:16 for short-form platforms.
        filters.append("crop=ih*9/16:ih")
    if job.get("burnCaptions") and captions:
        escaped = captions.replace("'", r"\'")
        filters.append(f"subtitles='{escaped}'")
    if filters:
        cmd += ["-vf", ",".join(filters), "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac"]
    else:
        # No filtering needed: stream-copy is frame-exact enough for review cuts.
        cmd += ["-c", "copy"]
    cmd += ["-movflags", "+faststart", output]
    return cmd


def main():
    blob_service, queue = clients()
    container = os.environ.get("PRIVATE_CONTAINER", "private")
    messages = queue.receive_messages(messages_per_page=1, visibility_timeout=1800)
    message = next(iter(messages), None)
    if message is None:
        print("queue empty; nothing to do")
        return

    job = json.loads(message.content)
    report(job, "Processing")
    try:
        with tempfile.TemporaryDirectory() as tmp:
            source = os.path.join(tmp, "source")
            output = os.path.join(tmp, "clip.mp4")
            captions = None

            blob = blob_service.get_blob_client(container, job["sourceBlobPath"])
            with open(source, "wb") as f:
                blob.download_blob().readinto(f)

            if job.get("burnCaptions") and job.get("captionsSrt"):
                captions = os.path.join(tmp, "captions.srt")
                with open(captions, "w", encoding="utf-8") as f:
                    f.write(job["captionsSrt"])

            subprocess.run(build_ffmpeg_cmd(job, source, captions, output), check=True, timeout=1700)

            out_blob = blob_service.get_blob_client(container, job["outputBlobPath"])
            with open(output, "rb") as f:
                out_blob.upload_blob(f, overwrite=True, content_type="video/mp4")

        report(job, "Succeeded")
        queue.delete_message(message)
    except Exception as exc:  # noqa: BLE001 — always report, then let the job fail visibly
        report(job, "Failed", error=str(exc)[:1900])
        queue.delete_message(message)
        raise


if __name__ == "__main__":
    main()
