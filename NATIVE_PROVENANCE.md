# Native provenance

`NATIVE_PROVENANCE.json` records SHA-256 and size for all 20 checked-in
native binaries, their configured NuGet paths, declared upstream components,
source revisions/archive hashes where available, and hashes of build inputs.
The baseline repository commit is the revision inspected before this manifest
was introduced. No native files were rebuilt or replaced during recording.

## Verify locally

From the tryAGI workspace:

```sh
python3 scripts/verify-native-provenance.py HeifSharp
python3 scripts/verify-native-provenance.py --require-complete HeifSharp
```

The first command checks integrity and inventory, and prints historical build
attestation gaps. The second fails on those gaps as well. A successful integrity
check does not prove that a binary was built from the declared upstream sources.

## Evidence and limitations

The retained Windows source archive matches the recorded libheif 1.21.2 archive checksum. The retained x265 checkout matches the pinned commit. Build-script CMake modifications are recorded by input hashes. These observations do not establish source-to-binary correspondence for every RID. Exact historical MinGW runtime package versions are unknown; compiler/container identities and build attestations were not retained. License labels come from existing notices, not a new legal review.

## Updating the baseline

Review upstream source/revision, license, download checksums, build environment,
patches and each output hash when rebuilding. Preserve the evidence alongside
the changed manifest. Do not regenerate hashes merely to silence a mismatch.
Unresolved source/build evidence must remain visible; never relabel it verified
without an actual attestation.
