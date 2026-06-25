# Third-Party Notices

HeifSharp includes managed MIT-licensed bindings plus precompiled native runtime
libraries under `natives/`.

## libheif

Bundled `libheif` binaries are built from libheif 1.21.2 and are licensed under
LGPL-3.0. Source and license details are documented in `scripts/VERSIONS.md`.

## x265

Bundled x265 binaries and the `libheif-plugin-x265` plugin are built from x265
git commit `7b3d1f515318f73056abd9e99944e9f79db090bd` and are GPL-2.0. libheif
loads the encoder plugin at runtime through its plugin API.

## kvazaar

The build script can alternatively build a kvazaar encoder plugin. kvazaar is
BSD-3-Clause licensed. No kvazaar native binaries are bundled in this release.
