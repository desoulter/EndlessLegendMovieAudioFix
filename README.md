# Endless Legend Movie Audio Fix

**Version 1.0.0** · [Changelog](CHANGELOG.md) · [MIT license](LICENSE)

Restores missing movie audio in the **macOS version of Endless Legend with
ELCP 2.7.9**: startup logos, the main intro and faction cutscenes.

The fix keeps Unity's video playback and plays locally extracted audio through
the game's FMOD library. It follows volume and mute changes, including the late
volume assignment that otherwise leaves faction intros silent.

The fix requires the original ELCP 2.7.9 `Assembly-CSharp-firstpass.dll`, without additional changes to that assembly. Other mods that replace the same file may conflict.

## Requirements

- An installed copy of Endless Legend with **ELCP 2.7.9 for Mac**.
- **Python 3.8+** for the automatic installer and uninstaller.
- **Mono** to run the patching tool during installation.
- **FFmpeg**, including `ffmpeg` and `ffprobe`, to extract movie audio.
- Approximately **150 MiB** for extracted audio, plus temporary installation space.
- **Mono's** `mcs` **compiler** to build the patching tools.

For [Homebrew](https://brew.sh/) users:

```bash
brew install python mono ffmpeg
```

Audio is extracted from your own game installation. This project distributes no
game binaries, game audio, Unity libraries or FMOD libraries.

## Install

1. Close Endless Legend.
2. Clone this repository or download its source code from GitHub.
3. Open Terminal in the repository folder, build the tools and install:
   ```bash
    bash build.sh "$HOME/Library/Application Support/Steam/steamapps/common/Endless Legend"
    python3 install.py install
   ```

Launch the game normally through Steam with empty launch options. Check the
installation with:

```bash
python3 install.py status
```

The default game directory is
`~/Library/Application Support/Steam/steamapps/common/Endless Legend`.
For another Steam library, supply the directory containing `EndlessLegend.app`
and `libfmodex.dylib`:

```bash
bash build.sh "/path/to/Endless Legend"
python3 install.py install --game "/path/to/Endless Legend"
python3 install.py status --game "/path/to/Endless Legend"
```

Use the same `--game` argument for removal and other commands.

The installer prepares and checks files before replacing the game assembly,
saves its original copy in `MovieAudioFix/backup`, and records installed-file
checksums. Keep that backup until the fix has been removed. Running `install`
again checks an existing installation.

## Uninstall

Close the game and run:

```bash
python3 install.py uninstall
```

If you no longer have the downloaded package, use the copy saved with the game:

```bash
python3 "$HOME/Library/Application Support/Steam/steamapps/common/Endless Legend/MovieAudioFix/install.py" uninstall
```

Removal requires only Python. It restores the original ELCP assembly and removes
the helper and generated WAV files. Files changed since installation are reported
instead of overwritten.

Uninstall before updating ELCP, verifying Steam files or moving the game to a
different directory. Reinstall afterwards if the game assembly is still supported.
The helper is bound to the absolute path of the game's FMOD library, so moving an
installed game requires reinstallation.

## Build details

`build.sh` reads the game's `mscorlib.dll` to target its .NET 2.0 runtime.
Building prepares the tools without modifying the game.

The build downloads **Mono.Cecil 0.11.6** from NuGet, extracts
`lib/net40/Mono.Cecil.dll` and checks its pinned SHA-256. A matching cached DLL is
reused, allowing subsequent builds offline. Installation itself downloads nothing.

Generated `tools/*.dll`, `tools/*.exe` and `SHA256SUMS` are ignored by Git.
Mono.Cecil is used only by the patching tool; it is not an in-game dependency.

## Manual installation

This method does not require Python. Build the tools with `bash build.sh GAME`
first, then close the game and open Terminal in the repository folder.

```bash
GAME="$HOME/Library/Application Support/Steam/steamapps/common/Endless Legend"
GAME="$(cd "$GAME" && pwd -P)"
WORK="$PWD/prepared"

mono tools/MovieAudioPatcher.exe prepare "$GAME" "$WORK"
bash tools/extract_audio.sh "$GAME" "$WORK"
mono tools/MovieAudioPatcher.exe manifest "$GAME" "$WORK"
```

Adjust `GAME` if needed. Run each command only after the previous command succeeds.
`WORK` must be a new directory. These commands prepare files without modifying
the game.

Copy the backup and installation record first, then merge the prepared files:

```bash
ditto "$WORK/MovieAudioFix" "$GAME/MovieAudioFix"
ditto "$WORK/files" "$GAME"
```

The commands merge individual files. Do not replace the whole app bundle in
Finder. The `prepared` directory contains files derived from your game; distribute
the original release package instead.

### Manual removal

Close the game. Paths below are relative to the game directory.

1. Copy `MovieAudioFix/backup/Assembly-CSharp-firstpass.dll` to

   `EndlessLegend.app/Contents/Resources/Data/Managed/Assembly-CSharp-firstpass.dll`,
    replacing the patched file.
2. Delete `EndlessLegend.app/Contents/Resources/Data/Managed/ELMovieAudioFix.dll`.
3. Delete the `.bik.audio.wav` files listed in `MovieAudioFix/manifest.json`.

   Keep the original `.bik` and `.ogv` files.
4. Delete `MovieAudioFix` after restoring the original assembly.

The automatic uninstaller also works after a manual installation.

## Troubleshooting

| Message or symptom | Action |
| --- | --- |
| `Unsupported game assembly` | Install ELCP 2.7.9 for Mac and remove changes to its firstpass assembly. Other builds are rejected deliberately. |
| `Build tools are missing` | Run `bash build.sh GAME` in your source checkout. |
| `Required tool not found` | Install the named dependency and retry. |
| `Missing or changed installed file` | Check for a game update, Steam verification or another mod replacing installed files. Use the removal instructions before reinstalling. |
| `Game location changed` | Uninstall and reinstall at the new location. |
| Movies are still silent | Check the installation status and the Unity log below. |

```bash
python3 install.py --version
python3 install.py status
grep 'EL Movie Audio Fix' "$HOME/Library/Logs/Unity/Player.log"
```

The log should contain `FMOD bridge v1.0.0 loaded`, then
`playing ... via game FMOD` or `playing ... via standalone FMOD`.
An initial `volume=0` can be normal: `volume updated ...` records a later volume
change from the game.

For a bug report, include your macOS version, Mac processor, ELCP version,
installer output and relevant log lines. Mention whether the problem affects
startup logos, the main intro or a particular faction cutscene.

## How it works

The Mac game displays `.ogv` movies using Unity `MovieTexture`. Its Mac cutscene
panel disables the Bink component, so replacing the Bink playback library does
not repair this playback path.

The installer extracts the first audio stream from each voiced `.bik` movie to
a signed 16-bit PCM WAV next to the original movie. It patches three audio-start
calls in `Assembly-CSharp-firstpass.dll` to use `ELMovieAudioFix.dll`.

Unity continues playing the video. The helper locates the matching WAV and uses
the game's FMOD system for audio. Before the audio manager is ready, it creates a
temporary FMOD system for startup movies. It follows the Unity AudioSource's
volume and mute settings and releases the sound when the movie ends, is skipped
or is unloaded. If audio startup fails, it falls back to Unity's original audio
call.

Native imports are bound directly to the installed game's `libfmodex.dylib`.
No `dllmap`, native-library replacement or launch wrapper is needed.

## Development

The integration suite uses temporary game copies and synthetic video/audio
fixtures. Supply a directory containing the original ELCP 2.7.9 Mac
`Assembly-CSharp-firstpass.dll`, `UnityEngine.dll` and `mscorlib.dll`:

```bash
bash build.sh "/path/to/Endless Legend"
EL_MOVIE_TEST_MANAGED="/path/to/original/Managed" python3 -m unittest discover -s tests -v
```

The suite checks installation, removal, rollback, installation conflict handling,
native binding, late volume changes, mute and resource cleanup. It uses a fake
FMOD library for playback checks; listening and video/audio timing must be
checked in the game.

Release notes are in [CHANGELOG.md](CHANGELOG.md).

## License and credits

Project code is licensed under the [MIT license](LICENSE).
[Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) is also
MIT-licensed; its notice is included in [licenses/Mono.Cecil.txt](licenses/Mono.Cecil.txt).
FFmpeg and Mono are separate prerequisites and are not bundled.

This is an unofficial community fix, not affiliated with or endorsed by
Amplitude Studios or SEGA. Endless Legend and its game assets belong to their
respective owners.
