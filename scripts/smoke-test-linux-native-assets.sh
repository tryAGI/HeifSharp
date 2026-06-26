#!/usr/bin/env bash
set -euo pipefail

PROJECT_PATH="${PROJECT_PATH:-src/libs/HeifSharp/HeifSharp.csproj}"
RUNTIME_IMAGE="${DOTNET_RUNTIME_IMAGE:-mcr.microsoft.com/dotnet/runtime:10.0}"
RID_ARGS=("$@")
if [[ ${#RID_ARGS[@]} -eq 0 ]]; then
  RID_ARGS=(linux-x64 linux-arm64)
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required for Linux native smoke tests." >&2
  exit 1
fi

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/heifsharp-linux-smoke.XXXXXX")"
cleanup() {
  if [[ "${KEEP_HEIFSHARP_SMOKE_DIR:-}" != "1" ]]; then
    rm -rf "$WORK_DIR"
  else
    echo "Keeping smoke test directory: $WORK_DIR"
  fi
}
trap cleanup EXIT

PACKAGE_DIR="$WORK_DIR/packages"
APP_DIR="$WORK_DIR/consumer"
mkdir -p "$PACKAGE_DIR"

dotnet pack "$PROJECT_PATH" --configuration Release --nologo --output "$PACKAGE_DIR"

packages=("$PACKAGE_DIR"/tryAGI.HeifSharp.*.nupkg)
if [[ ${#packages[@]} -ne 1 ]]; then
  echo "Expected exactly one tryAGI.HeifSharp package in $PACKAGE_DIR, found ${#packages[@]}." >&2
  printf '%s\n' "${packages[@]}" >&2
  exit 1
fi

package_name="$(basename "${packages[0]}")"
package_version="${package_name#tryAGI.HeifSharp.}"
package_version="${package_version%.nupkg}"

dotnet new console --framework net10.0 --name HeifSharpSmoke --output "$APP_DIR" >/dev/null

cat > "$APP_DIR/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$PACKAGE_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

cat > "$APP_DIR/Program.cs" <<'CS'
using HeifSharp;

const int width = 64;
const int height = 64;
var rgba = new byte[width * height * 4];

for (var y = 0; y < height; y++)
{
    for (var x = 0; x < width; x++)
    {
        var offset = (y * width + x) * 4;
        rgba[offset] = (byte)(x * 255 / Math.Max(1, width - 1));
        rgba[offset + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
        rgba[offset + 2] = 128;
        rgba[offset + 3] = 255;
    }
}

using var encoder = new HeifEncoder();
var heic = encoder.EncodeRgba(rgba, width, height);

if (heic.Length <= 64)
{
    throw new InvalidOperationException($"Unexpected HEIC length: {heic.Length}");
}

Console.WriteLine($"ok bytes={heic.Length}");
CS

dotnet add "$APP_DIR" package tryAGI.HeifSharp --version "$package_version" --no-restore >/dev/null

for rid in "${RID_ARGS[@]}"; do
  case "$rid" in
    linux-x64)
      docker_platform="linux/amd64"
      ;;
    linux-arm64)
      docker_platform="linux/arm64"
      ;;
    *)
      echo "Unsupported Linux smoke RID: $rid" >&2
      exit 1
      ;;
  esac

  echo "Restoring and publishing HeifSharp smoke consumer for $rid..."
  NUGET_PACKAGES="$WORK_DIR/nuget-$rid" dotnet restore "$APP_DIR" \
    --runtime "$rid" \
    --configfile "$APP_DIR/NuGet.config" >/dev/null
  NUGET_PACKAGES="$WORK_DIR/nuget-$rid" dotnet publish "$APP_DIR" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained false \
    --no-restore \
    --nologo >/dev/null

  publish_dir="$APP_DIR/bin/Release/net10.0/$rid/publish"
  required_assets=(
    "$publish_dir/libheif.so"
    "$publish_dir/libheif-plugin-x265.so"
    "$publish_dir/libheif-x265.so"
    "$publish_dir/libx265.so"
  )

  for asset in "${required_assets[@]}"; do
    if [[ ! -f "$asset" ]]; then
      echo "Missing required native asset for $rid: $asset" >&2
      exit 1
    fi
  done

  echo "Running HeifSharp smoke consumer in $docker_platform..."
  docker run --rm \
    --platform "$docker_platform" \
    -v "$publish_dir:/app:ro" \
    -w /app \
    "$RUNTIME_IMAGE" \
    dotnet HeifSharpSmoke.dll
done
