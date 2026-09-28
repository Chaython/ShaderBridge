# ShaderBridge Storage Redesign

## Goal

Reduce permanent shader-cache snapshot storage dramatically while preserving the ability to compare and restore useful game/application cache data across GPU driver updates.

## Constraints

- Never block or deny writes to live shader-cache paths.
- Never inject into games, graphics APIs, or anti-cheat processes.
- Preserve compatibility with existing version-1 whole-file snapshot manifests.
- Keep the application dependency-light and Windows-native.
- VSS is optional and must fail gracefully when unavailable, unsupported, or elevation is declined.

## Permanent snapshot format

Version-2 snapshots use fixed 4 MiB chunks. Each snapshot file keeps its full-file SHA-256 plus an ordered list of chunk hashes and chunk sizes. Chunks are stored content-addressed under `Store/chunks/<prefix>/<sha256>`.

If two versions of a multi-gigabyte cache differ in one chunk, only that new chunk consumes additional permanent storage. Existing version-1 `Store/objects` data remains readable for restore.

## VSS driver-transition checkpoints

VSS is a short-lived, explicit checkpoint mechanism. “Arm driver update” creates one ClientAccessible shadow copy for each supported local cache volume and records its shadow ID/device path and current driver fingerprint.

The live game/driver directories remain writable. The checkpoint is used only to address the pre-update view of a cache. “Release VSS checkpoint” deletes only ShaderBridge-owned shadow copies by ID.

VSS creation/deletion may require elevation. Unsupported volumes and individual VSS failures are reported without breaking normal chunked snapshots.

## UI

Dashboard adds:
- Arm driver update (VSS)
- Release VSS checkpoint
- checkpoint status text

Snapshots continue to create permanent snapshots, now chunked.

## Testing

A dependency-free console test harness verifies:
- 4 MiB chunk dedupe stores only changed chunks across similar large files.
- chunk rehydration reproduces original bytes.
- volume extraction is distinct and deterministic.
- shadow-path mapping maps a live local path to its VSS device path.
- legacy whole-file manifests remain supported by restore paths.

CI builds the app, runs the test harness, publishes win-x64, and uploads the artifact.
