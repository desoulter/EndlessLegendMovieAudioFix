# Endless Legend Movie Audio Fix 2.1.0

Restores movie audio in the native macOS version of Endless Legend with
**ELCP 2.7.9 for Mac**. The installer accepts only the matching original
`Assembly-CSharp-firstpass.dll`; other game/ELCP builds are unsupported.

The 2.1 playback mechanism was confirmed working on an Apple M4 Pro with macOS:
startup logos use standalone FMOD, and the menu trailer uses the game's FMOD.
Installer and removal tests ran under Mono on Linux using the original Mac assembly.

## Requirements

- An unmodified ELCP 2.7.9 Mac installation. Uninstall earlier movie audio fixes first.
- Python 3.8+ for automatic installation/removal.
- Mono (including `mcs`) and FFmpeg (`ffmpeg` and `ffprobe`) for building/installing.
- Approximately 150 MiB for extracted audio, plus temporary installation space.

If an older uninstaller leaves a `MovieAudioFix` directory, move it out of the
game directory after uninstalling the old patch.

If using [Homebrew](https://brew.sh/):

```bash
brew install python mono ffmpeg
```

This source package contains no compiled binaries. Build the tools before
installing. No game binaries, audio, FMOD libraries or Unity libraries are distributed.

## Build

Open Terminal in the cloned/extracted repository and run:

```bash
bash build.sh "$HOME/Library/Application Support/Steam/steamapps/common/Endless Legend"
```

Use your actual game directory if it is installed elsewhere. The script downloads
**Mono.Cecil 0.11.6** from [NuGet](https://www.nuget.org/packages/Mono.Cecil/0.11.6),
extracts `lib/net40/Mono.Cecil.dll`, checks its pinned SHA-256 and compiles the
helper and patcher. A cached matching DLL is reused; later builds work offline.
Building prepares tools only and does not modify the game. Installation downloads nothing.

Generated files in `tools/` (`*.dll`, `*.exe`) and `SHA256SUMS` are ignored by Git.
Keep `tools/extract_audio.sh` in the repository. Mono.Cecil is a build/install-time
dependency of the patcher; the in-game helper does not reference it.

## Automatic installation

Close the game. Extract this archive, open Terminal in its folder, and run:

```bash
python3 install.py install
```

For a non-default Steam library:

```bash
python3 install.py install --game "/path/to/Endless Legend"
```

Launch with Steam's normal Play button and empty launch options.

```bash
python3 install.py status
```

Use the same `--game` argument for every command if the installation is elsewhere.

## Manual installation (without Python)

Close the game. Open Terminal in this package's folder. Set `GAME` to the
directory containing `EndlessLegend.app` and `libfmodex.dylib`:

```bash
GAME="$HOME/Library/Application Support/Steam/steamapps/common/Endless Legend"
GAME="$(cd "$GAME" && pwd -P)"
WORK="$PWD/prepared"

mono tools/MovieAudioPatcher.exe prepare "$GAME" "$WORK"
bash tools/extract_audio.sh "$GAME" "$WORK"
mono tools/MovieAudioPatcher.exe manifest "$GAME" "$WORK"
```

Run each command only after the previous one succeeds. `WORK` must be a new
directory. These steps prepare files without modifying the game.

Copy the backup/manifest first, then merge the prepared files into the game:

```bash
ditto "$WORK/MovieAudioFix" "$GAME/MovieAudioFix"
ditto "$WORK/files" "$GAME"
```

Do not replace the entire app bundle in Finder. The commands merge individual
files. Keep `MovieAudioFix/backup` until the patch has been uninstalled.
The prepared files contain audio and a modified assembly from your game;
share this original package rather than the `prepared` directory.

## Automatic removal

Close the game and run either command:

```bash
python3 install.py uninstall
```

Or, if the downloaded package is no longer available:

```bash
python3 "$HOME/Library/Application Support/Steam/steamapps/common/Endless Legend/MovieAudioFix/install.py" uninstall
```

This also works after a manual installation. Removal needs only Python 3.
It restores the original assembly and removes the helper and generated WAVs.
Changed files are reported rather than overwritten. Uninstall before updating
ELCP, verifying Steam files, or moving the game; reinstall afterwards if the
game assembly is still the supported version.

## Manual removal

Close the game. All paths below are relative to the game directory.

1. Copy `MovieAudioFix/backup/Assembly-CSharp-firstpass.dll` to
   `EndlessLegend.app/Contents/Resources/Data/Managed/Assembly-CSharp-firstpass.dll`,
   replacing the patched file.
2. Delete `EndlessLegend.app/Contents/Resources/Data/Managed/ELMovieAudioFix.dll`.
3. Delete the `.bik.audio.wav` files listed in `MovieAudioFix/manifest.json`.
   Keep the original `.bik` and `.ogv` files.
4. Delete the `MovieAudioFix` directory after restoring the original assembly.

## How it works

The Mac game uses Unity `MovieTexture` to display `.ogv` movies. Its Mac cutscene
panel disables the Bink component, so replacing the Bink playback library does
not affect this path.

The installer extracts the first audio stream from each voiced `.bik` movie
into a signed 16-bit PCM WAV alongside that movie. It patches three audio-start
calls in `Assembly-CSharp-firstpass.dll` to call `ELMovieAudioFix.dll`.

Unity continues playing the video. The helper maps the movie path to its WAV
and plays it through the existing FMOD system. For early logos, before the game
audio system is initialized, it creates a temporary FMOD system. It copies the
original AudioSource's volume/mute setting and stops/releases the sound when
the movie ends, is skipped or is unloaded. Failed playback falls back to Unity.

The helper's native imports are bound to the **absolute path** of the installed
game's `libfmodex.dylib`. No `dllmap`, native-library replacement, launch wrapper
or Steam launch option is required. Moving the game requires reinstallation.

## Diagnostics

```bash
grep 'EL Movie Audio v2' "$HOME/Library/Logs/Unity/Player.log"
```

Expected: `FMOD bridge v2.1 loaded; direct native binding`, followed by
`playing ... via game FMOD` or `playing ... via standalone FMOD`.

## Source and license

Source is in `src/`. The helper targets the game's .NET 2.0 core library. The patching tool uses
[Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6).
After a build, `SHA256SUMS` contains checksums of the generated/downloaded binaries.

Project code: MIT, see `LICENSE`. Mono.Cecil: MIT, see `licenses/Mono.Cecil.txt`.
This is an unofficial patch and is not affiliated with Amplitude or SEGA.
