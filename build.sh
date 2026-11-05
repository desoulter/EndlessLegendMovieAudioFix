#!/bin/bash
# Rebuild the two project binaries against the game's .NET 2.0 core library.
set -euo pipefail

game_dir="$(cd "${1:?Usage: bash build.sh GAME}" && pwd -P)"
cd "$(dirname "$0")"
core_library="$game_dir/EndlessLegend.app/Contents/Resources/Data/Managed/mscorlib.dll"
test -f "$core_library" || { echo "Game mscorlib.dll not found." >&2; exit 1; }
mcs_path="$(command -v mcs || true)"
if [[ -z "$mcs_path" ]]; then
    mcs_path=/Library/Frameworks/Mono.framework/Versions/Current/Commands/mcs
fi
test -x "$mcs_path" || { echo "Mono's mcs compiler was not found." >&2; exit 1; }
build_dir="$(mktemp -d)"
trap 'rm -rf "$build_dir"' EXIT

# Use the .NET Framework build of Cecil; the patcher runs under desktop Mono.
cecil_version=0.11.6
cecil_sha256=c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b
mkdir -p tools
if [[ ! -f tools/Mono.Cecil.dll ]]; then
    echo "Downloading Mono.Cecil $cecil_version from NuGet..."
    curl --fail --location --silent --show-error --connect-timeout 15 --max-time 60 \
        "https://api.nuget.org/v3-flatcontainer/mono.cecil/$cecil_version/mono.cecil.$cecil_version.nupkg" \
        -o "$build_dir/cecil.nupkg"
    unzip -p "$build_dir/cecil.nupkg" lib/net40/Mono.Cecil.dll > "$build_dir/Mono.Cecil.dll"
    downloaded_sha256="$(shasum -a 256 "$build_dir/Mono.Cecil.dll")"
    if [[ "${downloaded_sha256%% *}" != "$cecil_sha256" ]]; then
        echo "Downloaded Mono.Cecil checksum mismatch." >&2
        exit 1
    fi
    cp "$build_dir/Mono.Cecil.dll" tools/Mono.Cecil.dll
fi
existing_sha256="$(shasum -a 256 tools/Mono.Cecil.dll)"
if [[ "${existing_sha256%% *}" != "$cecil_sha256" ]]; then
    echo "Existing tools/Mono.Cecil.dll does not match the pinned NuGet build." >&2
    exit 1
fi

"$mcs_path" -noconfig -nostdlib -target:library \
    -r:"$core_library" \
    -out:"$build_dir/ELMovieAudioFix.dll" src/ELMovieAudioFix.cs
"$mcs_path" -r:tools/Mono.Cecil.dll -out:"$build_dir/MovieAudioPatcher.exe" src/MovieAudioPatcher.cs
cp "$build_dir/ELMovieAudioFix.dll" tools/ELMovieAudioFix.template.dll
cp "$build_dir/MovieAudioPatcher.exe" tools/MovieAudioPatcher.exe
shasum -a 256 tools/ELMovieAudioFix.template.dll tools/MovieAudioPatcher.exe tools/Mono.Cecil.dll > SHA256SUMS
echo "Build complete."
