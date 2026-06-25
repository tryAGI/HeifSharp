using System.Reflection;
using System.Runtime.InteropServices;

namespace HeifSharp.Native;

/// <summary>
/// Resolves libheif at runtime, mirroring the OpusSharp NativeLibraryLoader. Search order
/// for each platform is: bundled runtimes/{rid}/native, then well-known system locations
/// (Homebrew on macOS, multi-arch lib dirs on Linux), then bare library names so the OS
/// loader's default search path applies. Throws DllNotFoundException with the attempted
/// paths if none load.
/// </summary>
internal static class NativeLibraryLoader
{
    private static readonly object _lock = new object();
    private static bool _initialized;
    private static string? _loadedFromPath;
    private static bool _pluginsLoaded;

    public static void Initialize()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(HeifNative).Assembly, ResolveDllImport);
                _initialized = true;
            }
            catch (Exception ex)
            {
                throw new HeifException(-1, $"Failed to install libheif resolver: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Lazily loads any vendored libheif plugins (e.g. libheif-plugin-x265.dylib) sitting
    /// next to the resolved libheif binary, OR honour the LIBHEIF_PLUGIN_PATH env var.
    /// libheif 1.18+ no longer auto-discovers plugins reliably when libheif is loaded
    /// from a non-standard install prefix, so callers must register them explicitly. We
    /// do it here so consumers don't have to.
    /// </summary>
    public static void EnsurePluginsLoaded()
    {
        if (_pluginsLoaded) return;
        lock (_lock)
        {
            if (_pluginsLoaded) return;
            try
            {
                // Force libheif to actually load by triggering a P/Invoke. Until something
                // calls into libheif, the SetDllImportResolver resolver hasn't fired yet
                // and _loadedFromPath is still null. heif_get_version is a cheap probe.
                _ = HeifNative.heif_get_version();

                var dir = Environment.GetEnvironmentVariable("LIBHEIF_PLUGIN_PATH");
                if (string.IsNullOrEmpty(dir) && _loadedFromPath is not null)
                {
                    dir = Path.GetDirectoryName(_loadedFromPath);
                }
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // Pre-load shared codec dependencies (libx265, libde265, etc.) BEFORE the
                    // plugin scan so the Linux dynamic loader registers their SONAMEs in the
                    // process. When `heif_load_plugins(dir)` then dlopens `libheif-plugin-x265.so`,
                    // the plugin's DT_NEEDED entry for `libx265.so.NNN` (the SONAME baked in at
                    // link time) is satisfied by the already-loaded library — no need for a
                    // versioned file on disk. Without this, production logs show:
                    //   dlopen: libx265.so.216: cannot open shared object file: No such file
                    // and the HEVC encoder is unavailable.
                    PreloadCodecDependencies(dir);

                    // Directory scan handles all matching plugin files. We deliberately do
                    // NOT call heif_load_plugin (singular) per file: with a null out-plugin
                    // pointer it segfaults inside libheif (verified on libheif 1.21.2).
                    HeifNative.heif_load_plugins(dir, IntPtr.Zero, IntPtr.Zero, 0);
                }
            }
            catch
            {
                // Non-fatal — if no plugins load, the encoder lookup will surface a clear error.
            }
            _pluginsLoaded = true;
        }
    }

    /// <summary>
    /// Loads the unversioned codec libraries (libx265.so / libx265.dylib / libde265.so / etc.)
    /// from the given directory if they exist. The Linux dynamic loader's DT_NEEDED resolver
    /// matches already-loaded libraries by their DT_SONAME tag, so a library shipped as
    /// <c>libx265.so</c> with internal SONAME <c>libx265.so.216</c> satisfies a plugin's
    /// <c>NEEDED libx265.so.216</c> entry once it's been loaded into the process — no file
    /// alias under the versioned name is required.
    /// </summary>
    private static void PreloadCodecDependencies(string dir)
    {
        var candidates = new[]
        {
            "libx265.so",
            "libx265.dylib",
            "libde265.so",
            "libde265.dylib",
            "libaom.so",
            "libaom.dylib",
            "libdav1d.so",
            "libdav1d.dylib",
        };

        foreach (var name in candidates)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try { _ = NativeLibrary.Load(path); }
            catch
            {
                // Best-effort. If a codec library can't load (e.g. missing system dep), the
                // matching plugin will fail at heif_load_plugins or at heif_context_get_encoder
                // and surface a clear error then. Don't block the whole probe.
            }
        }
    }

    private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "heif") return IntPtr.Zero;

        var paths = GetLibraryPaths();
        foreach (var path in paths)
        {
            try
            {
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    // Sanity-check by looking up a known export.
                    if (NativeLibrary.TryGetExport(handle, "heif_get_version", out _))
                    {
                        _loadedFromPath = path;
                        return handle;
                    }
                    try { NativeLibrary.Free(handle); } catch { /* ignore */ }
                }
            }
            catch
            {
                // continue
            }
        }

        throw new DllNotFoundException(
            $"Could not load libheif. Tried paths: {string.Join(", ", paths)}. " +
            "Run scripts/build-natives.sh to build vendored binaries, " +
            "or install libheif via your package manager (brew install libheif / apt install libheif1).");
    }

    private static string[] GetLibraryPaths()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            paths.AddRange(new[]
            {
                Path.Combine(assemblyDir, "heif.dll"),
                Path.Combine(assemblyDir, "libheif.dll"),
                Path.Combine(assemblyDir, "runtimes", "win-x64", "native", "heif.dll"),
                Path.Combine(assemblyDir, "runtimes", "win-x64", "native", "libheif.dll"),
                "heif.dll",
                "libheif.dll",
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            paths.AddRange(new[]
            {
                Path.Combine(assemblyDir, "runtimes", "osx", "native", "libheif.dylib"),
                Path.Combine(assemblyDir, "runtimes", "osx-arm64", "native", "libheif.dylib"),
                Path.Combine(assemblyDir, "runtimes", "osx-x64", "native", "libheif.dylib"),
                Path.Combine(assemblyDir, "libheif.dylib"),
                // Homebrew canonical locations
                "/opt/homebrew/opt/libheif/lib/libheif.dylib",
                "/opt/homebrew/lib/libheif.dylib",
                "/usr/local/opt/libheif/lib/libheif.dylib",
                "/usr/local/lib/libheif.dylib",
                "libheif.dylib",
                "libheif.1.dylib",
                "libheif",
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            paths.AddRange(new[]
            {
                Path.Combine(assemblyDir, "runtimes", "linux-x64", "native", "libheif.so"),
                Path.Combine(assemblyDir, "runtimes", "linux-arm64", "native", "libheif.so"),
                Path.Combine(assemblyDir, "libheif.so"),
                // Common multi-arch locations (Debian/Ubuntu)
                "/usr/lib/x86_64-linux-gnu/libheif.so.1",
                "/usr/lib/aarch64-linux-gnu/libheif.so.1",
                "/lib/x86_64-linux-gnu/libheif.so.1",
                "/lib/aarch64-linux-gnu/libheif.so.1",
                "/usr/lib/libheif.so.1",
                "/usr/local/lib/libheif.so",
                "libheif.so.1",
                "libheif.so",
                "libheif",
            });
        }

        return paths.ToArray();
    }
}
