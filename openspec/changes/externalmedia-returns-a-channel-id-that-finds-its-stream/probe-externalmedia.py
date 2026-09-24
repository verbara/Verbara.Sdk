#!/usr/bin/env python3
"""Measures what Asterisk puts on an AudioSocket wire for POST /channels/externalMedia.

Dependency-free on the SDK on purpose: nothing in this repository decodes a byte here. A parser
checked against its own encoder agrees with itself and says nothing about the wire — that closed
loop is what ADR-0060 exists to break, and this probe is the other end of it.

Run it against a container started as:

    docker run -d --rm --name as-probe --network host \
      -v "$PWD/docker/functional/asterisk-config:/etc/asterisk:ro" verbara/asterisk-local:22

Then: python3 probe-externalmedia.py  (needs the `websocket-client` package)

Every run prints its FULL query string. The first version of probe-capture.txt recorded parameters
in prose instead, and a run labelled "what the SDK sends" turned out to carry an extra parameter
nobody could see. Print the request or the capture cannot be audited.
"""
import base64, json, socket, threading, time, urllib.error, urllib.parse, urllib.request, uuid

import websocket  # websocket-client

ARI = "http://127.0.0.1:8088/ari"
AUTH = base64.b64encode(b"testari:testari").decode()
PORT = 19099


def _listen(box, seconds):
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", PORT))
    s.listen(4)
    s.settimeout(seconds)
    box["bytes"] = b""
    try:
        conn, _ = s.accept()
    except socket.timeout:
        s.close()
        return
    conn.settimeout(1.0)
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            chunk = conn.recv(65536)
        except socket.timeout:
            continue
        if not chunk:
            break
        box["bytes"] += chunk
        # The identification frame is 3 header bytes + 16 UUID bytes. Stop there, before audio
        # floods the capture.
        if len(box["bytes"]) >= 19 and box["bytes"][0] == 0x01:
            break
    conn.close()
    s.close()


def run(label, params, listener=True, seconds=4.0):
    box = {}
    thread = None
    if listener:
        thread = threading.Thread(target=_listen, args=(box, seconds))
        thread.start()
        time.sleep(0.3)

    query = urllib.parse.urlencode(params)
    request = urllib.request.Request(f"{ARI}/channels/externalMedia?{query}", method="POST")
    request.add_header("Authorization", "Basic " + AUTH)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            body = json.loads(response.read().decode())
            outcome = f"HTTP {response.status}  id={body.get('id')}  name={body.get('name')}"
    except urllib.error.HTTPError as error:
        outcome = f"HTTP {error.code}  {json.loads(error.read().decode()).get('message')}"

    if thread:
        thread.join()

    print(f"\n{label}")
    print(f"  QUERY   : {query}")
    print(f"  LISTENER: {'up' if listener else 'down'}")
    print(f"  ->        {outcome}")

    captured = box.get("bytes", b"")
    if captured:
        head = captured[:19]
        print(f"  HEX     : {' '.join(f'{b:02x}' for b in head)}")
        if len(head) >= 19 and head[0] == 0x01:
            p = head[3:19]
            wire = "%s-%s-%s-%s-%s" % (
                p[0:4].hex(), p[4:6].hex(), p[6:8].hex(), p[8:10].hex(), p[10:16].hex())
            print(f"  UUID    : {wire}")
            print(f"            == channelId ? {wire == params.get('channelId')}"
                  f"   == data ? {wire == params.get('data')}")
    return box.get("bytes", b"")


def main():
    # externalMedia never checks that `app` is registered, but the channel it creates runs Stasis
    # afterwards: with no subscriber Asterisk hangs it up and the session disappears in
    # milliseconds. Subscribe first, or a run measures a teardown race instead of a handshake.
    events = websocket.WebSocket()
    events.connect(
        f"ws://127.0.0.1:8088/ari/events?api_key=testari:testari&app=probe&subscribeAll=true",
        timeout=10)

    host = {"app": "probe", "external_host": f"127.0.0.1:{PORT}"}
    audiosocket = {**host, "format": "slin16", "encapsulation": "audiosocket", "transport": "tcp"}
    try:
        print("=" * 78)
        print("PART 1 — what the SDK sends today")
        run("RUN G — Encapsulation='audiosocket', Transport unset (what the activity sends)",
            {**host, "format": "slin16", "encapsulation": "audiosocket"}, listener=False)
        run("RUN H — the activity's defaults (no encapsulation => rtp/udp)",
            {**host, "format": "slin16"}, listener=False)
        run("RUN D — audiosocket + tcp, no data",
            {**host, "format": "slin", "encapsulation": "audiosocket", "transport": "tcp"},
            listener=False)

        print("\n" + "=" * 78)
        print("PART 2 — which parameter reaches the wire")
        run("RUN A — channelId and data DISTINCT",
            {**audiosocket, "channelId": str(uuid.uuid4()), "data": str(uuid.uuid4())})
        time.sleep(1)
        run("RUN C — data only, no channelId",
            {**audiosocket, "data": str(uuid.uuid4())})

        print("\n" + "=" * 78)
        print("PART 3 — the pattern, crossed against format and against a live target")
        for fmt in ("slin16", "slin"):
            for listening in (True, False):
                identifier = str(uuid.uuid4())
                time.sleep(1)
                run(f"channelId == data, format={fmt}",
                    {**audiosocket, "format": fmt, "channelId": identifier, "data": identifier},
                    listener=listening)
    finally:
        events.close()
    print("\n" + "=" * 78)


if __name__ == "__main__":
    main()
