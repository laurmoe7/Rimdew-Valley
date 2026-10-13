"""RimDew relay hub: connects the Stardew and RimWorld mods over localhost TCP.

Protocol: newline-delimited JSON. Every message has a "type".
Clients must first send {"type":"hello","game":"stardew"|"rimworld"}.
The hub forwards messages to the other game after running them through
rules.translate(), and keeps a shared clock/state.
"""
import asyncio
import json
import logging

HOST, PORT = "127.0.0.1", 7777
log = logging.getLogger("hub")

# game name -> StreamWriter
clients: dict[str, asyncio.StreamWriter] = {}
last_seen: dict[str, float] = {}
HEARTBEAT_TIMEOUT = 5.0
state = {"day": None, "season": None, "year": None}


def other(game: str) -> str:
    return "rimworld" if game == "stardew" else "stardew"


# (stardew qualified id, stardew name, rimworld defName, rimworld units per stardew unit)
ITEM_MAP = [
    ("(O)388", "Wood", "WoodLog", 5),
    ("(O)390", "Stone", "BlocksGranite", 3),
    ("(O)24", "Parsnip", "RawPotatoes", 2),
    ("(O)262", "Wheat", "RawCorn", 2),
    ("(O)178", "Hay", "Hay", 10),
    ("(O)176", "Egg", "EggChickenUnfertilized", 1),
    ("(O)184", "Milk", "Milk", 2),
    ("(O)440", "Wool", "WoolSheep", 3),
]
TO_RW = {}
TO_SD = {}
for _sid, _sname, _rw, _rate in ITEM_MAP:
    TO_RW[_sid.lower()] = TO_RW[_sname.lower()] = (_rw, _rate)
    TO_SD[_rw.lower()] = (_sid, _rate)


def translate_item(src: str, msg: dict) -> dict | None:
    count = int(msg.get("count", 1))
    key = str(msg.get("item", "")).lower()
    if src == "stardew" and key in TO_RW:
        rw, rate = TO_RW[key]
        return {**msg, "item": rw, "count": count * rate}
    if src == "rimworld" and key in TO_SD:
        sid, rate = TO_SD[key]
        return {**msg, "item": sid, "count": max(1, count // rate)}
    log.warning("no mapping for %s item %r", src, msg.get("item"))
    return None


def translate(src: str, msg: dict) -> dict | None:
    """Map a message from `src` into what the other game should receive.
    Return None to drop it. Real rules go here (items, events, colonists)."""
    t = msg.get("type")
    if t == "heartbeat":
        return None
    if t == "item_send":
        return translate_item(src, msg)
    # Everything else (npc_event, day_end, health_state, incident, ...) passes straight through;
    # the game mods own the rules. Only items need translating.
    return msg


async def send(game: str, msg: dict) -> None:
    w = clients.get(game)
    if w is None:
        return
    w.write((json.dumps(msg) + "\n").encode())
    await w.drain()


async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
    game = None
    try:
        async for line in reader:
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("type") == "hello":
                game = msg.get("game")
                if game not in ("stardew", "rimworld"):
                    return
                clients[game] = writer
                last_seen[game] = asyncio.get_running_loop().time()
                log.info("%s connected", game)
                await send(game, {"type": "welcome", "peer_online": other(game) in clients})
                await send(other(game), {"type": "peer_status", "online": True})
                continue
            if game is None:
                continue
            last_seen[game] = asyncio.get_running_loop().time()
            if msg.get("type") == "time_sync" and game == "stardew":
                state.update({k: msg.get(k) for k in state})
            out = translate(game, msg)
            if out is not None:
                await send(other(game), out)
    finally:
        if game and clients.get(game) is writer:
            del clients[game]
            log.info("%s disconnected", game)
            await send(other(game), {"type": "peer_status", "online": False})
        writer.close()


async def watchdog():
    """Drop clients that stop sending heartbeats (crashed/frozen game)."""
    while True:
        await asyncio.sleep(1)
        now = asyncio.get_running_loop().time()
        for game, w in list(clients.items()):
            if now - last_seen.get(game, now) > HEARTBEAT_TIMEOUT:
                log.warning("%s heartbeat timeout", game)
                w.close()


async def main():
    asyncio.create_task(watchdog())
    server = await asyncio.start_server(handle, HOST, PORT)
    log.info("hub listening on %s:%d", HOST, PORT)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    asyncio.run(main())
