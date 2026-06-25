# HeifSharp

C# bindings for [libheif](https://github.com/strukturag/libheif) producing
Apple-compatible HEIC stills. Mirrors the `tryAGI.OpusSharp` pattern: vendored
native binaries under `natives/`, a `NativeLibrary.SetDllImportResolver`-based
loader, and a managed `IDisposable` wrapper.

The NuGet package id is `tryAGI.HeifSharp`. The public namespace remains
`HeifSharp`.

## Installation

```bash
dotnet add package tryAGI.HeifSharp
```

The repository currently commits native assets for `linux-arm64`, `osx-arm64`,
and `osx-x64`. The project file already has conditional package entries for
`linux-x64` and `win-x64`; build and commit those binaries with
`scripts/build-natives.sh` before relying on those RIDs.

## Why

The realtime avatar pipeline needs a 30 fps still-frame format that watchOS can
hardware-decode at ~12 KB / frame (≈360 KB/s). JPEG is too heavy at that frame
rate; HEVC over fMP4 is great when LL-HLS works but has a 1–2 s startup latency.
HEIC stills give us hardware decode + per-frame addressability + bandwidth
parity with HLS, at the cost of needing libheif + an HEVC encoder backend on
the server.

## Encoder backend & license posture

By default, `build-natives.sh` builds with `x265` (GPL-2.0) and `kvazaar` is
optional. libheif itself is LGPL-3.0 and is the only thing the .NET wrapper
links against. `libheif-plugin-x265.so` is loaded by libheif at runtime via the
plugin API (`dlopen`), the same model Debian uses to keep the GPL surface
contained — see https://packages.debian.org/trixie/libheif-plugin-x265.

If your legal posture does not accept this, build with kvazaar only:

```bash
WITH_X265=OFF WITH_KVAZAAR=ON scripts/build-natives.sh osx-arm64
```

Apple HW-decode validation **must** be repeated against real watchOS hardware
when switching encoder backends — kvazaar is HEVC-Main spec-correct but has a
shorter field track record on Apple decoders than x265.

## Building native binaries

```bash
# macOS dev (run directly on host)
scripts/build-natives.sh osx-arm64

# Linux ARM64 (production target — use the codified Dockerfile to match prod glibc)
docker build -f Dockerfile.build-natives \
  --platform linux/arm64 \
  --target export \
  --output type=local,dest=natives/linux/linux-arm64 \
  .

# Or run the script directly inside the same image (slower for one-off use):
docker run --rm --platform linux/arm64 -v "$(pwd):/repo" -w /repo debian:bookworm-slim bash -c '
  apt-get update -qq && apt-get install -y -qq --no-install-recommends \
    cmake build-essential git nasm pkg-config curl ca-certificates >/dev/null
  scripts/build-natives.sh linux-arm64
'
```

Outputs are committed under `natives/{macos,linux/<rid>,windows}/`
and packed into the NuGet under `runtimes/<rid>/native/`. Expect ~6–10 MB per RID
combined for libheif + libx265 + plugin.

**Why `debian:bookworm-slim`?** Production runs on `mcr.microsoft.com/dotnet/aspnet:10.0`,
which is Debian bookworm. Building against a newer base (e.g. trixie) risks
`GLIBC_2.41 not found` errors at runtime. Keep the Dockerfile base in sync with the
.NET runtime base whenever it bumps.

## Usage (once natives are present)

```csharp
using HeifSharp;

var rgba = ReadRgbaFromSomewhere(width: 1280, height: 720);
using var encoder = new HeifEncoder(); // Apple-safe defaults

byte[] heic = encoder.EncodeRgba(rgba, 1280, 720);
// heic is a complete .heic file: ftyp box (heic/mif1/hvc1) + meta + mdat
```

The same `HeifEncoder` instance can be reused for many sequential frames; it is
not thread-safe.

## Apple compatibility constraints (baked into defaults)

These are the constraints `HeifEncoderConfig.AppleSafeDefaults()` enforces:

| Constraint                 | Default       | Why                                             |
|----------------------------|---------------|-------------------------------------------------|
| HEVC profile               | `main`        | Only profile reliably HW-decoded on watchOS     |
| Chroma subsampling         | `4:2:0`       | Apple HW decoders don't accept 4:2:2 / 4:4:4    |
| Bit depth                  | 8             | Main profile is 8-bit                           |
| ftyp brand                 | `hvc1`        | `hev1` rejected by Apple HW decoder             |
| Color tag                  | NCLX BT.709   | Avoids colour shift on Apple display pipeline   |
| Even width × height        | enforced      | Odd dims rejected by HEVC HW decoder            |

Override at your own risk.

## Verifying

```bash
dotnet build src/libs/HeifSharp/HeifSharp.csproj
dotnet test  src/tests/HeifSharp.Tests/HeifSharp.Tests.csproj
```

Tests gracefully **skip** when libheif isn't loadable (no committed binaries +
not on `$PATH`), so the project is safe to land before `scripts/build-natives.sh`
runs in CI. Once binaries are committed, the encoder + Apple-compat tests run.

Final validation before pipeline integration **must** include AirDropping a
`HeifEncoderTests` output to a real Apple Watch and confirming Photos opens it.
macOS Quick Look is too permissive to be a reliable proxy for watchOS HW decode.

## Layout

```
HeifSharp/
├── src/libs/HeifSharp/   # net10.0 library
│   ├── HeifEncoder.cs    # public API
│   ├── HeifEncoderConfig.cs
│   ├── HeifException.cs
│   └── Native/           # P/Invoke + loader
├── src/tests/HeifSharp.Tests/ # xUnit tests (skip when natives missing)
├── natives/                   # vendored binaries per RID (built by build-natives.sh)
├── scripts/
│   ├── build-natives.sh
│   └── VERSIONS.md
└── README.md
```

## Pattern reference

This project mirrors the extracted `tryAGI.OpusSharp` package layout:

- Vendored binaries under `natives/`, packed into NuGet `runtimes/<rid>/native/`.
- Loader uses `NativeLibrary.SetDllImportResolver` with a per-platform path list.
- Managed wrapper is a sealed `IDisposable` holding `IntPtr`.
- Tests use xUnit + FluentAssertions with the same package versions.

The one place HeifSharp differs from OpusSharp: libheif's parameter API is fully
typed (`heif_encoder_set_parameter_integer/_string/_boolean`), so we don't need
the variadic shim library that OpusSharp ships as `libopus_sharp`.
