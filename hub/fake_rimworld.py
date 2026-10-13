"""Stand-in for the RimWorld mod, for testing the Stardew side alone.
Connects as 'rimworld', prints what Stardew sends, and answers like the real sim would:
  day_end -> npc_state, health_state, work_done, research_state, plus one raid.
Usage: python hub/fake_rimworld.py"""
import json
import socket
import threading
import time

s = socket.create_connection(("127.0.0.1", 7777))
send = lambda m: s.sendall((json.dumps(m) + "\n").encode())
send({"type": "hello", "game": "rimworld"})


def beat():
    while True:
        send({"type": "heartbeat"})
        time.sleep(1)


threading.Thread(target=beat, daemon=True).start()
send({"type": "item_send", "item": "Wood", "count": 10})

npcs = []
for line in s.makefile():
    m = json.loads(line)
    print("<-", line.strip()[:200])
    if m["type"] == "npc_list":
        npcs = m["npcs"]
    if m["type"] == "day_end":
        b = m["backlog"]
        send({"type": "npc_state", "npcs": [
            {"name": n, "mood": 0.8 if i % 3 else 0.2, "break": i == 1, "label": "sad wander" if i == 1 else "ok"}
            for i, n in enumerate(npcs[:6])]})
        send({"type": "health_state", "phase": "day", "dead": False, "downed": False, "health_pct": 0.7,
              "bleed": 0.1, "pain": 0.2, "moving": 0.8, "manipulation": 0.9, "consciousness": 1.0,
              "injuries": ["Cut (left arm)", "Bruise (torso)"]})
        send({"type": "work_done", "water": b["water"], "harvest": b["harvest"], "animals": b["animals"],
              "hands": [{"name": "Bob", "mood": 0.6, "plants": 7, "animals": 3, "hours": 8.0, "status": "ok"}]})
        send({"type": "research_state", "target": "", "projects": [
            {"id": "Keg", "label": "kegs", "progress": 120, "cost": 300, "done": False}]})
        send({"type": "incident", "def": "RaidEnemy", "label": "raid", "category": "ThreatBig", "points": 180})
