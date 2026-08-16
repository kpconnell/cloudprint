#!/usr/bin/env python3
"""
Fake Quantronix Cubiscan TCP server for end-to-end testing of CloudPrint's tcp-raw path without hardware.

Speaks the real framing: <STX>cmd data<ETX><CR><LF>. Answers T (TA00), U, Z, s (scale only), M (a
measurement, optionally cycling through valid / under-range / NAK), and emits an unsolicited
measurement every --auto seconds (like a gate closing or the operator pressing Measure).

    python3 tools/fake-cubiscan/fake_cubiscan.py --port 1050 --auto 15

Then point a device at it (any OS — the tcp transport is cross-platform):
    "Type": "tcp-raw", "Host": "127.0.0.1", "Port": 1050,
    "FrameMode": "delimited", "FrameStart": "<STX>", "FrameEnd": "<ETX>",
    "InitCommands": [ "<STX>T<ETX>" ], "PollMode": "interval", "PollIntervalMs": 5000, "RequestCommand": "<STX>M<ETX>"
and send a command from the cloud:  {"device":"cubi","command":"<STX>M<ETX>"}
"""
import argparse, itertools, random, socket, threading, time

STX, ETX = b"\x02", b"\x03"

def frame(payload: bytes) -> bytes:
    return STX + payload + ETX + b"\r\n"

def measurement(i: int, variant: str) -> bytes:
    # Legacy/"CS 100-L" packet layout (62 bytes) — the one Rice Lake iDimension emulates too.
    L, W, H = 9.8 + (i % 3) * 0.5, 7.2, 3.5 + (i % 2) * 0.1
    K, D = 1.25 + (i % 4) * 0.25, 0.0
    if variant == "under":
        return frame(b"MAH000000,L____._,W____._,H____._,E,K______,D______,E,F0138,D")
    if variant == "unstable":
        return frame(b"MAH000000,L%05.1f,W%05.1f,H%05.1f,E,K------,D------,E,F0138,D" % (L, W, H))
    if variant == "nak":
        return frame(b"MNHM")
    return frame(b"MAH000000,L%05.1f,W%05.1f,H%05.1f,E,K%06.2f,D%06.2f,E,F0138,D" % (L, W, H, K, D))

def serve(conn: socket.socket, addr, args):
    print(f"[{addr}] connected")
    conn.settimeout(0.2)
    buf = b""
    counter = itertools.count(1)
    variants = itertools.cycle(["ok", "ok", "under", "ok", "unstable", "ok", "nak"] if args.mixed else ["ok"])
    last_auto = time.time()
    try:
        while True:
            try:
                data = conn.recv(256)
                if not data:
                    break
                buf += data
            except socket.timeout:
                pass
            # process complete command frames <STX>...<ETX>[<CR><LF>]
            while STX in buf and ETX in buf:
                s = buf.index(STX); e = buf.index(ETX, s)
                cmd = buf[s + 1:e]; buf = buf[e + 1:].lstrip(b"\r\n")
                print(f"[{addr}] <- {cmd!r}")
                if cmd == b"T":      reply = frame(b"TA00")
                elif cmd == b"U":    reply = frame(b"UAEED0138SLC001")
                elif cmd == b"Z":    reply = frame(b"ZA")
                elif cmd == b"s":    reply = frame(b"sAK001.25,lb")
                elif cmd in (b"M", b"C"):
                    time.sleep(args.measure_delay)
                    reply = measurement(next(counter), next(variants))
                else:                reply = frame(b"?N")
                print(f"[{addr}] -> {reply!r}")
                conn.sendall(reply)
            if args.auto and time.time() - last_auto >= args.auto:
                last_auto = time.time()
                m = measurement(next(counter), "ok").replace(b"MAH", b"MAC")  # originator C = device-initiated
                print(f"[{addr}] -> (unsolicited) {m!r}")
                conn.sendall(m)
    except (ConnectionResetError, BrokenPipeError):
        pass
    finally:
        print(f"[{addr}] closed")
        conn.close()

def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--port", type=int, default=1050)
    p.add_argument("--bind", default="0.0.0.0")
    p.add_argument("--auto", type=float, default=0, help="emit an unsolicited measurement every N seconds (0 = off)")
    p.add_argument("--measure-delay", type=float, default=1.0, help="seconds a measurement takes (real units: 1-7 s)")
    p.add_argument("--mixed", action="store_true", help="cycle valid / under-range / unstable / NAK replies")
    args = p.parse_args()
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.bind, args.port)); srv.listen(5)
    print(f"fake cubiscan listening on {args.bind}:{args.port} (auto={args.auto}s, mixed={args.mixed})")
    while True:
        conn, addr = srv.accept()
        threading.Thread(target=serve, args=(conn, addr, args), daemon=True).start()

if __name__ == "__main__":
    main()
