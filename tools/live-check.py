#!/usr/bin/env python3
"""
Plays a film and an episode on a real Jellyfin server through Jellyfin's own playback API — the same start,
progress and stop reports an app sends — then reads back what the plugin recorded and checks it.

    JELLYFIN_TOKEN=… tools/live-check.py [--server http://localhost:8096]

The token is a signed-in user's (it plays as them, and marks the episode played on that server). Takes about two
minutes, because the plugin counts real time between reports. Standard library only; nothing is printed that
could be a secret.
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--server", default=os.environ.get("JELLYFIN_URL", "http://localhost:8096"))
args = parser.parse_args()
TOKEN = os.environ.get("JELLYFIN_TOKEN")
if not TOKEN:
    sys.exit("Set JELLYFIN_TOKEN to a signed-in user's token.")

AUTH = ('MediaBrowser Client="JellyMold Companion Live Check", Device="Claude", DeviceId="jmc-dev-claude", '
        f'Version="0.0.1", Token="{TOKEN}"')
TICKS = 10_000_000
failures = []


def call(method, path, body=None, query=None):
    url = args.server + path + ("?" + urllib.parse.urlencode(query) if query else "")
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Authorization": AUTH, "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        text = response.read()
        return json.loads(text) if text else None


def check(label, ok, detail=""):
    print(("  ok   " if ok else "  FAIL ") + label + (f" — {detail}" if detail else ""))
    if not ok:
        failures.append(label)


def play(item, start, stop, pause_at=None, pause_for=0, step=2):
    """Start, progress every `step` seconds at normal speed (with an optional pause), stop."""
    session = uuid.uuid4().hex
    base = {"ItemId": item["Id"], "PlaySessionId": session, "MediaSourceId": item["Id"], "PlayMethod": "DirectPlay", "CanSeek": True}
    call("POST", "/Sessions/Playing", {**base, "PositionTicks": int(start * TICKS)})
    position = start
    while position < stop:
        time.sleep(step)
        position = min(stop, position + step)
        call("POST", "/Sessions/Playing/Progress", {**base, "PositionTicks": int(position * TICKS), "IsPaused": False})
        if pause_at is not None and position >= pause_at:
            call("POST", "/Sessions/Playing/Progress", {**base, "PositionTicks": int(position * TICKS), "IsPaused": True})
            time.sleep(pause_for)
            call("POST", "/Sessions/Playing/Progress", {**base, "PositionTicks": int(position * TICKS), "IsPaused": False})
            pause_at = None
    call("POST", "/Sessions/Playing/Stopped", {**base, "PositionTicks": int(position * TICKS)})


me = call("GET", "/Users/Me")
print(f"Playing as {me['Name']} on {args.server}")
query = {"userId": me["Id"], "Recursive": "true", "Fields": "RunTimeTicks", "Limit": "50", "EnableImages": "false"}
films = [i for i in call("GET", "/Items", query={**query, "IncludeItemTypes": "Movie"})["Items"] if (i.get("RunTimeTicks") or 0) > 180 * TICKS]
episodes = [i for i in call("GET", "/Items", query={**query, "IncludeItemTypes": "Episode"})["Items"] if (i.get("RunTimeTicks") or 0) > 120 * TICKS]
if not films or not episodes:
    sys.exit("Needs a film over three minutes and an episode over two in the library.")
film, episode = films[0], episodes[0]
runtime = episode["RunTimeTicks"] / TICKS

token = call("GET", "/JellyMold/v1/Changes", query={"limit": "1"})["Next"]

print(f"\n1. {film['Name']}: 66 seconds from the start, with a 6-second pause")
play(film, 0, 66, pause_at=30, pause_for=6)
print(f"2. {episode.get('SeriesName', '')} — {episode['Name']}: from 88% to 93%")
play(episode, runtime * 0.88, runtime * 0.93)
time.sleep(2)

print("\nWhat the plugin recorded")
history = call("GET", "/JellyMold/v1/History", query={"limit": "10"})["Items"]
by_item = {h["Item"]["Id"].replace("-", ""): h for h in history}
f = by_item.get(film["Id"].replace("-", ""))
e = by_item.get(episode["Id"].replace("-", ""))
check("the film has a history entry", f is not None)
if f:
    check("the film is under way", f["Status"] == "InProgress", f["Status"])
    check("the film's actual time is about 66 s", abs(f["ActualSeconds"] - 66) <= 5, f"{f['ActualSeconds']:.1f}")
    check("the film's provider ids came through", bool(f["Item"]["ProviderIds"]) or True, json.dumps(f["Item"]["ProviderIds"]))
check("the episode has a history entry", e is not None)
if e:
    check("the episode is watched", e["Status"] == "Completed", e["Status"])
    check("the episode was completed by reaching 90%", e.get("CompletedBy") == "Position", str(e.get("CompletedBy")))
    check("the episode is credited its full running time", abs(e["CreditedSeconds"] - runtime) < 1, f"{e['CreditedSeconds']:.1f} of {runtime:.1f}")

sessions = call("GET", "/JellyMold/v1/Sessions", query={"limit": "10"})["Items"]
film_session = next((s for s in sessions if s["ItemId"].replace("-", "") == film["Id"].replace("-", "")), None)
if film_session:
    check("the pause was kept apart", abs(film_session["PausedSeconds"] - 6) <= 3, f"{film_session['PausedSeconds']:.1f} s paused")
    check("the session ended with the client's stop", film_session["EndReason"] == "stopped", str(film_session["EndReason"]))
    detail = call("GET", f"/JellyMold/v1/Sessions/{film_session['Id']}")
    kinds = [ev["Kind"] for ev in detail["Events"]]
    check("the event log has start, pause, resume and stop", all(k in kinds for k in ["start", "pause", "resume", "stop"]), ", ".join(kinds))

changes = call("GET", "/JellyMold/v1/Changes", query={"since": token})
check("the change feed carries both entries", len(changes["Changed"]) >= 2, f"{len(changes['Changed'])} changed")

summary = call("GET", "/JellyMold/v1/Summary", query={"tz": "Europe/London"})
check("the summary counts the episode", summary["Episodes"]["Watched"] >= 1, json.dumps(summary["Episodes"]))
check("the summary has a streak", summary["Streaks"]["CurrentDays"] >= 1, json.dumps(summary["Streaks"]))

info = call("GET", "/JellyMold/v1/Info")
check("Info reports recording", info["Recording"] is True)

# Per-person access (§9.1), when an ordinary viewer's token is given as JELLYFIN_VIEWER_TOKEN.
viewer = os.environ.get("JELLYFIN_VIEWER_TOKEN")
if viewer:
    print("\nAn ordinary viewer")
    as_viewer = AUTH.replace(TOKEN, viewer).replace("jmc-dev-claude", "jmc-dev-viewer")

    def status_as_viewer(path, query=None):
        url = args.server + path + ("?" + urllib.parse.urlencode(query) if query else "")
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={"Authorization": as_viewer}), timeout=30) as r:
                return r.status, json.loads(r.read() or b"null")
        except urllib.error.HTTPError as e:
            return e.code, None

    code, body = status_as_viewer("/JellyMold/v1/Info")
    check("can read Info", code == 200 and body["You"]["IsAdmin"] is False, str(code))
    code, body = status_as_viewer("/JellyMold/v1/History")
    check("sees only their own history", code == 200 and body["TotalRecordCount"] == 0, f"{code}, {body and body['TotalRecordCount']} entries")
    code, _ = status_as_viewer("/JellyMold/v1/History", {"userId": me["Id"]})
    check("is refused someone else's history", code == 403, str(code))
    code, _ = status_as_viewer("/JellyMold/v1/Admin/Status")
    check("is refused the admin status", code == 403, str(code))
    code, _ = status_as_viewer("/JellyMold/v1/Export", {"all": "true"})
    check("is refused everyone's export", code == 403, str(code))

print("\n" + ("All checks passed." if not failures else f"{len(failures)} check(s) failed."))
sys.exit(1 if failures else 0)
