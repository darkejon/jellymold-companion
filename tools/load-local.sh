#!/usr/bin/env bash
# Builds the Jellyfin 12 version of the plugin and loads it into the local test server (CLAUDE.md: the native
# Jellyfin.app on this Mac), then quits and reopens Jellyfin.app and waits for it to come back.
#
#   tools/load-local.sh            build, install, restart
#   tools/load-local.sh --no-restart
#
set -euo pipefail

cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

SERVER="${JELLYFIN_URL:-http://localhost:8096}"
PLUGINS="$HOME/Library/Application Support/jellyfin/plugins"
VERSION="$(sed -n 's:.*<ReleaseVersion>\(.*\)</ReleaseVersion>.*:\1:p' Directory.Build.props)"
TARGET="$PLUGINS/JellyMold Companion_12.$VERSION"

dotnet build src/Jellyfin.Plugin.JellyMoldCompanion -c Release -f net10.0 --nologo -v quiet | grep -E "error|warn" && exit 1 || true
OUT="src/Jellyfin.Plugin.JellyMoldCompanion/bin/Release/net10.0"

# Older copies of this plugin first: Jellyfin loads one folder per plugin, the newest version.
find "$PLUGINS" -maxdepth 1 -type d -name 'JellyMold Companion_*' -print0 | xargs -0 rm -rf
mkdir -p "$TARGET"
cp "$OUT/Jellyfin.Plugin.JellyMoldCompanion.dll" "$OUT/JellyMoldCompanion.Core.dll" "$TARGET/"
cat > "$TARGET/meta.json" <<JSON
{
  "category": "General",
  "changelog": "Local development build.",
  "description": "A permanent record of everything each person watches and listens to on this server.",
  "guid": "224d7c95-988f-4534-965f-69a4f6e3d802",
  "name": "JellyMold Companion",
  "overview": "Watch history for JellyMold and any other app.",
  "owner": "darkejon",
  "targetAbi": "12.1.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "12.$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
JSON
echo "Installed into $TARGET"

[[ "${1:-}" == "--no-restart" ]] && exit 0

# A full quit and reopen of Jellyfin.app. Jellyfin's own restart (POST /System/Restart) happens inside the same
# process and keeps the plugin code it already loaded, so a new build would not take effect.
osascript -e 'tell application "Jellyfin" to quit' >/dev/null
for _ in $(seq 1 30); do
  pgrep -f 'jellyfin --webdir' >/dev/null || break
  sleep 1
done
open -a Jellyfin
echo "Reopening Jellyfin…"

# Up when the plugin's own route answers 401 (it exists, and wants a sign-in) — not 503 while the server loads.
for _ in $(seq 1 90); do
  if [[ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' "$SERVER/JellyMold/v1/Info")" == "401" ]] &&
     [[ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' "$SERVER/System/Info/Public")" == "200" ]]; then
    echo "Server is back with the plugin loaded."
    exit 0
  fi
  sleep 2
done
echo "The server did not come back within three minutes." >&2
exit 1
