#!/usr/bin/env bash
# A Jellyfin 10.10.7 server in Docker (OrbStack) — what JRDisk runs — for testing the 10.10 build without going
# near JRDisk. Listens on 127.0.0.1:8097 only. Its config lives in .docker/ (gitignored).
#
#   tools/docker-10.10.sh up      create and start it, with test-media/ as its library folder
#   tools/docker-10.10.sh load    build the 10.10 plugin, install it, restart the container
#   tools/docker-10.10.sh down    remove the container (its config folder stays)
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

NAME="jmc-jellyfin-10.10"
PORT=8097
DIR="$PWD/.docker/jellyfin-10.10"
SERVER="http://localhost:$PORT"
VERSION="$(sed -n 's:.*<ReleaseVersion>\(.*\)</ReleaseVersion>.*:\1:p' Directory.Build.props)"

wait_for() { # the status code to wait for on the plugin's route
  for _ in $(seq 1 90); do
    if [[ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' "$SERVER/System/Info/Public")" == "200" ]] &&
       [[ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' "$SERVER/JellyMold/v1/Info")" == "$1" ]]; then
      return 0
    fi
    sleep 2
  done
  echo "Jellyfin 10.10.7 did not come up on $SERVER." >&2
  exit 1
}

case "${1:-}" in
  up)
    [[ -d test-media ]] || tools/make-test-media.sh
    mkdir -p "$DIR/config" "$DIR/cache"
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" \
      -v "$DIR/config:/config" -v "$DIR/cache:/cache" -v "$PWD/test-media:/media:ro" \
      jellyfin/jellyfin:10.10.7 >/dev/null
    for _ in $(seq 1 90); do
      [[ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' "$SERVER/System/Info/Public")" == "200" ]] && break
      sleep 2
    done
    echo "Jellyfin 10.10.7 is up on $SERVER"
    ;;
  load)
    dotnet build src/Jellyfin.Plugin.JellyMoldCompanion -c Release -f net8.0 --nologo -v quiet | grep -E "error|warn" && exit 1 || true
    OUT="src/Jellyfin.Plugin.JellyMoldCompanion/bin/Release/net8.0"
    find "$DIR/config/plugins" -maxdepth 1 -type d -name 'JellyMold Companion_*' -print0 2>/dev/null | xargs -0 rm -rf
    TARGET="$DIR/config/plugins/JellyMold Companion_10.$VERSION"
    mkdir -p "$TARGET"
    cp "$OUT/Jellyfin.Plugin.JellyMoldCompanion.dll" "$OUT/JellyMoldCompanion.Core.dll" "$TARGET/"
    tools/write-meta.sh "$TARGET" "10.$VERSION" "10.10.7.0"
    docker restart "$NAME" >/dev/null
    wait_for 401
    echo "Installed 10.$VERSION into Jellyfin 10.10.7 on $SERVER"
    ;;
  down)
    docker rm -f "$NAME" >/dev/null
    echo "Removed $NAME (its config stays in $DIR)"
    ;;
  *)
    sed -n '2,9p' "$0"
    exit 2
    ;;
esac
