#!/usr/bin/env python3
"""
Installs JellyMold Companion on a Jellyfin server the way an admin would: adds the plugin repository if it is not
there, asks the catalog which version it offers this server, and installs that. Restart the server afterwards.

    JELLYFIN_TOKEN=… tools/catalog-install.py --server http://localhost:8097

The token is an admin's, passed in the environment (never as an argument). Standard library only. Prints the version
the server was offered, which is the point of the exercise: a 10.10 server must be offered the 10.x build, a 12
server the 12.x build.
"""
import argparse
import json
import os
import sys
import urllib.parse
import urllib.request

REPOSITORY = "https://raw.githubusercontent.com/darkejon/jellymold-companion/main/manifest.json"
GUID = "224d7c95-988f-4534-965f-69a4f6e3d802"

parser = argparse.ArgumentParser()
parser.add_argument("--server", required=True)
parser.add_argument("--dry-run", action="store_true", help="say what would be installed, install nothing")
args = parser.parse_args()
token = os.environ.get("JELLYFIN_TOKEN") or sys.exit("Set JELLYFIN_TOKEN to an admin's token.")
headers = {"Authorization": f'MediaBrowser Token="{token}"', "Content-Type": "application/json"}


def call(method, path, body=None):
    request = urllib.request.Request(args.server + path, data=json.dumps(body).encode() if body is not None else None,
                                     method=method, headers=headers)
    with urllib.request.urlopen(request, timeout=60) as response:
        text = response.read()
        return json.loads(text) if text else None


version = call("GET", "/System/Info")["Version"]
repositories = call("GET", "/Repositories")
if not any(r.get("Url") == REPOSITORY for r in repositories):
    repositories.append({"Name": "JellyMold Companion", "Url": REPOSITORY, "Enabled": True})
    call("POST", "/Repositories", repositories)
    print(f"Added the repository to Jellyfin {version}.")
else:
    print(f"The repository is already on Jellyfin {version}.")

package = next((p for p in call("GET", "/Packages") if p["guid"].replace("-", "") == GUID.replace("-", "")), None)
if package is None:
    sys.exit("The catalog does not list JellyMold Companion. Is the repository reachable from the server?")

# What Jellyfin itself does: the newest version whose targetAbi this server meets.
server = tuple(int(x) for x in version.split("."))
offered = [v for v in package["versions"] if tuple(int(x) for x in v["targetAbi"].split(".")[:3]) <= server[:3]]
best = max(offered, key=lambda v: tuple(int(x) for x in v["version"].split(".")))
print(f"Offered {best['version']} (for Jellyfin {best['targetAbi']}).")
if args.dry_run:
    sys.exit(0)

query = urllib.parse.urlencode({"assemblyGuid": package["guid"], "version": best["version"], "repositoryUrl": best["repositoryUrl"]})
call("POST", "/Packages/Installed/" + urllib.parse.quote(package["name"]) + "?" + query)
print("Installation queued. Restart Jellyfin to load it.")
