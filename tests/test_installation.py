"""Integration tests. Set EL_MOVIE_TEST_MANAGED to an original ELCP 2.7.9 Mac Managed directory."""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock


PACKAGE = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("installer", PACKAGE / "install.py")
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)
FIXTURE_MANAGED = os.environ.get("EL_MOVIE_TEST_MANAGED")


@unittest.skipUnless(FIXTURE_MANAGED, "Set EL_MOVIE_TEST_MANAGED to run integration tests")
class InstallationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="el-movie-test-")
        self.addCleanup(self.temporary.cleanup)
        # These tests operate on private temporary copies, never on a running game.
        closed = mock.patch.object(installer, "require_closed_game")
        closed.start()
        self.addCleanup(closed.stop)
        self.game = (Path(self.temporary.name) / 'game with spaces, "quotes" and Unicode é').resolve()
        managed = self.game / installer.MANAGED
        managed.mkdir(parents=True)
        for name in ["Assembly-CSharp-firstpass.dll", "UnityEngine.dll", "mscorlib.dll"]:
            shutil.copy2(Path(FIXTURE_MANAGED) / name, managed / name)
        (self.game / "libfmodex.dylib").write_bytes(b"Native library placeholder for installation tests")
        self.movies = self.game / installer.MOVIES
        self.movies.mkdir(parents=True)
        self.audio_movie = self.movies / 'Intro with "quotes".bik'
        self.silent_movie = self.movies / "Silent.bik"
        # The pipeline probes file contents; synthetic AVI fixtures avoid shipping game media.
        installer.run([installer.tool("ffmpeg"), "-nostdin", "-v", "error",
                       "-f", "lavfi", "-i", "color=size=16x16:rate=10",
                       "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                       "-t", "0.2", "-c:v", "rawvideo", "-c:a", "pcm_s16le",
                       "-f", "avi", self.audio_movie])
        installer.run([installer.tool("ffmpeg"), "-nostdin", "-v", "error",
                       "-f", "lavfi", "-i", "color=size=16x16:rate=10", "-t", "0.2",
                       "-c:v", "rawvideo", "-an", "-f", "avi", self.silent_movie])
        self.original_files = self.snapshot()

    def snapshot(self):
        return {p.relative_to(self.game).as_posix(): installer.checksum(p)
                for p in self.game.rglob("*") if p.is_file()}

    def install(self):
        with contextlib.redirect_stdout(io.StringIO()):
            installer.install(self.game)

    def test_install_status_repeat_and_uninstall(self):
        self.install()
        data = installer.read_manifest(self.game)
        audio = [entry for entry in data["files"] if entry["path"].endswith(".audio.wav")]
        self.assertEqual(len(audio), 1)
        self.assertEqual(data["native_library"], str(self.game / "libfmodex.dylib"))
        self.assertIn(str(self.game / "libfmodex.dylib").encode(), (self.game / installer.HELPER).read_bytes())
        self.assertNotIn(b"el_movie_fmod", (self.game / installer.HELPER).read_bytes())
        installed = self.snapshot()
        self.install()
        self.assertEqual(installed, self.snapshot())
        installer.status(self.game)
        installer.uninstall(self.game)
        self.assertEqual(self.original_files, self.snapshot())

    def test_manual_install_and_automatic_uninstall(self):
        prepared = Path(self.temporary.name) / "prepared"
        patcher = PACKAGE / "tools/MovieAudioPatcher.exe"
        installer.run([installer.tool("mono"), patcher, "prepare", self.game, prepared])
        installer.run(["bash", PACKAGE / "tools/extract_audio.sh", self.game, prepared])
        installer.run([installer.tool("mono"), patcher, "manifest", self.game, prepared])
        self.assertEqual(self.original_files, self.snapshot(), "Preparation must not change the game")
        shutil.copytree(prepared / installer.STATE, self.game / installer.STATE)
        shutil.copytree(prepared / "files", self.game, dirs_exist_ok=True)
        installer.status(self.game)
        installer.uninstall(self.game)
        self.assertEqual(self.original_files, self.snapshot())

    def test_decode_failure_does_not_change_game(self):
        original_run = installer.run

        def fail_ffmpeg(arguments):
            if Path(arguments[0]).name == "ffmpeg":
                raise installer.PatchError("Simulated decode failure")
            return original_run(arguments)

        with mock.patch.object(installer, "run", side_effect=fail_ffmpeg):
            with self.assertRaises(installer.PatchError):
                self.install()
        self.assertEqual(self.original_files, self.snapshot())

    def test_copy_failure_rolls_back(self):
        original_copy = installer.atomic_copy

        def fail_assembly(source, destination):
            if destination == self.game / installer.ASSEMBLY:
                raise OSError("Simulated assembly replacement failure")
            return original_copy(source, destination)

        with mock.patch.object(installer, "atomic_copy", side_effect=fail_assembly):
            with self.assertRaises(OSError):
                self.install()
        self.assertEqual(self.original_files, self.snapshot())

    def test_uninstall_preserves_later_modifications(self):
        self.install()
        helper = self.game / installer.HELPER
        helper.write_bytes(helper.read_bytes() + b"Modified by another tool")
        changed = self.snapshot()
        with self.assertRaises(installer.PatchError):
            installer.uninstall(self.game)
        self.assertEqual(changed, self.snapshot())

    def test_unsupported_assembly_is_rejected(self):
        assembly = self.game / installer.ASSEMBLY
        assembly.write_bytes(assembly.read_bytes() + b"Different game build")
        changed = self.snapshot()
        with self.assertRaises(installer.PatchError):
            self.install()
        self.assertEqual(changed, self.snapshot())

    def mark_previous_version(self):
        helper = self.game / installer.HELPER
        helper.write_bytes(helper.read_bytes() + b"previous build")
        data = installer.read_manifest(self.game)
        data["version"] = "2.1.0"
        for entry in data["files"]:
            if entry["path"] == installer.HELPER:
                entry["sha256"] = installer.checksum(helper)
        (self.game / installer.STATE / "manifest.json").write_text(json.dumps(data))

    def test_helper_upgrade_preserves_game_assembly_and_audio(self):
        self.install()
        self.mark_previous_version()
        before = self.snapshot()
        installer.upgrade(self.game)
        self.assertEqual(installer.read_manifest(self.game)["version"], "2.1.1")
        for relative, digest in before.items():
            if relative == installer.ASSEMBLY or relative.endswith(".audio.wav"):
                self.assertEqual(digest, installer.checksum(self.game / relative))
        upgraded = self.snapshot()
        installer.upgrade(self.game)
        self.assertEqual(upgraded, self.snapshot())
        installer.uninstall(self.game)
        self.assertEqual(self.original_files, self.snapshot())

    def test_failed_upgrade_restores_helper_and_manifest(self):
        self.install()
        self.mark_previous_version()
        before = self.snapshot()
        original_copy = installer.atomic_copy
        failed = [False]

        def fail_manifest_once(source, destination):
            if destination == self.game / installer.STATE / "manifest.json" and not failed[0]:
                failed[0] = True
                raise OSError("Simulated upgrade commit failure")
            return original_copy(source, destination)

        with mock.patch.object(installer, "atomic_copy", side_effect=fail_manifest_once):
            with self.assertRaises(OSError):
                installer.upgrade(self.game)
        self.assertEqual(before, self.snapshot())

    def test_native_playback_without_dllmap(self):
        library = self.game / "libfmodex.dylib"
        flags = ["-dynamiclib", "-arch", "arm64", "-arch", "x86_64"] if sys.platform == "darwin" else ["-shared", "-fPIC"]
        installer.run([installer.tool("cc"), *flags, PACKAGE / "tests/fake_fmod.c", "-o", library])
        prepared = Path(self.temporary.name) / "native-prepared"
        installer.run([installer.tool("mono"), PACKAGE / "tools/MovieAudioPatcher.exe", "prepare", self.game, prepared])
        run = Path(self.temporary.name) / "native-run"
        run.mkdir()
        shutil.copy2(prepared / "files" / installer.HELPER, run / "ELMovieAudioFix.dll")
        source = (PACKAGE / "tests/TestPlayback.cs").read_text()
        source = source.replace('"el_movie_fmod"', '@"' + str(library).replace('"', '""') + '"')
        (run / "TestPlayback.cs").write_text(source)
        installer.run([installer.tool("mcs"), "-nowarn:0414", "-r:" + str(run / "ELMovieAudioFix.dll"),
                       "-out:" + str(run / "TestPlayback.exe"), run / "TestPlayback.cs"])
        self.assertFalse(list(run.glob("*.config")))
        # The fake native decoder checks existence, not WAV data or an audio device.
        Path(str(self.audio_movie) + ".audio.wav").write_bytes(b"test audio fixture")
        output = installer.run([installer.tool("mono"), run / "TestPlayback.exe",
                                str(self.audio_movie.with_suffix(".ogv"))])
        self.assertIn("PASS:", output)


if __name__ == "__main__":
    unittest.main()
