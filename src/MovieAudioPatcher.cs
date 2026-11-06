using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class MovieAudioPatcher
{
    private const string Version = "1.0.0";
    private const string OriginalHash = "ed19f8e0c6f5b7e0786f240af4b322b85498a5db112e99a8b31ac108bacef397";
    private const string Managed = "EndlessLegend.app/Contents/Resources/Data/Managed/";
    private const string Movies = "EndlessLegend.app/Contents/Resources/Data/StreamingAssets/Movies/";
    private const string GameAssembly = Managed + "Assembly-CSharp-firstpass.dll";
    private const string AudioHelper = Managed + "ELMovieAudioFix.dll";

    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    private static void CheckGame(string game)
    {
        string assembly = Path.Combine(game, GameAssembly);
        if (!File.Exists(assembly) || Hash(assembly) != OriginalHash)
            throw new InvalidOperationException("Unsupported game assembly; ELCP 2.7.9 for macOS is required.");
        if (!File.Exists(Path.Combine(game, "libfmodex.dylib")))
            throw new FileNotFoundException("Game FMOD library not found.");
        if (Directory.Exists(Path.Combine(game, "MovieAudioFix")) ||
            File.Exists(Path.Combine(game, AudioHelper)) || File.Exists(Path.Combine(game, AudioHelper + ".config")))
            throw new InvalidOperationException("Movie audio fix files already exist. Remove the existing installation before installing.");
    }

    private static void BindHelper(string template, string library, string output)
    {
        using (AssemblyDefinition helper = AssemblyDefinition.ReadAssembly(template))
        {
            int imports = 0;
            foreach (ModuleReference module in helper.MainModule.ModuleReferences)
                if (module.Name == "el_movie_fmod") module.Name = library;
            foreach (TypeDefinition type in helper.MainModule.Types)
            {
                foreach (MethodDefinition method in type.Methods)
                    if (method.HasPInvokeInfo && method.PInvokeInfo.Module.Name == library) imports++;
                foreach (FieldDefinition field in type.Fields)
                    if (field.Name == "NativeLibrary" && field.HasConstant) field.Constant = library;
            }
            if (imports != 11 || helper.Name.Name != "ELMovieAudioFix")
                throw new InvalidOperationException("Unrecognized audio helper template.");
            helper.Write(output);
        }
    }

    private static void Prepare(string game, string output)
    {
        CheckGame(game);
        if (Directory.Exists(output) || File.Exists(output))
            throw new InvalidOperationException("Preparation output already exists; use a new directory.");
        string files = Path.Combine(output, "files");
        string backup = Path.Combine(output, "MovieAudioFix/backup");
        Directory.CreateDirectory(Path.Combine(files, Managed));
        Directory.CreateDirectory(backup);
        File.Copy(Path.Combine(game, GameAssembly), Path.Combine(backup, "Assembly-CSharp-firstpass.dll"));
        string template = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ELMovieAudioFix.template.dll");
        string helper = Path.Combine(files, AudioHelper);
        BindHelper(template, Path.Combine(game, "libfmodex.dylib"), helper);
        PatchAssembly(Path.Combine(game, GameAssembly), helper, Path.Combine(files, GameAssembly));
        Console.WriteLine("Prepared game assembly, bound audio helper and original backup.");
    }

    private static string Quote(string value)
    {
        StringBuilder result = new StringBuilder("\"");
        foreach (char character in value)
        {
            if (character == '"' || character == '\\') result.Append('\\').Append(character);
            else if (character < 32) result.Append("\\u").Append(((int)character).ToString("x4"));
            else result.Append(character);
        }
        return result.Append('"').ToString();
    }

    private static void WriteManifest(string game, string output)
    {
        CheckGame(game);
        string files = Path.Combine(output, "files");
        string state = Path.Combine(output, "MovieAudioFix");
        string backup = Path.Combine(state, "backup/Assembly-CSharp-firstpass.dll");
        if (!File.Exists(backup) || Hash(backup) != OriginalHash)
            throw new InvalidOperationException("Original backup is missing or has changed.");
        if (!File.Exists(Path.Combine(files, GameAssembly)) || !File.Exists(Path.Combine(files, AudioHelper)))
            throw new InvalidOperationException("Prepared assembly or helper is missing.");
        string[] paths = Directory.GetFiles(files, "*", SearchOption.AllDirectories);
        Array.Sort(paths, StringComparer.Ordinal);
        StringBuilder json = new StringBuilder();
        json.Append("{\n  \"format\": 1,\n  \"version\": ").Append(Quote(Version));
        json.Append(",\n  \"original_sha256\": ").Append(Quote(OriginalHash));
        json.Append(",\n  \"native_library\": ").Append(Quote(Path.Combine(game, "libfmodex.dylib")));
        json.Append(",\n  \"files\": [\n");
        int tracks = 0;
        for (int index = 0; index < paths.Length; index++)
        {
            string relative = paths[index].Substring(files.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
            bool audio = relative.StartsWith(Movies, StringComparison.Ordinal) &&
                         relative.EndsWith(".bik.audio.wav", StringComparison.Ordinal);
            if (relative != GameAssembly && relative != AudioHelper && !audio)
                throw new InvalidOperationException("Unexpected prepared file: " + relative);
            if (relative != GameAssembly && File.Exists(Path.Combine(game, relative)))
                throw new InvalidOperationException("Refusing to overwrite existing file: " + relative);
            if (audio) tracks++;
            json.Append("    {\"path\": ").Append(Quote(relative));
            json.Append(", \"sha256\": ").Append(Quote(Hash(paths[index]))).Append("}");
            json.Append(index + 1 == paths.Length ? "\n" : ",\n");
        }
        if (tracks == 0) throw new InvalidOperationException("No extracted WAV tracks; extract movie audio first.");
        json.Append("  ]\n}\n");
        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../install.py"),
                  Path.Combine(state, "install.py"), true);
        File.WriteAllText(Path.Combine(state, "manifest.json"), json.ToString(), new UTF8Encoding(false));
        Console.WriteLine("Prepared manifest for " + tracks + " audio tracks.");
    }

    public static int Main(string[] args)
    {
        if (args.Length != 3 || (args[0] != "prepare" && args[0] != "manifest"))
        {
            Console.Error.WriteLine("Usage: mono MovieAudioPatcher.exe prepare|manifest GAME OUTPUT");
            return 2;
        }
        try
        {
            string game = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
            string output = Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar);
            if (args[0] == "prepare") Prepare(game, output);
            else WriteManifest(game, output);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Error: " + error.Message);
            return 1;
        }
    }

    private static MethodDefinition Method(TypeDefinition type, string name)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (method.Name == name) return method;
        }
        throw new InvalidOperationException("Missing method: " + type.FullName + "::" + name);
    }
    private static FieldDefinition Field(TypeDefinition type, string name)
    {
        foreach (FieldDefinition field in type.Fields)
        {
            if (field.Name == name) return field;
        }
        throw new InvalidOperationException("Missing field: " + type.FullName + "::" + name);
    }
    private static bool Call(Instruction instruction, string type, string name)
    {
        MethodReference method = instruction.Operand as MethodReference;
        return method != null && method.DeclaringType.FullName == type && method.Name == name;
    }
    private static void Prefix(MethodDefinition method, MethodReference hook)
    {
        ILProcessor il = method.Body.GetILProcessor();
        Instruction first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        method.Body.MaxStackSize = Math.Max(2, method.Body.MaxStackSize);
    }
    private static void ExpandBranches(MethodDefinition method)
    {
        // Injected instructions can push a short branch past its signed-byte range.
        foreach (Instruction ins in method.Body.Instructions)
        {
            if (ins.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
            string fullName = ins.OpCode.Name.Substring(0, ins.OpCode.Name.Length - 2);
            foreach (System.Reflection.FieldInfo field in typeof(OpCodes).GetFields())
            {
                if (field.FieldType != typeof(OpCode)) continue;
                OpCode candidate = (OpCode)field.GetValue(null);
                if (candidate.Name == fullName)
                {
                    ins.OpCode = candidate;
                    break;
                }
            }
        }
    }
    private static void PatchAssembly(string input, string helperPath, string output)
    {
        DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input)));
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(helperPath)));
        ReaderParameters read = new ReaderParameters { AssemblyResolver = resolver };
        AssemblyDefinition original = AssemblyDefinition.ReadAssembly(input, read);
        AssemblyDefinition helper = AssemblyDefinition.ReadAssembly(helperPath, read);
        ModuleDefinition module = original.MainModule;
        TypeDefinition bridge = helper.MainModule.GetType("ELMovieAudioFix");
        MethodReference play = module.ImportReference(Method(bridge, "Play"));
        MethodReference stop = module.ImportReference(Method(bridge, "Stop"));
        MethodReference tick = module.ImportReference(Method(bridge, "Tick"));
        TypeDefinition movie = module.GetType("AgePrimitiveMovie");
        TypeDefinition loader = null;
        foreach (TypeDefinition type in movie.NestedTypes) if (type.Name.StartsWith("<LoadMovie>")) loader = type;
        if (loader == null) throw new InvalidOperationException("Missing movie loading coroutine");
        FieldDefinition owner = Field(loader, "<>f__this");
        FieldDefinition path = Field(loader, "<moviePath>__0");
        MethodDefinition move = Method(loader, "MoveNext");
        ILProcessor il = move.Body.GetILProcessor();
        int count = 0;
        foreach (Instruction ins in new List<Instruction>(move.Body.Instructions))
        {
            if (!Call(ins, "UnityEngine.AudioSource", "Play")) continue;
            il.InsertBefore(ins, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(ins, il.Create(OpCodes.Ldfld, path));
            il.InsertBefore(ins, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(ins, il.Create(OpCodes.Ldfld, owner));
            ins.OpCode = OpCodes.Call;
            ins.Operand = play;
            count++;
        }
        if (count != 2) throw new InvalidOperationException("Expected two cutscene AudioSource.Play calls, found " + count);
        move.Body.MaxStackSize = Math.Max(5, move.Body.MaxStackSize);
        ExpandBranches(move);
        Prefix(Method(movie, "UnloadMovie"), stop);
        Prefix(Method(movie, "OnMoviePlaybackComplete"), stop);
        Prefix(Method(movie, "Update"), tick);

        TypeDefinition boot = module.GetType("Amplitude.Unity.Framework.BackgroundMovieBootstrapper");
        TypeDefinition bootIterator = null;
        foreach (TypeDefinition type in boot.NestedTypes) if (type.Name.StartsWith("<Play>")) bootIterator = type;
        if (bootIterator == null) throw new InvalidOperationException("Missing startup movie coroutine");
        FieldDefinition savedPath = new FieldDefinition("elMovieAudioPath", FieldAttributes.Assembly, module.TypeSystem.String);
        boot.Fields.Add(savedPath);
        FieldDefinition bootOwner = Field(bootIterator, "<>f__this");
        FieldDefinition bootPath = Field(bootIterator, "<moviePath>__7");
        MethodDefinition bootMove = Method(bootIterator, "MoveNext");
        ILProcessor bil = bootMove.Body.GetILProcessor();
        int starts = 0;
        foreach (Instruction ins in new List<Instruction>(bootMove.Body.Instructions))
        {
            if (!Call(ins, boot.FullName, "PlayMovie")) continue;
            // The PlayMovie receiver is already on the evaluation stack.
            bil.InsertBefore(ins, bil.Create(OpCodes.Dup));
            bil.InsertBefore(ins, bil.Create(OpCodes.Ldarg_0));
            bil.InsertBefore(ins, bil.Create(OpCodes.Ldfld, bootPath));
            bil.InsertBefore(ins, bil.Create(OpCodes.Stfld, savedPath));
            starts++;
        }
        if (starts != 1) throw new InvalidOperationException("Unexpected startup PlayMovie call count");
        // Tick each time the startup coroutine resumes, without adding a Unity Update method.
        Instruction first = bootMove.Body.Instructions[0];
        bil.InsertBefore(first, bil.Create(OpCodes.Ldarg_0));
        bil.InsertBefore(first, bil.Create(OpCodes.Ldfld, bootOwner));
        bil.InsertBefore(first, bil.Create(OpCodes.Call, tick));
        ExpandBranches(bootMove);
        bootMove.Body.MaxStackSize = Math.Max(5, bootMove.Body.MaxStackSize);

        MethodDefinition bootPlay = Method(boot, "PlayMovie");
        ILProcessor pil = bootPlay.Body.GetILProcessor();
        int bootCalls = 0;
        foreach (Instruction ins in new List<Instruction>(bootPlay.Body.Instructions))
        {
            if (!Call(ins, "UnityEngine.AudioSource", "Play")) continue;
            pil.InsertBefore(ins, pil.Create(OpCodes.Ldarg_0));
            pil.InsertBefore(ins, pil.Create(OpCodes.Ldfld, savedPath));
            pil.InsertBefore(ins, pil.Create(OpCodes.Ldarg_0));
            ins.OpCode = OpCodes.Call;
            ins.Operand = play;
            bootCalls++;
        }
        if (bootCalls != 1) throw new InvalidOperationException("Unexpected startup AudioSource.Play call count");
        ExpandBranches(bootPlay);
        bootPlay.Body.MaxStackSize = Math.Max(5, bootPlay.Body.MaxStackSize);
        Prefix(Method(boot, "UnloadMovie"), stop);
        Prefix(Method(boot, "OnDisable"), stop);
        Prefix(Method(boot, "OnApplicationQuit"), stop);
        original.Write(output);
        Console.WriteLine("Patched 3 audio starts, 5 cleanup hooks and 2 update paths; video calls preserved.");
        helper.Dispose();
        original.Dispose();
    }
}
