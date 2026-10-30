#!/usr/bin/env bash
# A small library for the test servers, generated rather than copied: two films, a series of three episodes, an
# album of three songs and two audiobooks (one file, and one split into chapter files). Test patterns and tones,
# a few megabytes in all. Written to test-media/ (gitignored).
set -euo pipefail
cd "$(dirname "$0")/.."

FFMPEG="$(command -v ffmpeg || echo /Applications/Jellyfin.app/Contents/MacOS/ffmpeg)"
OUT="test-media"

video() { # path seconds title
  mkdir -p "$(dirname "$1")"
  [[ -f "$1" ]] && return
  "$FFMPEG" -loglevel error -f lavfi -i "testsrc=size=320x180:rate=5:duration=$2" -f lavfi -i "sine=frequency=330:duration=$2" \
    -c:v libx264 -preset ultrafast -crf 40 -c:a aac -b:a 32k -metadata title="$3" -shortest "$1"
}

audio() { # path seconds title artist album track
  mkdir -p "$(dirname "$1")"
  [[ -f "$1" ]] && return
  "$FFMPEG" -loglevel error -f lavfi -i "sine=frequency=440:duration=$2" -c:a libmp3lame -b:a 32k \
    -metadata title="$3" -metadata artist="$4" -metadata album_artist="$4" -metadata album="$5" -metadata track="$6" "$1"
}

video "$OUT/Movies/Test Film (2020)/Test Film (2020).mkv" 300 "Test Film"
video "$OUT/Movies/Another Film (2021)/Another Film (2021).mkv" 240 "Another Film"
for e in 1 2 3; do
  video "$OUT/Shows/Test Show/Season 01/Test Show S01E0$e.mkv" 180 "Episode $e"
done
audio "$OUT/Music/Test Artist/Test Album/01 - First Song.mp3" 200 "First Song" "Test Artist" "Test Album" 1
audio "$OUT/Music/Test Artist/Test Album/02 - Second Song.mp3" 150 "Second Song" "Test Artist" "Test Album" 2
audio "$OUT/Music/Test Artist/Test Album/03 - Long Song.mp3" 400 "Long Song" "Test Artist" "Test Album" 3
audio "$OUT/Audiobooks/Test Author/One File Book/One File Book.mp3" 600 "One File Book" "Test Author" "One File Book" 1
for c in 1 2 3; do
  audio "$OUT/Audiobooks/Test Author/Chapter Book/Chapter $c.mp3" 240 "Chapter $c" "Test Author" "Chapter Book" "$c"
done

du -sh "$OUT"
