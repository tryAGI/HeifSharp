using System.Runtime.InteropServices;
using Xunit;

namespace HeifSharp.Tests;

/// <summary>
/// Fact attribute for tests that need only libheif to be loadable. Skips if the loader
/// can't find libheif at all.
/// </summary>
public sealed class NativeRequiredFactAttribute : FactAttribute
{
    public NativeRequiredFactAttribute()
    {
        if (!NativeProbe.LibHeifLoadable)
        {
            Skip = $"libheif is not loadable. {NativeProbe.SkipHint}";
        }
    }
}

/// <summary>
/// Fact attribute for tests that need a working HEVC encoder plugin (x265 or kvazaar).
/// Skips if libheif loads but no HEVC encoder backend is registered, so encode-dependent
/// tests pass cleanly on machines without the plugin (e.g. dev macs with bare Homebrew
/// libheif, or CI before scripts/build-natives.sh has run).
/// </summary>
public sealed class HevcEncoderRequiredFactAttribute : FactAttribute
{
    public HevcEncoderRequiredFactAttribute()
    {
        if (!NativeProbe.LibHeifLoadable)
        {
            Skip = $"libheif is not loadable. {NativeProbe.SkipHint}";
            return;
        }
        if (!NativeProbe.HevcEncoderUsable)
        {
            Skip = "libheif loaded but no HEVC encoder plugin is available. " +
                   "Install libheif-plugin-x265 (apt) / brew install libheif --with-x265, " +
                   "or run scripts/build-natives.sh.";
        }
    }
}

public sealed class HevcEncoderRequiredTheoryAttribute : TheoryAttribute
{
    public HevcEncoderRequiredTheoryAttribute()
    {
        if (!NativeProbe.LibHeifLoadable)
        {
            Skip = $"libheif is not loadable. {NativeProbe.SkipHint}";
            return;
        }
        if (!NativeProbe.HevcEncoderUsable)
        {
            Skip = "libheif loaded but no HEVC encoder plugin is available. " +
                   "Install libheif-plugin-x265 (apt) / brew install libheif --with-x265, " +
                   "or run scripts/build-natives.sh.";
        }
    }
}

internal static class NativeProbe
{
    private static readonly Lazy<bool> _loadable = new Lazy<bool>(Probe);
    private static readonly Lazy<bool> _hevcUsable = new Lazy<bool>(ProbeHevcEncoder);

    public static bool LibHeifLoadable => _loadable.Value;

    /// <summary>
    /// True if libheif loads AND a usable HEVC encoder backend (x265 or kvazaar) is
    /// available — verified by attempting a tiny end-to-end encode.
    /// </summary>
    public static bool HevcEncoderUsable => _hevcUsable.Value;

    public static string SkipHint =>
        "Run scripts/build-natives.sh, or install libheif " +
        "(brew install libheif on macOS, apt-get install libheif1 libheif-plugin-x265 on Linux).";

    private static bool ProbeHevcEncoder()
    {
        if (!LibHeifLoadable) return false;
        try
        {
            using var encoder = new HeifEncoder();
            // 16x16 — smallest sane test, well under any HEVC min-block constraint.
            var rgba = new byte[16 * 16 * 4];
            for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i & 0xFF);
            var heic = encoder.EncodeRgba(rgba, 16, 16);
            return heic != null && heic.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool Probe()
    {
        var dir = Path.GetDirectoryName(typeof(NativeProbe).Assembly.Location) ?? ".";
        var candidates = new System.Collections.Generic.List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(Path.Combine(dir, "runtimes", "win-x64", "native", "heif.dll"));
            candidates.Add(Path.Combine(dir, "heif.dll"));
            candidates.Add("heif");
            candidates.Add("libheif");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add(Path.Combine(dir, "runtimes", "osx-arm64", "native", "libheif.dylib"));
            candidates.Add(Path.Combine(dir, "runtimes", "osx-x64", "native", "libheif.dylib"));
            candidates.Add(Path.Combine(dir, "libheif.dylib"));
            candidates.Add("/opt/homebrew/opt/libheif/lib/libheif.dylib");
            candidates.Add("/opt/homebrew/lib/libheif.dylib");
            candidates.Add("/usr/local/opt/libheif/lib/libheif.dylib");
            candidates.Add("libheif.dylib");
            candidates.Add("libheif");
        }
        else
        {
            var linuxRid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => null,
            };

            if (linuxRid is not null)
            {
                candidates.Add(Path.Combine(dir, "runtimes", linuxRid, "native", "libheif.so"));
            }

            candidates.Add(Path.Combine(dir, "libheif.so"));

            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                candidates.Add("/usr/lib/x86_64-linux-gnu/libheif.so.1");
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                candidates.Add("/usr/lib/aarch64-linux-gnu/libheif.so.1");
            }

            candidates.Add("libheif.so.1");
            candidates.Add("libheif.so");
            candidates.Add("libheif");
        }

        foreach (var c in candidates)
        {
            try
            {
                if (NativeLibrary.TryLoad(c, out var handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }
            }
            catch
            {
                // continue
            }
        }
        return false;
    }
}
