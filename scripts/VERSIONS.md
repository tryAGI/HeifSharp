# HeifSharp native dependency versions

Source versions pinned by `scripts/build-natives.sh`. Bump these together (libheif and the
HEVC plugin must be ABI-compatible). After bumping, rebuild for every RID and update the
binaries under `natives/`.

| Component | Version | SHA256 | License | Source |
|-----------|---------|--------|---------|--------|
| libheif   | 1.21.2  | `75f530b7154bc93e7ecf846edfc0416bf5f490612de8c45983c36385aa742b42` | LGPL-3.0 | https://github.com/strukturag/libheif/releases/tag/v1.21.2 |
| x265      | git master @ `b81f650e21e8aacbe6a9ad04ce14aefc05b932c0` (verified linux-x64 and win-x64, 2026-06-25) | n/a (git checkout) | GPL-2.0 (loaded as runtime plugin only) | https://bitbucket.org/multicoreware/x265_git.git |
| MinGW-w64 runtime DLLs | Debian bookworm `gcc-mingw-w64-x86-64-posix` / `g++-mingw-w64-x86-64-posix` runtime libraries | n/a (Debian packages) | GPL-3.0-or-later WITH GCC-exception-3.1 / ZPL-2.1 | https://packages.debian.org/bookworm/gcc-mingw-w64-x86-64-posix |
| kvazaar   | 2.3.1   | (capture on first build) | BSD-3-Clause (alternative encoder) | https://github.com/ultravideo/kvazaar/releases/tag/v2.3.1 |

To build with this exact x265 SHA:
```bash
X265_GIT_REF=b81f650e21e8aacbe6a9ad04ce14aefc05b932c0 scripts/build-natives.sh osx-arm64
```

### Why x265 is built from git master

x265's most recent tagged release (4.1) does not compile on Apple Silicon against
Xcode 21's clang because the aarch64 NEON intrinsic header inclusion changed.
The fix landed on master post-release. Once `build-natives.sh osx-arm64` succeeds
on a clean checkout, capture the resulting commit SHA from the build log line
"x265 HEAD = <sha>" and pin it via:

```bash
X265_GIT_REF=<sha> scripts/build-natives.sh osx-arm64
```

Update this table with that SHA + the date it was tested.

## Verifying after a bump

```bash
# Per-RID rebuild
scripts/build-natives.sh osx-arm64
scripts/build-natives.sh linux-arm64
scripts/build-natives.sh linux-x64
scripts/build-natives.sh win-x64

# Run tests; Apple-compat tests must still pass
dotnet test src/tests/HeifSharp.Tests/HeifSharp.Tests.csproj

# Manual: AirDrop a 720p HeifEncoderTests output to a real Apple Watch and confirm Photos opens it
```

## License posture (READ BEFORE BUMPING)

- `libheif` is LGPL-3.0; we link it dynamically and that is what the .NET binding consumes.
- `libheif-plugin-x265` is GPL-2.0. libheif loads it via `dlopen` at runtime as a plugin —
  Debian explicitly splits the package into `libheif-plugin-x265` (Suggests) for this
  reason: the GPL surface is contained to a process-local plugin and does not infect the
  consuming application's link line. See https://packages.debian.org/trixie/libheif-plugin-x265.
- `kvazaar` is BSD-3-Clause and ships as `libheif-plugin-kvazaar`. If legal does not accept
  the x265 plugin posture, build the wrapper with kvazaar only by passing
  `WITH_X265=OFF WITH_KVAZAAR=ON` to `build-natives.sh`. Apple HW-decode validation must
  be repeated against real watchOS / iOS hardware before adopting kvazaar in production
  because the field-validation track record is shorter than x265's.
