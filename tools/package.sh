#!/usr/bin/env bash
# Builds both release zips and adds them to manifest.json — the plugin repository an admin adds under
# Dashboard → Plugins → Repositories (design §13a).
#
#   tools/package.sh "What changed, in a line or two."
#
# Produces artifacts/jellymold-companion_10.<release>.zip (Jellyfin 10.10) and _12.<release>.zip (Jellyfin 12), each
# holding the plugin's two assemblies and a meta.json, with an .md5 beside it — MD5 being what Jellyfin checks before
# it installs. The zips are attached to a GitHub release named v<release>; manifest.json points at them there.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

CHANGELOG="${1:?Say what changed: tools/package.sh \"…\"}"
RELEASE="$(sed -n 's:.*<ReleaseVersion>\(.*\)</ReleaseVersion>.*:\1:p' Directory.Build.props)"
REPO="darkejon/jellymold-companion"
mkdir -p artifacts

dotnet test tests/JellyMoldCompanion.Core.Tests -c Release --nologo -v quiet

for build in "net8.0 10 10.10.7.0" "net10.0 12 12.1.0.0"; do
  read -r framework major abi <<<"$build"
  version="$major.$RELEASE"
  dotnet build src/Jellyfin.Plugin.JellyMoldCompanion -c Release -f "$framework" --nologo -v quiet | grep -E "error|warn" && exit 1 || true
  stage="$(mktemp -d)"
  cp "src/Jellyfin.Plugin.JellyMoldCompanion/bin/Release/$framework/Jellyfin.Plugin.JellyMoldCompanion.dll" \
     "src/Jellyfin.Plugin.JellyMoldCompanion/bin/Release/$framework/JellyMoldCompanion.Core.dll" "$stage/"
  tools/write-meta.sh "$stage" "$version" "$abi" "$CHANGELOG"
  zip="artifacts/jellymold-companion_$version.zip"
  rm -f "$zip"
  (cd "$stage" && zip -q -X "$OLDPWD/$zip" ./*)
  rm -rf "$stage"
  md5 -q "$zip" > "$zip.md5"
  echo "$zip  md5 $(cat "$zip.md5")"
done

python3 -I - "$RELEASE" "$CHANGELOG" "$REPO" <<'PY'
import datetime, json, pathlib, sys
release, changelog, repo = sys.argv[1:4]
path = pathlib.Path("manifest.json")
manifest = json.loads(path.read_text()) if path.exists() else [{
    "guid": "224d7c95-988f-4534-965f-69a4f6e3d802",
    "name": "JellyMold Companion",
    "overview": "Watch history for JellyMold and any other app.",
    "description": "A permanent record of everything each person watches and listens to on this server, readable by each "
                   "person from an app. Survives media being deleted from the library; never deletes history.",
    "owner": "darkejon",
    "category": "General",
    "versions": [],
}]
plugin = manifest[0]
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
for major, abi in (("10", "10.10.7.0"), ("12", "12.1.0.0")):
    version = f"{major}.{release}"
    zip_name = f"jellymold-companion_{version}.zip"
    plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
    plugin["versions"].append({
        "version": version,
        "changelog": changelog,
        "targetAbi": abi,
        "sourceUrl": f"https://github.com/{repo}/releases/download/v{release}/{zip_name}",
        "checksum": pathlib.Path(f"artifacts/{zip_name}.md5").read_text().strip(),
        "timestamp": now,
    })
# Newest first. A 12 build always outranks a 10.10 one, so a Jellyfin 12 server never installs the 10.10 build.
plugin["versions"].sort(key=lambda v: tuple(int(p) for p in v["version"].split(".")), reverse=True)
path.write_text(json.dumps(manifest, indent=2) + "\n")
print(f"manifest.json: {len(plugin['versions'])} versions")
PY
