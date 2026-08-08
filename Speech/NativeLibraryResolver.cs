using System.Reflection;
using System.Runtime.InteropServices;
using PortAudioSharp;
using SherpaOnnx;

namespace StardewWikiAgent.Speech;

/// <summary>
/// SMAPI loads mod assemblies through its own loader, so the default P/Invoke resolver does
/// not probe the mod folder for native libraries — a bare <c>[DllImport("sherpa-onnx-c-api")]</c>
/// fails with <see cref="DllNotFoundException"/>. This registers a resolver that loads the
/// flattened <c>.dylib</c> files sitting next to the managed assemblies from the mod directory.
/// </summary>
internal static class NativeLibraryResolver
{
    private static bool registered;
    private static string modDir = string.Empty;

    /// <summary>Register the resolver for the sherpa-onnx and PortAudioSharp assemblies. Idempotent.</summary>
    public static void Register(string modDirectory)
    {
        if (registered)
            return;
        registered = true;
        modDir = modDirectory;
        NativeLibrary.SetDllImportResolver(typeof(OnlineRecognizer).Assembly, Resolve);
        NativeLibrary.SetDllImportResolver(typeof(PortAudio).Assembly, Resolve);
    }

    /// <summary>Whether the given native library can be loaded from the mod directory.</summary>
    public static bool CanLoad(string libraryName)
    {
        return Resolve(libraryName, typeof(OnlineRecognizer).Assembly, null) != IntPtr.Zero;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (string candidate in CandidateFileNames(libraryName))
        {
            string full = Path.Combine(modDir, candidate);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out IntPtr handle))
                return handle;
        }

        // Fall back to the default resolution (returns IntPtr.Zero → .NET keeps searching).
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateFileNames(string name)
    {
        // The P/Invoke name (e.g. "sherpa-onnx-c-api") maps to a platform-specific file name.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return $"{name}.dll";
            yield return $"lib{name}.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return $"lib{name}.dylib";
            yield return $"{name}.dylib";
        }
        else
        {
            yield return $"lib{name}.so";
            yield return $"{name}.so";
        }
    }
}
