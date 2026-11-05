#!/bin/bash
# Prepare WAV files for a manual installation; never write to the game directory.
set -euo pipefail

game_dir="$(cd "${1:?Usage: bash tools/extract_audio.sh GAME PREPARED}" && pwd -P)"
prepared_dir="$(cd "${2:?Prepared directory is required}" && pwd -P)"
movies_dir="$game_dir/EndlessLegend.app/Contents/Resources/Data/StreamingAssets/Movies"

test -d "$prepared_dir/files" || { echo "Run MovieAudioPatcher.exe prepare first." >&2; exit 1; }
command -v ffmpeg >/dev/null
command -v ffprobe >/dev/null

find "$movies_dir" -type f -name '*.bik' -print0 |
while IFS= read -r -d '' movie; do
    streams="$(ffprobe -v error -select_streams a:0 -show_entries stream=index -of csv=p=0 "$movie")"
    if [[ -z "$streams" ]]; then
        continue
    fi
    if [[ -e "$movie.audio.wav" ]]; then
        echo "An audio sidecar already exists: $movie.audio.wav" >&2
        exit 1
    fi
    relative="${movie#"$game_dir/"}"
    destination="$prepared_dir/files/$relative.audio.wav"
    mkdir -p "$(dirname "$destination")"
    echo "Extracting $(basename "$movie")"
    ffmpeg -nostdin -hide_banner -loglevel error -n -i "$movie" \
        -map 0:a:0 -vn -c:a pcm_s16le "$destination"
done
