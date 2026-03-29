#!/usr/bin/env python3
"""
Mock HTTP API server for testing CloudPrint HTTP transport.

Implements the CloudPrint HTTP API spec:
  GET  /print-jobs/next?timeout=N  — long-poll for next job
  PATCH /print-jobs/{id}           — acknowledge job (completed/failed)

Usage:
  python3 mock-api-server.py

Then configure CloudPrint with:
  Transport: http
  ApiUrl: http://localhost:8111/print-jobs/next
  AckUrl: http://localhost:8111/print-jobs
  ApiHeaderName: X-Api-Key
  ApiHeaderValue: test-key
"""

import json
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

# --- Job queue ---
jobs = []
sent_jobs = {}  # id -> job (locked for processing)
completed_jobs = []
failed_jobs = []
job_available = threading.Event()
lock = threading.Lock()

API_KEY = "test-key"

SAMPLE_JOBS = [
    {
        "id": "test-job-1",
        "fileUrl": "https://raw.githubusercontent.com/kpconnell/cloudprint/main/test-assets/sample-label.zpl",
        "contentType": "application/vnd.zebra.zpl",
        "copies": 1,
        "metadata": {"source": "mock-server"}
    },
    {
        "id": "test-job-2",
        "fileUrl": "https://raw.githubusercontent.com/kpconnell/cloudprint/main/LICENSE",
        "contentType": "text/plain",
        "copies": 1,
        "metadata": {"source": "mock-server"}
    },
]


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path != "/print-jobs/next":
            self.send_error(404)
            return

        # Check auth
        api_key = self.headers.get("X-Api-Key")
        if api_key != API_KEY:
            self.send_response(401)
            self.end_headers()
            return

        # Parse timeout
        params = parse_qs(parsed.query)
        timeout = int(params.get("timeout", ["30"])[0])

        # Try to get a job, wait up to timeout
        deadline = time.time() + timeout
        job = None

        while time.time() < deadline:
            with lock:
                if jobs:
                    job = jobs.pop(0)
                    sent_jobs[job["id"]] = job
                    break
            # Wait for signal or 1s intervals
            remaining = deadline - time.time()
            if remaining > 0:
                job_available.wait(timeout=min(1.0, remaining))
                job_available.clear()

        if job is None:
            self.send_response(204)
            self.end_headers()
            print(f"  GET /print-jobs/next -> 204 (no jobs, waited {timeout}s)")
            return

        body = json.dumps(job).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        print(f"  GET /print-jobs/next -> 200 (job {job['id']})")

    def do_PATCH(self):
        parsed = urlparse(self.path)
        parts = parsed.path.strip("/").split("/")
        if len(parts) != 2 or parts[0] != "print-jobs":
            self.send_error(404)
            return

        # Check auth
        api_key = self.headers.get("X-Api-Key")
        if api_key != API_KEY:
            self.send_response(401)
            self.end_headers()
            return

        job_id = parts[1]
        content_length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(content_length)) if content_length > 0 else {}

        status = body.get("status", "unknown")
        error = body.get("error")

        with lock:
            job = sent_jobs.pop(job_id, None)
            if job:
                if status == "completed":
                    completed_jobs.append(job)
                elif status == "failed":
                    job["error"] = error
                    failed_jobs.append(job)

        self.send_response(200)
        self.end_headers()

        if error:
            print(f"  PATCH /print-jobs/{job_id} -> {status}: {error}")
        else:
            print(f"  PATCH /print-jobs/{job_id} -> {status}")

    def log_message(self, format, *args):
        pass  # suppress default logging


def enqueue_job(job):
    with lock:
        jobs.append(job)
    job_available.set()
    print(f"  Enqueued job {job['id']}")


def interactive_menu():
    print("\nCommands:")
    print("  1  — Enqueue sample ZPL label")
    print("  2  — Enqueue sample text file")
    print("  s  — Show status")
    print("  q  — Quit")

    while True:
        try:
            cmd = input("\n> ").strip()
        except (EOFError, KeyboardInterrupt):
            break

        if cmd == "1":
            job = SAMPLE_JOBS[0].copy()
            job["id"] = f"zpl-{int(time.time())}"
            enqueue_job(job)
        elif cmd == "2":
            job = SAMPLE_JOBS[1].copy()
            job["id"] = f"txt-{int(time.time())}"
            enqueue_job(job)
        elif cmd == "s":
            with lock:
                print(f"  Queued: {len(jobs)}, Sent: {len(sent_jobs)}, "
                      f"Completed: {len(completed_jobs)}, Failed: {len(failed_jobs)}")
        elif cmd == "q":
            break


if __name__ == "__main__":
    import sys

    port = 8111
    server = HTTPServer(("0.0.0.0", port), Handler)

    print(f"Mock CloudPrint API server running on http://localhost:{port}")
    print(f"  Fetch:  GET  http://localhost:{port}/print-jobs/next")
    print(f"  Ack:    PATCH http://localhost:{port}/print-jobs/{{id}}")
    print(f"  Key:    X-Api-Key: {API_KEY}")

    if sys.stdin.isatty():
        server_thread = threading.Thread(target=server.serve_forever, daemon=True)
        server_thread.start()
        interactive_menu()
        print("\nShutting down...")
        server.shutdown()
    else:
        # Non-interactive: pre-load sample jobs and serve
        for job in SAMPLE_JOBS:
            enqueue_job(job)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            print("\nShutting down...")
            server.shutdown()
