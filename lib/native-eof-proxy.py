"""Same-host HTTP proxy with a TCP urgent-byte EOF hint for socket polling.

The urgent byte is out of band. It is never appended to the normal JPEG stream.
Only the standard library is used; stdout contains metadata JSON lines.
"""
import argparse
import json
import select
import socket
import sys
import threading
import time

MAX_HEADERS = 8192
MAX_IMAGE = 500 * 1024
stopping = threading.Event()
lock = threading.Lock()
output_lock = threading.Lock()
sockets = set()
workers = set()
slots = threading.BoundedSemaphore(16)
completed = 0
rejected = 0


def report(event, **fields):
    with output_lock:
        print(json.dumps(dict(event=event, **fields), separators=(",", ":")), flush=True)


def track(sock):
    with lock:
        sockets.add(sock)
    return sock


def close_socket(sock):
    if sock is None:
        return
    with lock:
        sockets.discard(sock)
    try:
        sock.close()
    except OSError:
        pass


def headers(sock):
    data = bytearray()
    while b"\r\n\r\n" not in data:
        part = sock.recv(min(4096, MAX_HEADERS + 1 - len(data)))
        if not part:
            raise ValueError("Connection ended before HTTP headers")
        data.extend(part)
        boundary = data.find(b"\r\n\r\n")
        if boundary >= 0:
            if boundary + 4 > MAX_HEADERS:
                raise ValueError("HTTP headers exceed limit")
            return bytes(data[:boundary + 4]), bytes(data[boundary + 4:])
        if len(data) >= MAX_HEADERS:
            raise ValueError("HTTP headers exceed limit")
    raise ValueError("Invalid HTTP headers")


def request_padding(sock, initial):
    # The game's C-string request includes a trailing NUL. Permit only a small
    # zero suffix. Once its terminator is present, drain queued bytes without
    # delaying the response. Wait briefly only when the suffix is still missing.
    padding = bytearray(initial)
    deadline = time.monotonic() + 0.02
    while True:
        if len(padding) > 16 or any(padding):
            raise ValueError("Only up to 16 trailing NUL bytes are supported")
        remaining = deadline - time.monotonic()
        wait = 0 if padding else remaining
        if remaining <= 0 or not select.select([sock], [], [], wait)[0]:
            return len(padding)
        part = sock.recv(17 - len(padding))
        if not part:
            return len(padding)
        padding.extend(part)


def serve(client, args):
    global completed
    started = time.monotonic()
    upstream = None
    sent = 0
    uploaded = 0
    body_bytes = 0
    urgent_sent = False
    padding_length = 0
    error = None
    try:
        client.settimeout(args.timeout / 1000)
        request_headers, extra = headers(client)
        first = request_headers.split(b"\r\n", 1)[0].split(b" ")
        if len(first) != 3 or first[0] != b"GET" or first[2] not in (b"HTTP/1.0", b"HTTP/1.1"):
            raise ValueError("Only HTTP GET requests are supported")
        if first[1].split(b"?", 1)[0] not in (b"/img.jpg", b"/preview.jpg"):
            raise ValueError("Only camera image requests are supported")
        for line in request_headers.split(b"\r\n")[1:]:
            key, _, value = line.partition(b":")
            if key.lower() == b"transfer-encoding" or (key.lower() == b"content-length" and value.strip() != b"0"):
                raise ValueError("Request body is not supported")
        padding_length = request_padding(client, extra)
        upstream = track(socket.create_connection(("127.0.0.1", args.target_port), args.timeout / 1000))
        upstream.sendall(request_headers)
        uploaded = len(request_headers)
        response_headers, initial = headers(upstream)
        length = None
        for line in response_headers.split(b"\r\n")[1:]:
            key, _, value = line.partition(b":")
            if key.lower() == b"transfer-encoding":
                raise ValueError("Chunked upstream response is unsupported")
            if key.lower() == b"content-length":
                if length is not None or not value.strip().isdigit():
                    raise ValueError("Invalid upstream Content-Length")
                length = int(value.strip())
        if length is None or length > MAX_IMAGE or len(initial) > length:
            raise ValueError("Upstream response exceeds limit or has no valid length")
        client.sendall(response_headers)
        sent += len(response_headers)
        if initial:
            client.sendall(initial)
            sent += len(initial)
            body_bytes += len(initial)
        while body_bytes < length:
            part = upstream.recv(min(16384, length - body_bytes))
            if not part:
                raise ValueError("Upstream response ended before declared length")
            client.sendall(part)
            sent += len(part)
            body_bytes += len(part)
        # The tested emulator build drops POLLHUP from read readiness. An
        # urgent indication adds POLLRDBAND so its existing mask observes EOF.
        urgent_sent = client.send(b"\x00", socket.MSG_OOB) == 1
        if not urgent_sent:
            raise OSError("Urgent EOF indication was not sent")
        client.shutdown(socket.SHUT_WR)
    except (OSError, ValueError) as exc:
        error = str(exc)[:240]
    finally:
        close_socket(upstream)
        close_socket(client)
        with lock:
            completed += 1
            number = completed
        slots.release()
        if number <= 10:
            report("self-proxy-complete", completed=number, bytesUpstream=uploaded,
                   bytesDownstream=sent, imageBytes=body_bytes, paddingLength=padding_length, urgentByteSent=urgent_sent,
                   hadError=error is not None, error=error,
                   durationMs=round((time.monotonic() - started) * 1000))
        with lock:
            workers.discard(threading.current_thread())


def control():
    for line in sys.stdin:
        if line.strip() == "STOP":
            break
    stopping.set()


def main():
    global rejected
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--target-port", type=int, required=True)
    parser.add_argument("--timeout", type=int, default=10000)
    args = parser.parse_args()
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        listener.bind((args.host, args.port))
        listener.listen(16)
        listener.settimeout(0.2)
    except OSError as exc:
        report("error", code="EADDRINUSE" if getattr(exc, "winerror", None) == 10048 or exc.errno == 98 else "EPROXYBIND", message=str(exc))
        listener.close()
        return 1
    threading.Thread(target=control, daemon=True).start()
    report("ready", address=args.host, port=listener.getsockname()[1])
    try:
        while not stopping.is_set():
            try:
                client, peer = listener.accept()
            except socket.timeout:
                continue
            if peer[0] != args.host:
                rejected += 1
                if rejected <= 5:
                    report("self-proxy-rejected", remoteAddress=peer[0])
                client.close()
                continue
            if not slots.acquire(blocking=False):
                client.close()
                continue
            track(client)
            worker = threading.Thread(target=serve, args=(client, args), daemon=True)
            with lock:
                workers.add(worker)
            worker.start()
    finally:
        listener.close()
        with lock:
            active = list(sockets)
        for sock in active:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            close_socket(sock)
        deadline = time.monotonic() + 1
        with lock:
            active_workers = list(workers)
        for worker in active_workers:
            worker.join(max(0, deadline - time.monotonic()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
