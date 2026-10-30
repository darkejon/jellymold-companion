#!/usr/bin/env bash
# Writes the meta.json Jellyfin reads from a plugin's folder.
#   tools/write-meta.sh <folder> <version> <targetAbi> [changelog]
set -euo pipefail
folder="$1" version="$2" abi="$3" changelog="${4:-Local development build.}"
cat > "$folder/meta.json" <<JSON
{
  "category": "General",
  "changelog": "$changelog",
  "description": "A permanent record of everything each person watches and listens to on this server, readable by each person from an app. Survives media being deleted; never deletes history.",
  "guid": "224d7c95-988f-4534-965f-69a4f6e3d802",
  "name": "JellyMold Companion",
  "overview": "Watch history for JellyMold and any other app.",
  "owner": "darkejon",
  "targetAbi": "$abi",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$version",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": ["Jellyfin.Plugin.JellyMoldCompanion.dll", "JellyMoldCompanion.Core.dll"]
}
JSON
