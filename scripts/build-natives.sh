#!/usr/bin/env bash
#
# Builds vendored libheif + x265 (or kvazaar) shared libraries for one RID and copies the
# result into natives/. Pinned versions live in VERSIONS.md.
#
# Usage:
#   build-natives.sh <rid>
#
# RIDs supported:
#   osx-arm64, osx-x64        # macOS (run on the matching architecture host)
#   linux-arm64, linux-x64    # Linux (run on the matching architecture host or via cross-build)
#   win-x64                   # Windows (run from MSYS2/MinGW)
#
# Required tools: git, cmake (>= 3.16), curl or wget, a C/C++ toolchain, nasm (for x265),
# pkg-config. macOS additionally needs Xcode command-line tools.
#
# Environment overrides:
#   WITH_X265=ON|OFF     (default ON)   — build & include the x265 plugin
#   WITH_KVAZAAR=ON|OFF  (default OFF)  — build & include the kvazaar plugin
#   JOBS=<n>             (default nproc) — parallel build jobs
#
# Output layout (per RID):
#   natives/macos/libheif.dylib
#   natives/macos/libheif-plugin-x265.dylib
#   natives/macos/libx265.dylib
#   natives/linux/<rid>/libheif.so
#   natives/linux/<rid>/libheif-plugin-x265.so
#   natives/linux/<rid>/libx265.so
#   natives/windows/heif.dll
#   natives/windows/heif-plugin-x265.dll
#   natives/windows/x265.dll

set -euo pipefail

if [[ "${1:-}" == "" ]]; then
  echo "Usage: $0 <rid>   (e.g. linux-arm64, osx-arm64, linux-x64, osx-x64, win-x64)" >&2
  exit 64
fi
RID="$1"

# Pinned versions — keep in sync with VERSIONS.md.
LIBHEIF_VERSION="1.21.2"
LIBHEIF_SHA256="75f530b7154bc93e7ecf846edfc0416bf5f490612de8c45983c36385aa742b42"
# x265: pinned master commit SHA; override via X265_GIT_REF when bumping.
# We use git here (instead of a release tarball) because the most recent x265 release
# (4.1) does not compile against Xcode 21's clang on Apple Silicon — the NEON-intrinsic
# include path was fixed only on master. Pin the SHA in VERSIONS.md after a successful
# build so future runs are reproducible.
X265_GIT_URL="${X265_GIT_URL:-https://bitbucket.org/multicoreware/x265_git.git}"
X265_GIT_REF="${X265_GIT_REF:-b81f650e21e8aacbe6a9ad04ce14aefc05b932c0}"
KVAZAAR_VERSION="2.3.1"

WITH_X265="${WITH_X265:-ON}"
WITH_KVAZAAR="${WITH_KVAZAAR:-OFF}"
JOBS="${JOBS:-$( (nproc 2>/dev/null) || (sysctl -n hw.ncpu 2>/dev/null) || echo 4 )}"

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ROOT_DIR="$( cd "$SCRIPT_DIR/.." && pwd )"
NATIVES_DIR="$ROOT_DIR/natives"
WORK_DIR="$ROOT_DIR/.build/$RID"
PREFIX="$WORK_DIR/install"
CMAKE_SYSTEM_ARGS=()

if [[ "$RID" == "win-x64" && "$(uname -s)" != MINGW* && "$(uname -s)" != MSYS* ]]; then
  if [[ -z "${CC:-}" ]]; then
    if command -v x86_64-w64-mingw32-gcc-posix >/dev/null; then
      CC=x86_64-w64-mingw32-gcc-posix
    else
      CC=x86_64-w64-mingw32-gcc
    fi
  fi
  if [[ -z "${CXX:-}" ]]; then
    if command -v x86_64-w64-mingw32-g++-posix >/dev/null; then
      CXX=x86_64-w64-mingw32-g++-posix
    else
      CXX=x86_64-w64-mingw32-g++
    fi
  fi
  : "${RC:=x86_64-w64-mingw32-windres}"

  command -v "$CC" >/dev/null || { echo "Missing $CC for win-x64 cross-build" >&2; exit 69; }
  command -v "$CXX" >/dev/null || { echo "Missing $CXX for win-x64 cross-build" >&2; exit 69; }
  command -v "$RC" >/dev/null || { echo "Missing $RC for win-x64 cross-build" >&2; exit 69; }

  MINGW_SYSROOT="$("$CC" -print-sysroot)"
  CMAKE_SYSTEM_ARGS=(
    -DCMAKE_SYSTEM_NAME=Windows
    -DCMAKE_SYSTEM_PROCESSOR=x86_64
    -DCMAKE_C_COMPILER="$CC"
    -DCMAKE_CXX_COMPILER="$CXX"
    -DCMAKE_RC_COMPILER="$RC"
    -DCMAKE_FIND_ROOT_PATH="$MINGW_SYSROOT"
    -DCMAKE_FIND_ROOT_PATH_MODE_PROGRAM=NEVER
    -DCMAKE_FIND_ROOT_PATH_MODE_LIBRARY=ONLY
    -DCMAKE_FIND_ROOT_PATH_MODE_INCLUDE=ONLY
    -DCMAKE_FIND_ROOT_PATH_MODE_PACKAGE=ONLY
  )
fi

echo "==> Building HeifSharp natives for $RID"
echo "    libheif=$LIBHEIF_VERSION   x265=git:$X265_GIT_REF (enabled=$WITH_X265)   kvazaar=$KVAZAAR_VERSION (enabled=$WITH_KVAZAAR)"
echo "    work dir: $WORK_DIR"
mkdir -p "$WORK_DIR" "$PREFIX"

# --- helpers ---
fetch() {
  local url="$1" out="$2"
  if [[ -f "$out" ]]; then return; fi
  echo "==> Fetch $url"
  if command -v curl >/dev/null; then
    curl -fL --retry 3 -o "$out" "$url"
  else
    wget -O "$out" "$url"
  fi
}

# Verify a downloaded file's SHA256 against an expected value. Empty expected = skip.
verify_sha256() {
  local file="$1" expected="$2"
  if [[ -z "$expected" ]]; then return 0; fi
  local actual
  if command -v shasum >/dev/null; then
    actual=$(shasum -a 256 "$file" | awk '{print $1}')
  elif command -v sha256sum >/dev/null; then
    actual=$(sha256sum "$file" | awk '{print $1}')
  else
    echo "WARN: no shasum/sha256sum available — skipping checksum verification of $file" >&2
    return 0
  fi
  if [[ "$actual" != "$expected" ]]; then
    echo "ERROR: SHA256 mismatch for $file" >&2
    echo "  expected: $expected" >&2
    echo "  actual:   $actual" >&2
    return 1
  fi
  echo "    SHA256 OK ($actual)"
}

# --- x265 ---
if [[ "$WITH_X265" == "ON" ]]; then
  echo "==> Building x265 (git $X265_GIT_REF from $X265_GIT_URL)"
  X265_REPO="$WORK_DIR/x265-src"
  if [[ ! -d "$X265_REPO/.git" ]]; then
    git clone --depth 1 --branch "$X265_GIT_REF" "$X265_GIT_URL" "$X265_REPO" || \
      git clone "$X265_GIT_URL" "$X265_REPO"
  fi
  git config --global --add safe.directory "$X265_REPO" >/dev/null 2>&1 || true
  (cd "$X265_REPO" && git fetch --depth 1 origin "$X265_GIT_REF" >/dev/null 2>&1 || true)
  (cd "$X265_REPO" && git checkout "$X265_GIT_REF" >/dev/null 2>&1 || true)
  X265_HEAD_SHA=$(cd "$X265_REPO" && git rev-parse HEAD)
  echo "    x265 HEAD = $X265_HEAD_SHA"
  X265_SRC="$X265_REPO/source"
  X265_BUILD="$WORK_DIR/x265-build"
  # x265's CMakeLists historically pins CMP0025/CMP0054 to OLD and uses an old
  # cmake_minimum_required. cmake 4.x removed support for both. Patch defensively —
  # safe no-op if upstream has already fixed it.
  if [[ -f "$X265_SRC/CMakeLists.txt" ]]; then
    sed_inplace() {
      if sed --version 2>/dev/null | grep -q GNU; then
        sed -i "$@"
      else
        sed -i '' "$@"
      fi
    }
    sed_inplace -E 's/cmake_policy\(SET (CMP0025|CMP0054) OLD\)/cmake_policy(SET \1 NEW)/g' "$X265_SRC/CMakeLists.txt"
    sed_inplace -E 's/cmake_minimum_required *\( *VERSION *[0-9]+\.[0-9]+(\.[0-9]+)? *\)/cmake_minimum_required(VERSION 3.13)/' "$X265_SRC/CMakeLists.txt"
    if grep -q 'list(GET VERSION_LIST 0 X265_VERSION_MAJOR)' "$X265_SRC/CMakeLists.txt" &&
       ! grep -q 'X265_VERSION_LIST_LENGTH' "$X265_SRC/CMakeLists.txt"; then
      sed_inplace '/list(GET VERSION_LIST 0 X265_VERSION_MAJOR)/i\
list(LENGTH VERSION_LIST X265_VERSION_LIST_LENGTH)\
if(X265_VERSION_LIST_LENGTH LESS 2)\
    set(VERSION_LIST 0 0)\
endif()
' "$X265_SRC/CMakeLists.txt"
    fi
    if grep -q 'set(X265_BRANCH_ID 0)' "$X265_SRC/CMakeLists.txt" &&
       ! grep -q 'X265_TAG_DISTANCE.*MATCHES' "$X265_SRC/CMakeLists.txt"; then
      sed_inplace '/set(X265_BRANCH_ID 0)/i\
if(NOT "${X265_TAG_DISTANCE}" MATCHES "^[0-9]+$")\
    set(X265_TAG_DISTANCE 0)\
endif()
' "$X265_SRC/CMakeLists.txt"
    fi
  fi
  # CMAKE_POSITION_INDEPENDENT_CODE=ON is required on Linux: libheif's plugin is a
  # shared object that statically links libx265 (cmake --install on x265 only ships
  # the .a). Without -fPIC the linker emits "dangerous relocation: unsupported
  # relocation" on aarch64 and refuses to produce libheif-x265.so. macOS is more
  # tolerant but the flag is harmless there.
  cmake -S "$X265_SRC" -B "$X265_BUILD" \
    "${CMAKE_SYSTEM_ARGS[@]}" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DENABLE_SHARED=ON -DENABLE_CLI=OFF -DENABLE_TESTS=OFF
  cmake --build "$X265_BUILD" -j"$JOBS"
  cmake --install "$X265_BUILD"
  # x265's cmake install ships only the static lib in PREFIX/lib/. Copy the shared
  # library from the build dir so libheif's find_package picks it up at link time.
  case "$RID" in
    osx-*)
      X265_SHLIB="$( ls "$X265_BUILD"/libx265.*.dylib 2>/dev/null | sed -n '1p' || true )"
      [[ -n "$X265_SHLIB" && -f "$X265_SHLIB" ]] && cp -L "$X265_SHLIB" "$PREFIX/lib/libx265.dylib"
      ;;
    linux-*)
      X265_SHLIB="$( ls "$X265_BUILD"/libx265.so* 2>/dev/null | grep -v '\.so$' | sed -n '1p' || true )"
      [[ -z "$X265_SHLIB" ]] && X265_SHLIB="$X265_BUILD/libx265.so"
      [[ -f "$X265_SHLIB" ]] && cp -L "$X265_SHLIB" "$PREFIX/lib/libx265.so" || true
      ;;
    win-x64)
      X265_DLL="$( ls "$X265_BUILD"/*x265*.dll 2>/dev/null | sed -n '1p' || true )"
      if [[ -n "$X265_DLL" && -f "$X265_DLL" ]]; then
        mkdir -p "$PREFIX/bin"
        cp -L "$X265_DLL" "$PREFIX/bin/x265.dll"
      fi
      ;;
  esac
fi

# --- kvazaar (optional) ---
if [[ "$WITH_KVAZAAR" == "ON" ]]; then
  echo "==> Building kvazaar $KVAZAAR_VERSION"
  KV_TGZ="$WORK_DIR/kvazaar-${KVAZAAR_VERSION}.tar.gz"
  fetch "https://github.com/ultravideo/kvazaar/archive/refs/tags/v${KVAZAAR_VERSION}.tar.gz" "$KV_TGZ"
  tar -xzf "$KV_TGZ" -C "$WORK_DIR"
  KV_SRC="$WORK_DIR/kvazaar-${KVAZAAR_VERSION}"
  KV_BUILD="$WORK_DIR/kvazaar-build"
  cmake -S "$KV_SRC" -B "$KV_BUILD" \
    "${CMAKE_SYSTEM_ARGS[@]}" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DBUILD_SHARED_LIBS=ON
  cmake --build "$KV_BUILD" -j"$JOBS"
  cmake --install "$KV_BUILD"
fi

# --- libheif ---
echo "==> Building libheif $LIBHEIF_VERSION"
LH_TGZ="$WORK_DIR/libheif-${LIBHEIF_VERSION}.tar.gz"
fetch "https://github.com/strukturag/libheif/releases/download/v${LIBHEIF_VERSION}/libheif-${LIBHEIF_VERSION}.tar.gz" "$LH_TGZ"
verify_sha256 "$LH_TGZ" "$LIBHEIF_SHA256"
tar -xzf "$LH_TGZ" -C "$WORK_DIR"
LH_SRC="$WORK_DIR/libheif-${LIBHEIF_VERSION}"
LH_BUILD="$WORK_DIR/libheif-build"

# We deliberately disable AOM/dav1d/rav1e/jpeg2000/svt to keep the AVIF/AV1 surface and
# unrelated codec deps out of our binaries. Only HEVC encoders are pulled in.
cmake -S "$LH_SRC" -B "$LH_BUILD" \
  "${CMAKE_SYSTEM_ARGS[@]}" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DCMAKE_PREFIX_PATH="$PREFIX" \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTING=OFF \
  -DWITH_EXAMPLES=OFF \
  -DWITH_X265="$WITH_X265" \
  -DWITH_X265_PLUGIN="$WITH_X265" \
  -DWITH_KVAZAAR="$WITH_KVAZAAR" \
  -DWITH_KVAZAAR_PLUGIN="$WITH_KVAZAAR" \
  -DWITH_AOM_DECODER=OFF \
  -DWITH_AOM_ENCODER=OFF \
  -DWITH_DAV1D=OFF \
  -DWITH_RAV1E=OFF \
  -DWITH_SvtEnc=OFF \
  -DWITH_JPEG_DECODER=OFF \
  -DWITH_JPEG_ENCODER=OFF \
  -DWITH_OpenJPEG_DECODER=OFF \
  -DWITH_OpenJPEG_ENCODER=OFF
cmake --build "$LH_BUILD" -j"$JOBS"
cmake --install "$LH_BUILD"

# --- copy outputs into natives/ ---
case "$RID" in
  osx-arm64|osx-x64)
    OUT_DIR="$NATIVES_DIR/macos"
    mkdir -p "$OUT_DIR"
    cp -L "$PREFIX/lib/libheif.dylib"             "$OUT_DIR/libheif.dylib"
    if [[ "$WITH_X265" == "ON" ]]; then
      # x265's cmake install only ships .a on macOS even with ENABLE_SHARED=ON;
      # grab the .dylib from the build dir directly, then normalize the name.
      X265_DYLIB="$( ls "$WORK_DIR"/x265-build/libx265.*.dylib 2>/dev/null | sed -n '1p' || true )"
      if [[ -z "$X265_DYLIB" ]]; then
        X265_DYLIB="$PREFIX/lib/libx265.dylib"
      fi
      [[ -f "$X265_DYLIB" ]] && cp -L "$X265_DYLIB" "$OUT_DIR/libx265.dylib"
      # libheif on macOS installs its plugin as `libheif-<encoder>.so` (extension
      # follows libheif's CMake convention even on Mach-O). Rename to a .dylib with
      # the explicit "plugin" prefix our packaging expects.
      PLUGIN_SRC="$( ls "$PREFIX"/lib/libheif/libheif-x265.* 2>/dev/null | sed -n '1p' || true )"
      if [[ -n "$PLUGIN_SRC" && -f "$PLUGIN_SRC" ]]; then
        cp -L "$PLUGIN_SRC" "$OUT_DIR/libheif-plugin-x265.dylib"
      fi
    fi
    # Strip absolute install paths from rpath / dylib id so the .dylib is portable.
    [[ -f "$OUT_DIR/libheif.dylib" ]] && install_name_tool -id "@rpath/libheif.dylib" "$OUT_DIR/libheif.dylib" || true
    [[ -f "$OUT_DIR/libx265.dylib" ]] && install_name_tool -id "@rpath/libx265.dylib" "$OUT_DIR/libx265.dylib" || true
    [[ -f "$OUT_DIR/libheif-plugin-x265.dylib" ]] && install_name_tool -id "@rpath/libheif-plugin-x265.dylib" "$OUT_DIR/libheif-plugin-x265.dylib" || true
    # Plugin links against @rpath/libheif.1.dylib at build time. Make that resolve
    # to libheif.dylib sitting next to the plugin, regardless of the loading
    # process's rpath setup, by using @loader_path.
    if [[ -f "$OUT_DIR/libheif-plugin-x265.dylib" ]]; then
      for dep in $(otool -L "$OUT_DIR/libheif-plugin-x265.dylib" 2>/dev/null | awk 'NR>1{print $1}' | grep -E "libheif(\.[0-9]+)?\.dylib"); do
        install_name_tool -change "$dep" "@loader_path/libheif.dylib" "$OUT_DIR/libheif-plugin-x265.dylib" || true
      done
    fi
    # Some libheif builds emit a versioned soname for the plugin link (libheif.1.dylib).
    # Provide an alias so any unfixed @rpath/libheif.1.dylib still resolves.
    if [[ -f "$OUT_DIR/libheif.dylib" && ! -f "$OUT_DIR/libheif.1.dylib" ]]; then
      cp -L "$OUT_DIR/libheif.dylib" "$OUT_DIR/libheif.1.dylib"
      install_name_tool -id "@rpath/libheif.1.dylib" "$OUT_DIR/libheif.1.dylib" || true
    fi
    # Wipe libheif's compiled-in plugin install path so it can't auto-load a
    # broken sibling left over from the build dir on this dev machine. We don't
    # rely on auto-load anyway — EnsurePluginsLoaded does explicit registration.
    rm -rf "$WORK_DIR/install/lib/libheif" 2>/dev/null || true
    # libheif's directory scan looks for files matching "libheif-*.so" (the upstream
    # install convention is the same on macOS, even though the actual file is Mach-O).
    # Provide an alias under that exact name so heif_load_plugins(dir) picks it up.
    if [[ -f "$OUT_DIR/libheif-plugin-x265.dylib" && ! -f "$OUT_DIR/libheif-x265.so" ]]; then
      cp -L "$OUT_DIR/libheif-plugin-x265.dylib" "$OUT_DIR/libheif-x265.so"
    fi
    # Self-codesign so macOS will load the dylibs.
    codesign --force --sign - "$OUT_DIR"/*.dylib "$OUT_DIR"/*.so 2>/dev/null || true
    ;;
  linux-x64|linux-arm64)
    OUT_DIR="$NATIVES_DIR/linux/$RID"
    mkdir -p "$OUT_DIR"
    cp -L "$PREFIX/lib/libheif.so"                "$OUT_DIR/libheif.so"
    if [[ "$WITH_X265" == "ON" ]]; then
      [[ -f "$PREFIX/lib/libx265.so" ]] && cp -L "$PREFIX/lib/libx265.so" "$OUT_DIR/libx265.so"
      # libheif's plugin install path is $PREFIX/lib/libheif/ and the file is named
      # `libheif-<encoder>.so` (no "plugin" infix). Match either naming for safety.
      PLUGIN_SRC="$( ls "$PREFIX"/lib/libheif/libheif-x265.* 2>/dev/null | sed -n '1p' || true )"
      if [[ -z "$PLUGIN_SRC" ]]; then
        PLUGIN_SRC="$( ls "$PREFIX"/lib/libheif-plugin-x265.so 2>/dev/null | sed -n '1p' || true )"
      fi
      if [[ -n "$PLUGIN_SRC" && -f "$PLUGIN_SRC" ]]; then
        # Use upstream-convention name `libheif-x265.so` so libheif's directory
        # scan (heif_load_plugins) picks it up. Also drop a `-plugin-` alias for
        # consumers that expect that name (matches our macOS layout).
        cp -L "$PLUGIN_SRC" "$OUT_DIR/libheif-x265.so"
        cp -L "$PLUGIN_SRC" "$OUT_DIR/libheif-plugin-x265.so"
      fi
    fi
    strip --strip-unneeded "$OUT_DIR"/*.so 2>/dev/null || true
    ;;
  win-x64)
    OUT_DIR="$NATIVES_DIR/windows"
    mkdir -p "$OUT_DIR"
    copy_first() {
      local dest="$1"
      shift
      local src
      for src in "$@"; do
        if [[ -f "$src" ]]; then
          cp -L "$src" "$dest"
          return 0
        fi
      done
      echo "Missing output for $dest; tried: $*" >&2
      return 1
    }

    copy_first "$OUT_DIR/libheif.dll" \
      "$PREFIX/bin/libheif.dll" \
      "$PREFIX/bin/heif.dll" \
      "$PREFIX/bin/libheif-1.dll"
    cp -L "$OUT_DIR/libheif.dll" "$OUT_DIR/heif.dll"

    if [[ "$WITH_X265" == "ON" ]]; then
      copy_first "$OUT_DIR/x265.dll" \
        "$PREFIX/bin/x265.dll" \
        "$PREFIX/bin/libx265.dll"

      PLUGIN_SRC="$( ls "$PREFIX"/bin/*heif*x265*.dll "$PREFIX"/lib/libheif/*heif*x265*.dll 2>/dev/null | sed -n '1p' || true )"
      if [[ -z "$PLUGIN_SRC" ]]; then
        echo "Missing x265 plugin DLL under $PREFIX" >&2
        exit 1
      fi
      cp -L "$PLUGIN_SRC" "$OUT_DIR/heif-plugin-x265.dll"
    fi

    copy_runtime_dll() {
      local name="$1"
      local tool="$2"
      local src
      src="$("$tool" "-print-file-name=$name" 2>/dev/null || true)"
      if [[ "$src" == "$name" ]]; then
        src=""
      fi
      if [[ -n "$src" && -f "$src" ]]; then
        cp -L "$src" "$OUT_DIR/$name"
        return 0
      fi

      local tool_path
      tool_path="$(command -v "$tool" 2>/dev/null || true)"
      local search_dirs=()
      if [[ -n "$tool_path" ]]; then
        search_dirs+=("$(dirname "$tool_path")")
      fi
      if [[ -n "${MINGW_SYSROOT:-}" ]]; then
        search_dirs+=("$MINGW_SYSROOT/bin" "$MINGW_SYSROOT/lib")
      fi

      local libgcc_path
      libgcc_path="$("$tool" -print-libgcc-file-name 2>/dev/null || true)"
      if [[ -n "$libgcc_path" && -f "$libgcc_path" ]]; then
        search_dirs+=("$(dirname "$libgcc_path")")
      fi

      search_dirs+=("/usr/x86_64-w64-mingw32/bin" "/usr/x86_64-w64-mingw32/lib")
      local dir
      for dir in "${search_dirs[@]}"; do
        if [[ -f "$dir/$name" ]]; then
          cp -L "$dir/$name" "$OUT_DIR/$name"
          return 0
        fi
      done

      echo "WARN: could not find MinGW runtime DLL $name; Windows binaries may need it" >&2
      return 0
    }

    copy_runtime_dll "libgcc_s_seh-1.dll" "$CC"
    copy_runtime_dll "libstdc++-6.dll" "$CXX"
    copy_runtime_dll "libwinpthread-1.dll" "$CC"
    ;;
  *)
    echo "Unsupported RID: $RID" >&2
    exit 64
    ;;
esac

echo "==> Done. Outputs in $OUT_DIR"
ls -lh "$OUT_DIR"
