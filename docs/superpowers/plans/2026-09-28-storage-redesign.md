# Storage-Efficient Snapshots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Replace whole-file-only permanent snapshots with 4 MiB chunk dedupe and add optional short-lived VSS driver-transition checkpoints.

**Architecture:** A focused ChunkStore owns content-addressed chunk storage and rehydration. SnapshotService writes v2 manifests while retaining the legacy object restore fallback. VssCheckpointService owns local-volume discovery, shadow-path mapping, elevated creation/deletion, and persisted checkpoint metadata.

**Tech Stack:** C#/.NET 10 WPF, Windows PowerShell/WMI Win32_ShadowCopy, dependency-free console tests.

**Spec:** docs/superpowers/specs/2026-09-28-storage-redesign.md

## Global Constraints

- Do not block writes to live shader caches.
- 4 MiB fixed chunks.
- Existing version-1 snapshots remain readable.
- VSS is optional and gracefully degradable.
- No game-process injection or graphics DLL proxying.
- No third-party test packages.

## Review Focus

- Files not divisible by 4 MiB must round-trip exactly.
- Empty files must snapshot/restore.
- Identical chunks at different offsets must deduplicate safely.
- VSS path mapping must never accept UNC or mismatched volumes.
- Elevation cancellation/VSS unsupported must leave existing snapshots usable.

### Task 1: Chunk store and v2 manifest
- [ ] Add dependency-free tests for dedupe and rehydration and observe RED.
- [ ] Add SnapshotChunk metadata and snapshot format version.
- [ ] Implement ChunkStore with 4 MiB chunks.
- [ ] Move SnapshotService create/restore to chunks with v1 fallback.
- [ ] Run tests GREEN.

### Task 2: VSS checkpoint service
- [ ] Add tests for volume extraction and shadow-path mapping and observe RED.
- [ ] Add VssCheckpoint/VssTransitionState models.
- [ ] Implement VSS creation/deletion through elevated Windows PowerShell using Win32_ShadowCopy ClientAccessible.
- [ ] Persist only ShaderBridge-created shadow IDs.
- [ ] Run tests GREEN.

### Task 3: UI and settings
- [ ] Add Arm driver update / Release checkpoint controls and status.
- [ ] Keep ordinary snapshots available when VSS is unavailable.
- [ ] Update README with storage model and elevation behavior.

### Task 4: CI verification
- [ ] Add test harness to solution/workflow.
- [ ] Build, run tests, publish self-contained win-x64, upload artifact.
- [ ] Merge only after feature-branch CI is green.


Execution status: RED test harness committed; awaiting first CI compile gate.
