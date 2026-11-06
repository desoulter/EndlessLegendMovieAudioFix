# Changelog

## 1.0.0 — 2026-11-06

First public release for Endless Legend with ELCP 2.7.9 on macOS.

- Restore audio for startup logos, the main intro and faction cutscenes while
  retaining the game's video playback.
- Follow volume and mute changes during playback, including volume assigned
  after a faction cutscene starts.
- Use the game's FMOD system when available and a temporary system for startup
  movies; release audio resources when playback ends or is skipped.
- Provide automatic and manual installation and removal, original-file backups,
  installation checksums and rollback on installation failure.
- Extract audio locally from the user's game files. Distribute no game binaries,
  game audio, Unity libraries or FMOD libraries.
