using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

// Built against the game's own mscorlib 2.0. No Unity or game assembly dependency.
public static class ELMovieAudioFix
{
    private sealed class Playback
    {
        internal IntPtr System, Sound, Channel;
        internal string Path;
        internal bool OwnSystem;
        internal object AudioSource;
        internal float Volume;
    }

    private static readonly Dictionary<object, Playback> Playing = new Dictionary<object, Playback>();
    private static readonly Dictionary<string, bool> Warned = new Dictionary<string, bool>();

    // Package preparation binds every native import to the absolute path of the
    // game's FMOD library. Unity's embedded Mono may ignore dllmap files.
    private const string NativeLibrary = "el_movie_fmod";

    static ELMovieAudioFix()
    {
        Log("FMOD bridge v2.1.1 loaded; direct native binding; live volume tracking; Unity MovieTexture video");
    }

    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_System_Create(out IntPtr system);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_System_Init(IntPtr system, int channels, uint flags, IntPtr extra);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_System_CreateStream(IntPtr system, string path, uint mode, IntPtr info, out IntPtr sound);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_System_PlaySound(IntPtr system, int index, IntPtr sound, int paused, out IntPtr channel);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_Channel_SetVolume(IntPtr channel, float volume);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_Channel_SetPaused(IntPtr channel, int paused);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_Channel_Stop(IntPtr channel);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_Channel_IsPlaying(IntPtr channel, out int playing);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_Sound_Release(IntPtr sound);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_System_Update(IntPtr system);
    [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FMOD_System_Release(IntPtr system);

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException(operation + ": FMOD result " + result);
    }

    private static object Property(object value, string name)
    {
        if (value == null) return null;
        PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property == null ? null : property.GetValue(value, null);
    }

    private static object Field(object value, string name)
    {
        if (value == null) return null;
        for (Type type = value.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(value);
        }
        return null;
    }

    private static void Log(string message)
    {
        message = "[EL Movie Audio v2] " + message;
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type debug = assembly.GetType("UnityEngine.Debug", false);
                if (debug == null) continue;
                MethodInfo log = debug.GetMethod("Log", new Type[] { typeof(object) });
                if (log != null)
                {
                    log.Invoke(null, new object[] { message });
                    return;
                }
            }
        }
        catch { }
        Console.Error.WriteLine(message);
    }

    private static void WarnOnce(string key, string message)
    {
        if (Warned.ContainsKey(key)) return;
        Warned[key] = true;
        Log(message);
    }

    private static IntPtr ExistingSystem()
    {
        Type managerType = null, objectType = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (managerType == null) managerType = assembly.GetType("Amplitude.Unity.Audio.AudioManager", false);
            if (objectType == null) objectType = assembly.GetType("UnityEngine.Object", false);
        }
        if (managerType == null || objectType == null) return IntPtr.Zero;
        MethodInfo find = objectType.GetMethod("FindObjectOfType", new Type[] { typeof(Type) });
        if (find == null) return IntPtr.Zero;
        object manager = find.Invoke(null, new object[] { managerType });
        object initialized = Field(manager, "fmodSystemInitialized");
        if (initialized is bool && !(bool)initialized) return IntPtr.Zero;
        object system = Field(manager, "fmodSystem");
        if (system == null) return IntPtr.Zero;
        MethodInfo raw = system.GetType().GetMethod("getRaw", Type.EmptyTypes);
        return raw == null ? IntPtr.Zero : (IntPtr)raw.Invoke(system, null);
    }

    private static string Sidecar(string movie)
    {
        if (String.IsNullOrEmpty(movie)) return null;
        string[] candidates = new string[] {
            movie + ".audio.wav",
            System.IO.Path.ChangeExtension(movie, ".bik") + ".audio.wav",
            System.IO.Path.ChangeExtension(movie.Replace("Movies, OSX", "Movies"), ".bik") + ".audio.wav"
        };
        foreach (string path in candidates)
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static float SourceVolume(object audioSource)
    {
        object volume = Property(audioSource, "volume");
        float level = volume == null ? 1.0f : Convert.ToSingle(volume);
        object mute = Property(audioSource, "mute");
        return mute != null && Convert.ToBoolean(mute) ? 0.0f : level;
    }

    private static void UpdateVolume(Playback state)
    {
        // CutsceneModalPanel sets volume after LoadMovie has already started audio.
        float level = SourceVolume(state.AudioSource);
        if (level == state.Volume) return;
        Check(FMOD_Channel_SetVolume(state.Channel, level), "update volume");
        state.Volume = level;
        Log("volume updated " + System.IO.Path.GetFileName(state.Path) + "=" + level);
    }

    public static void Play(object audioSource, string moviePath, object owner)
    {
        Stop(owner);
        Playback state = new Playback();
        try
        {
            string path = Sidecar(moviePath);
            if (path == null)
            {
                WarnOnce("missing:" + moviePath, "no sidecar for " + moviePath + "; keeping Unity audio");
                OriginalPlay(audioSource);
                return;
            }
            state.Path = path;
            state.AudioSource = audioSource;
            try
            {
                state.System = ExistingSystem();
            }
            catch (Exception error)
            {
                WarnOnce("lookup", "existing FMOD system lookup failed: " +
                         error.GetType().FullName + ": " + error.Message);
            }
            if (state.System == IntPtr.Zero)
            {
                Check(FMOD_System_Create(out state.System), "create system");
                state.OwnSystem = true;
                Check(FMOD_System_Init(state.System, 8, 0, IntPtr.Zero), "initialize system");
            }
            // FMOD_LOOP_OFF | FMOD_2D. All narrated cutscenes are finite.
            Check(FMOD_System_CreateStream(state.System, path, 0x00000009u, IntPtr.Zero, out state.Sound), "create stream");
            Check(FMOD_System_PlaySound(state.System, -1, state.Sound, 1, out state.Channel), "create paused channel");
            state.Volume = SourceVolume(audioSource);
            Check(FMOD_Channel_SetVolume(state.Channel, state.Volume), "set volume");
            Check(FMOD_Channel_SetPaused(state.Channel, 0), "start sound");
            if (state.OwnSystem) Check(FMOD_System_Update(state.System), "update system");
            Playing[owner] = state;
            Log("playing " + System.IO.Path.GetFileName(moviePath) + " via " + (state.OwnSystem ? "standalone" : "game") + " FMOD, volume=" + state.Volume);
        }
        catch (Exception error)
        {
            Dispose(state);
            Log("play failed: " + error.GetType().FullName + ": " + error.Message + "; keeping Unity audio");
            OriginalPlay(audioSource);
        }
    }

    private static void OriginalPlay(object source)
    {
        if (source == null) return;
        try
        {
            source.GetType().GetMethod("Play", Type.EmptyTypes).Invoke(source, null);
        }
        catch (Exception error)
        {
            Log("Unity audio fallback failed: " + error.GetType().FullName + ": " + error.Message);
        }
    }

    public static void Tick(object owner)
    {
        Playback state;
        if (owner == null || !Playing.TryGetValue(owner, out state)) return;
        try
        {
            if (state.OwnSystem) Check(FMOD_System_Update(state.System), "update system");
            int playing;
            int result = FMOD_Channel_IsPlaying(state.Channel, out playing);
            if (result != 0 || playing == 0)
            {
                Stop(owner);
                return;
            }
            object texture = Field(owner, "movieTexture");
            object running = Property(texture, "isPlaying");
            if (running != null && !Convert.ToBoolean(running))
            {
                Stop(owner);
                return;
            }
            UpdateVolume(state);
        }
        catch (Exception error)
        {
            Log("update failed: " + error.GetType().FullName + ": " + error.Message);
            Stop(owner);
        }
    }

    public static void Stop(object owner)
    {
        Playback state;
        if (owner == null || !Playing.TryGetValue(owner, out state)) return;
        Playing.Remove(owner);
        Dispose(state);
        Log("stopped " + System.IO.Path.GetFileName(state.Path));
    }

    private static void Dispose(Playback state)
    {
        // One failed cleanup call must not prevent releasing the remaining resources.
        if (state.Channel != IntPtr.Zero)
        {
            try { FMOD_Channel_Stop(state.Channel); }
            catch { }
            state.Channel = IntPtr.Zero;
        }
        if (state.Sound != IntPtr.Zero)
        {
            try { FMOD_Sound_Release(state.Sound); }
            catch { }
            state.Sound = IntPtr.Zero;
        }
        if (state.OwnSystem && state.System != IntPtr.Zero)
        {
            try { FMOD_System_Release(state.System); }
            catch { }
            state.System = IntPtr.Zero;
        }
    }
}
