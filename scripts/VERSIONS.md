# HeifSharp native dependency versions

Source versions pinned by `scripts/build-natives.sh`. Bump these together (libheif and the
HEVC plugin must be ABI-compatible). After bumping, rebuild for every RID and update the
binaries under `natives/`.

| Component | Version | SHA256 | License | Source |
|-----------|---------|--------|---------|--------|
| libheif   | 1.21.2  | `75f530b7154bc93e7ecf846edfc0416bf5f490612de8c45983c36385aa742b42` | LGPL-3.0 | https://github.com/strukturag/libheif/releases/tag/v1.21.2 |
| x265      | git master @ `7b3d1f515318f73056abd9e99944e9f79db090bd` (verified osx-arm64, 2026-05-10) | n/a (git checkout) | GPL-2.0 (loaded as runtime plugin only) | https://bitbucket.org/multicoreware/x265_git.git |
| kvazaar   | 2.3.1   | (capture on first build) | BSD-3-Clause (alternative encoder) | https://github.com/ultravideo/kvazaar/releases/tag/v2.3.1 |

To build with this exact x265 SHA:
```bash
X265_GIT_REF=7b3d1f515318f73056abd9e99944e9f79db090bd scripts/build-natives.sh osx-arm64
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
