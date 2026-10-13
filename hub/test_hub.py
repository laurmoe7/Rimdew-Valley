import asyncio
import json

import hub


async def client(game):
    r, w = await asyncio.open_connection(hub.HOST, hub.PORT)
    w.write((json.dumps({"type": "hello", "game": game}) + "\n").encode())
    await w.drain()
    return r, w


async def recv(r):
    return json.loads(await asyncio.wait_for(r.readline(), 2))


async def main():
    server = await asyncio.start_server(hub.handle, hub.HOST, hub.PORT)
    async with server:
        sr, sw = await client("stardew")
        assert (await recv(sr))["type"] == "welcome"
        rr, rw = await client("rimworld")
        assert (await recv(rr))["peer_online"] is True
        assert (await recv(sr))["type"] == "peer_status"

        sw.write((json.dumps({"type": "item_send", "item": "Wheat", "count": 5}) + "\n").encode())
        await sw.drain()
        got = await recv(rr)
        assert got["item"] == "RawCorn" and got["count"] == 10, got
        sw.write((json.dumps({"type": "day_end", "backlog": {"water": 3}}) + "\n").encode())
        await sw.drain()
        assert (await recv(rr))["backlog"]["water"] == 3
        sw.close()
        rw.close()
    print("hub OK")


asyncio.run(main())
