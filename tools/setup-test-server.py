#!/usr/bin/env python3
"""
Sets up a fresh Jellyfin test server in Docker (tools/docker-10.10.sh): the first-run wizard, an admin, an ordinary
viewer (the plugin's per-person access needs one to test against), and libraries over test-media/.

    tools/setup-test-server.py setup            run once, after `docker-10.10.sh up`
    tools/setup-test-server.py token admin      a fresh token for one of its accounts, for scripts

Test accounts only, on a throwaway container bound to localhost. Their passwords are generated here and kept in
.docker/jellyfin-10.10/test-users.json (gitignored); nothing secret is printed except a token asked for by
`token`, which scripts capture.
"""
import json
import pathlib
import secrets
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

SERVER = "http://localhost:8097"
STORE = pathlib.Path(__file__).resolve().parent.parent / ".docker" / "jellyfin-10.10" / "test-users.json"
CLIENT = 'MediaBrowser Client="JellyMold Companion Setup", Device="Claude", DeviceId="jmc-setup-{who}", Version="0.0.1"'


def call(method, path, body=None, query=None, token=None, who="setup"):
    url = SERVER + path + ("?" + urllib.parse.urlencode(query, doseq=True) if query else "")
    header = CLIENT.format(who=who) + (f', Token="{token}"' if token else "")
    request = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None, method=method,
                                     headers={"Authorization": header, "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        text = response.read()
        return json.loads(text) if text else None


def sign_in(name, password):
    return call("POST", "/Users/AuthenticateByName", {"Username": name, "Pw": password}, who=name)["AccessToken"]


def setup():
    if STORE.exists():
        sys.exit(f"Already set up ({STORE}). Remove it and the container's config to start again.")
    accounts = {"admin": secrets.token_urlsafe(18), "viewer": secrets.token_urlsafe(18)}

    call("POST", "/Startup/Configuration", {"UICulture": "en-GB", "MetadataCountryCode": "GB", "PreferredMetadataLanguage": "en"})
    call("GET", "/Startup/User")
    call("POST", "/Startup/User", {"Name": "admin", "Password": accounts["admin"]})
    call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
    call("POST", "/Startup/Complete")
    time.sleep(2)

    admin = sign_in("admin", accounts["admin"])
    call("POST", "/Users/New", {"Name": "viewer", "Password": accounts["viewer"]}, token=admin)
    for name, kind, folder in [("Films", "movies", "Movies"), ("Shows", "tvshows", "Shows"),
                               ("Music", "music", "Music"), ("Audiobooks", "books", "Audiobooks")]:
        call("POST", "/Library/VirtualFolders", {"LibraryOptions": {}},
             {"name": name, "collectionType": kind, "paths": f"/media/{folder}", "refreshLibrary": "false"}, token=admin)
    call("POST", "/Library/Refresh", token=admin)

    STORE.parent.mkdir(parents=True, exist_ok=True)
    STORE.write_text(json.dumps(accounts))
    STORE.chmod(0o600)
    print("Set up: accounts 'admin' and 'viewer', libraries Films, Shows, Music and Audiobooks. Scanning now.")


def token(name):
    accounts = json.loads(STORE.read_text())
    print(sign_in(name, accounts[name]))


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "setup":
        setup()
    elif len(sys.argv) == 3 and sys.argv[1] == "token":
        token(sys.argv[2])
    else:
        sys.exit(__doc__)
