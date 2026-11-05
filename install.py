#!/usr/bin/env python3
"""Install, inspect or remove the Endless Legend movie audio fix."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile


PACKAGE = Path(__file__).resolve().parent
DEFAULT_GAME = Path.home() / "Library/Application Support/Steam/steamapps/common/Endless Legend"
MANAGED = "EndlessLegend.app/Contents/Resources/Data/Managed"
MOVIES = "EndlessLegend.app/Contents/Resources/Data/StreamingAssets/Movies"
ASSEMBLY = MANAGED + "/Assembly-CSharp-firstpass.dll"
HELPER = MANAGED + "/ELMovieAudioFix.dll"
STATE = "MovieAudioFix"
ORIGINAL_SHA256 = "ed19f8e0c6f5b7e0786f240af4b322b85498a5db112e99a8b31ac108bacef397"


class PatchError(Exception):
    pass


def checksum(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run(arguments):
    result = subprocess.run(
        [str(value) for value in arguments], stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    if result.returncode:
        raise PatchError(result.stderr.strip() or result.stdout.strip() or
                         "Command failed: " + str(arguments[0]))
    return result.stdout


def tool(name):
    found = shutil.which(name)
    if found:
        return Path(found)
    for directory in ["/opt/homebrew/bin", "/usr/local/bin",
                      "/Library/Frameworks/Mono.framework/Versions/Current/Commands"]:
        candidate = Path(directory) / name
        if candidate.is_file():
            return candidate
    raise PatchError("Required tool not found: " + name + ". See README.md.")


def require_closed_game():
    result = subprocess.run(["pgrep", "-x", "EndlessLegend"], capture_output=True)
    if result.returncode == 0:
        raise PatchError("Close Endless Legend first.")
    if result.returncode != 1:
        raise PatchError("Could not check whether Endless Legend is running.")


def atomic_copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=destination.name + ".", dir=str(destination.parent))
    os.close(descriptor)
    pending = Path(name)
    try:
        shutil.copy2(source, pending)
        os.replace(pending, destination)
    finally:
        if pending.exists():
            pending.unlink()


def game_path(game, relative):
    path = PurePosixPath(relative)
    allowed = relative in (ASSEMBLY, HELPER) or (
        relative.startswith(MOVIES + "/") and relative.endswith(".bik.audio.wav")
    )
    if not allowed or path.is_absolute() or ".." in path.parts or "\\" in relative:
        raise PatchError("Invalid installed-file path: " + relative)
    destination = game / relative
    if game not in destination.resolve().parents:
        raise PatchError("Installed-file path leaves the game directory: " + relative)
    return destination


def read_manifest(game):
    manifest = game / STATE / "manifest.json"
    if not manifest.is_file():
        raise PatchError("No community patch installation found. See README.md for manual removal.")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if data.get("format") != 1 or data.get("original_sha256") != ORIGINAL_SHA256:
        raise PatchError("Unsupported installation manifest.")
    paths = set()
    for entry in data["files"]:
        relative = entry["path"]
        game_path(game, relative)
        if relative in paths or len(entry["sha256"]) != 64:
            raise PatchError("Invalid or duplicate manifest entry: " + relative)
        paths.add(relative)
    if not {ASSEMBLY, HELPER}.issubset(paths):
        raise PatchError("Incomplete installation manifest.")
    return data


def original_backup(game):
    backup = game / STATE / "backup/Assembly-CSharp-firstpass.dll"
    if not backup.is_file() or checksum(backup) != ORIGINAL_SHA256:
        raise PatchError("Original assembly backup is missing or has changed.")
    return backup


def status(game):
    if not (game / STATE / "manifest.json").exists():
        print("Not installed.")
        return
    data = read_manifest(game)
    original_backup(game)
    for entry in data["files"]:
        path = game_path(game, entry["path"])
        if not path.is_file() or checksum(path) != entry["sha256"]:
            raise PatchError("Missing or changed installed file: " + str(path))
    library = game / "libfmodex.dylib"
    if str(library) != data["native_library"] or not library.is_file():
        raise PatchError("Game location changed. Uninstall and reinstall to update the FMOD path.")
    tracks = sum(entry["path"].endswith(".audio.wav") for entry in data["files"])
    print("Installed " + data["version"] + "; checksums match. Audio tracks: " + str(tracks))


def extract_audio(game, output):
    ffmpeg, ffprobe = tool("ffmpeg"), tool("ffprobe")
    movies = sorted((game / MOVIES).rglob("*.bik"))
    if not movies:
        raise PatchError("No Bink movies found in " + str(game / MOVIES))
    count = 0
    for movie in movies:
        streams = run([ffprobe, "-v", "error", "-select_streams", "a:0",
                       "-show_entries", "stream=index", "-of", "csv=p=0", movie])
        if not streams.strip():
            continue
        relative = movie.relative_to(game).as_posix() + ".audio.wav"
        if game_path(game, relative).exists():
            raise PatchError("An audio sidecar already exists: " + relative)
        destination = output / "files" / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        print("Extracting " + movie.name, flush=True)
        run([ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-n",
             "-i", movie, "-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", destination])
        count += 1
    if not count:
        raise PatchError("No movie audio streams found.")


def install(game):
    require_closed_game()
    if (game / STATE / "manifest.json").is_file():
        status(game)
        return
    if (game / STATE).exists():
        raise PatchError("MovieAudioFix already exists. Remove any earlier patch before installing.")
    if not (game / ASSEMBLY).is_file() or checksum(game / ASSEMBLY) != ORIGINAL_SHA256:
        raise PatchError("Unsupported game assembly. This release requires ELCP 2.7.9 for macOS.")
    if (game / HELPER).exists() or (game / (HELPER + ".config")).exists():
        raise PatchError("An earlier audio helper exists. Remove it before installing.")
    mono = tool("mono")
    patcher = PACKAGE / "tools/MovieAudioPatcher.exe"
    for name in ["MovieAudioPatcher.exe", "ELMovieAudioFix.template.dll", "Mono.Cecil.dll"]:
        if not (PACKAGE / "tools" / name).is_file():
            raise PatchError("Build tools are missing. Run bash build.sh with your game directory first.")
    # All preparation happens outside live game files, including audio decoding.
    with tempfile.TemporaryDirectory(prefix="el-movie-audio-", dir=str(game)) as temporary:
        prepared = Path(temporary) / "prepared"
        print(run([mono, patcher, "prepare", game, prepared]).strip())
        extract_audio(game, prepared)
        print(run([mono, patcher, "manifest", game, prepared]).strip())
        if (game / STATE).exists():
            raise PatchError("Installation directory appeared during preparation: " + str(game / STATE))
        os.replace(prepared / STATE, game / STATE)
        installed = []
        try:
            data = read_manifest(game)
            # Install the assembly last: until then, the game uses its original code.
            for entry in sorted(data["files"], key=lambda value: value["path"] == ASSEMBLY):
                relative = entry["path"]
                destination = game_path(game, relative)
                if relative != ASSEMBLY and destination.exists():
                    raise PatchError("File appeared during installation: " + relative)
                if relative == ASSEMBLY and checksum(destination) != ORIGINAL_SHA256:
                    raise PatchError("Game assembly changed during installation.")
                atomic_copy(prepared / "files" / relative, destination)
                installed.append(entry)
        except Exception:
            # Roll back only successful writes, including a partially installed payload.
            for entry in reversed(installed):
                path = game_path(game, entry["path"])
                if not path.is_file() or checksum(path) != entry["sha256"]:
                    raise PatchError("Rollback stopped at a changed file; backup preserved: " + str(path))
                if entry["path"] == ASSEMBLY:
                    atomic_copy(original_backup(game), path)
                else:
                    path.unlink()
            shutil.rmtree(game / STATE)
            raise
    status(game)
    print("Launch normally through Steam; leave Steam launch options empty.")


def uninstall(game):
    require_closed_game()
    if not (game / STATE).exists():
        print("Not installed.")
        return
    data = read_manifest(game)
    backup = original_backup(game)
    # Check everything before removing anything; never overwrite another mod.
    for entry in data["files"]:
        path = game_path(game, entry["path"])
        accepted = [entry["sha256"]]
        if entry["path"] == ASSEMBLY:
            accepted.append(ORIGINAL_SHA256)
        if path.exists() and (not path.is_file() or checksum(path) not in accepted):
            raise PatchError("File changed after installation; remove manually: " + str(path))
    atomic_copy(backup, game / ASSEMBLY)
    for entry in data["files"]:
        if entry["path"] != ASSEMBLY:
            path = game_path(game, entry["path"])
            if path.exists():
                path.unlink()
    for relative in ["backup/Assembly-CSharp-firstpass.dll", "install.py", "manifest.json"]:
        path = game / STATE / relative
        if path.exists():
            path.unlink()
    for directory in [game / STATE / "backup", game / STATE]:
        if directory.exists() and not any(directory.iterdir()):
            directory.rmdir()
    print("Uninstalled. Original ELCP assembly restored; generated audio files removed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["install", "status", "uninstall"])
    parser.add_argument("--game", type=Path, default=DEFAULT_GAME,
                        help="Endless Legend installation directory")
    arguments = parser.parse_args()
    game = arguments.game.expanduser().resolve()
    try:
        {"install": install, "status": status, "uninstall": uninstall}[arguments.action](game)
        return 0
    except (PatchError, OSError, ValueError, KeyError, TypeError) as error:
        print("Error: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
