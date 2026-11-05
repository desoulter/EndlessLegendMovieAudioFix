using System;
using System.Runtime.InteropServices;

// Minimal game/Unity stand-ins for exercising the bridge outside Unity.
namespace UnityEngine
{
    public class Object
    {
        public static object Manager;
        public static object FindObjectOfType(Type type) { return Manager; }
    }

    public static class Debug
    {
        public static void Log(object message) { Console.Error.WriteLine(message); }
    }

    public class MovieTexture
    {
        public bool isPlaying { get; set; }
    }

    public class AudioSource
    {
        public float volume { get; set; }
        public bool mute { get; set; }
        public int Starts;
        public void Play() { Starts++; }
    }
}

namespace FMOD
{
    public class System
    {
        public IntPtr getRaw() { return new IntPtr(0x1234); }
    }
}

namespace Amplitude.Unity.Audio
{
    public class AudioManager
    {
        private readonly FMOD.System fmodSystem = new FMOD.System();
        private bool fmodSystemInitialized = true;
        public void Unready() { fmodSystemInitialized = false; }
    }
}

public class MovieOwner
{
    private readonly UnityEngine.MovieTexture movieTexture;
    public MovieOwner(UnityEngine.MovieTexture texture) { movieTexture = texture; }
}

public class DerivedOwner : MovieOwner
{
    public DerivedOwner(UnityEngine.MovieTexture texture) : base(texture) { }
}

class TestPlayback
{
    // The test runner substitutes the absolute path to the fake native library.
    private const string NativeLibrary = "el_movie_fmod";

    [DllImport(NativeLibrary)] private static extern int test_stops();
    [DllImport(NativeLibrary)] private static extern int test_releases();
    [DllImport(NativeLibrary)] private static extern int test_systems();
    [DllImport(NativeLibrary)] private static extern int test_system_releases();
    [DllImport(NativeLibrary)] private static extern float test_volume();
    [DllImport(NativeLibrary)] private static extern void test_fail(int value);
    [DllImport(NativeLibrary)] private static extern void test_finished(int value);

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
    }

    private static void Main(string[] args)
    {
        var manager = new Amplitude.Unity.Audio.AudioManager();
        UnityEngine.Object.Manager = manager;
        var texture = new UnityEngine.MovieTexture { isPlaying = true };
        var owner = new DerivedOwner(texture);
        var source = new UnityEngine.AudioSource { volume = 0.25f };

        ELMovieAudioFix.Play(source, args[0], owner);
        Check(source.Starts == 0 && Math.Abs(test_volume() - 0.25) < 0.001 &&
              test_systems() == 0, "existing game system and volume");
        ELMovieAudioFix.Tick(owner);
        Check(test_stops() == 0, "playing video must keep sound");

        ELMovieAudioFix.Stop(owner);
        Check(test_stops() == 1 && test_releases() == 1 &&
              test_system_releases() == 0, "skip cleanup without releasing the shared system");
        ELMovieAudioFix.Stop(owner);
        Check(test_stops() == 1, "idempotent cleanup");

        ELMovieAudioFix.Play(source, args[0], owner);
        ELMovieAudioFix.Play(source, args[0], owner);
        Check(test_stops() == 2 && test_releases() == 2, "replay cleanup");
        texture.isPlaying = false;
        ELMovieAudioFix.Tick(owner);
        Check(test_stops() == 3, "video end cleanup through inherited private field");

        ELMovieAudioFix.Play(source, "/no/such/clip.ogv", owner);
        Check(source.Starts == 1, "missing sidecar fallback");
        test_fail(1);
        ELMovieAudioFix.Play(source, args[0], owner);
        Check(source.Starts == 2, "FMOD failure fallback");
        test_fail(0);

        texture.isPlaying = true;
        source.mute = true;
        ELMovieAudioFix.Play(source, args[0], owner);
        Check(test_volume() == 0, "mute");
        test_finished(1);
        ELMovieAudioFix.Tick(owner);
        Check(test_stops() == 4, "audio EOF cleanup");
        test_finished(0);

        manager.Unready();
        source.mute = false;
        ELMovieAudioFix.Play(source, args[0], owner);
        Check(test_systems() == 1, "uninitialized game system uses standalone output");
        ELMovieAudioFix.Stop(owner);
        Check(test_system_releases() == 1, "standalone system released");

        UnityEngine.Object.Manager = null;
        ELMovieAudioFix.Play(source, args[0], owner);
        ELMovieAudioFix.Stop(owner);
        Check(test_systems() == 2 && test_system_releases() == 2,
              "startup without a game audio manager");
        Console.WriteLine("PASS: native binding, volume, mute, fallback, replay, skip, EOF, startup and cleanup");
    }
}
