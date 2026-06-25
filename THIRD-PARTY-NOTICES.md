# Third-Party Notices

HeifSharp includes managed MIT-licensed bindings plus precompiled native runtime
libraries under `natives/`.

## libheif

Bundled `libheif` binaries are built from libheif 1.21.2 and are licensed under
LGPL-3.0. Source and license details are documented in `scripts/VERSIONS.md`.

## x265

Bundled x265 binaries and the `libheif-plugin-x265` plugin are built from x265
git commit `b81f650e21e8aacbe6a9ad04ce14aefc05b932c0` and are GPL-2.0. libheif
loads the encoder plugin at runtime through its plugin API.

## MinGW-w64 runtime DLLs

Windows x64 native binaries bundle `libgcc_s_seh-1.dll`, `libstdc++-6.dll`, and
`libwinpthread-1.dll` from Debian bookworm's MinGW-w64 POSIX runtime packages so
the `win-x64` RID is self-contained. `libgcc` and `libstdc++` are distributed
under GPL-3.0-or-later with the GCC Runtime Library Exception; `libwinpthread`
is distributed as part of the MinGW-w64 runtime. Source and package details are
documented in `scripts/VERSIONS.md`.

## kvazaar

The build script can alternatively build a kvazaar encoder plugin. kvazaar is
BSD-3-Clause licensed. No kvazaar native binaries are bundled in this release.
