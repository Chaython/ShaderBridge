# Snapshot Correctness and Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ShaderBridge snapshots coherent under live cache mutation, import armed VSS pre-update cache state after driver changes, and safely bound permanent snapshot storage with retention and garbage collection.

**Architecture:** Keep the existing v2 4 MiB content-addressed chunk format. Add stable source reading and atomic manifest publication inside the snapshot pipeline, extend VSS transition state to capture frozen cache-root mappings and support post-update import, and add a separate store-maintenance service for retention/GC/statistics so destructive cleanup is isolated from snapshot creation.

**Tech Stack:** C#/.NET 10 WPF, existing Windows PowerShell/WMI VSS integration, dependency-free console test harness, GitHub Actions on `windows-latest`.

**Spec:** `docs/superpowers/specs/2026-09-28-next-storage-improvements-design.md`

## Global Constraints

- Live shader-cache folders remain fully writable at all times.
- Never inject into games, graphics APIs, anti-cheat processes, or drivers.
- Existing v1 and v2 snapshot manifests remain readable.
- Existing v2 4 MiB chunk storage remains the permanent storage format for this batch.
- VSS remains optional; normal snapshots must work without it.
- Destructive cleanup may delete only data under ShaderBridge's own `chunks` and legacy `objects` stores proven unreferenced by every readable retained manifest.
- Corrupt/unreadable manifests block destructive GC.
- CI must restore, build, run storage tests, publish win-x64, and upload the artifact before merge.

## Review Focus

- File metadata can change without size changing; stable reads must detect last-write/creation changes and retry.
- VSS transition JSON created by the previous release has no cache-root mappings; it must remain releasable but not importable.
- A corrupt manifest must make GC fail closed without deleting any chunk/object.
- Pinned snapshots must survive retention pruning even when their driver fingerprint is outside the newest-N set.
- A partial VSS release must immediately persist the remaining owned shadow IDs so later retries cannot target already-deleted shadows.

---

### Task 1: Stable chunk reads and atomic manifest publication

**Files:**
- Modify: `src/ShaderBridge/Services/ChunkStore.cs`
- Modify: `src/ShaderBridge/Services/SnapshotService.cs`
- Modify: `tests/ShaderBridge.Tests/Program.cs`

**Interfaces:**
- Produces: `ChunkStore.StoreFileStableAsync(string sourcePath, string storeRoot, int maxAttempts = 3, CancellationToken ct = default) -> Task<ChunkStoreResult>`
- Produces: atomic internal manifest writer in `SnapshotService`
- Existing `StoreFileDetailedAsync` remains available for immutable/VSS sources.

- [ ] **Step 1: Add failing stable-read tests**

Add tests that mutate a source file during the first read attempt and assert the stable API retries and returns the final coherent bytes/hash; add a second test that forces metadata changes through all three attempts and asserts an `IOException` identifying an unstable source.

- [ ] **Step 2: Run CI/test harness and verify RED**

Run: `dotnet run --project .\tests\ShaderBridge.Tests\ShaderBridge.Tests.csproj -c Release`

Expected: build/test failure because `StoreFileStableAsync` does not exist.

- [ ] **Step 3: Implement `StoreFileStableAsync`**

Capture length, last-write UTC, and creation UTC before each read. Run the existing chunk/hash pipeline, recapture metadata afterward, accept only if all checked values match, otherwise retry up to three attempts with a short bounded delay. Throw after exhaustion; do not alter live-file permissions.

- [ ] **Step 4: Add failing atomic-manifest test**

Create a snapshot into a temporary store and assert no final `manifests/<id>.json` becomes visible until complete; assert no stale `*.tmp-*` remains after success.

- [ ] **Step 5: Implement atomic manifest publication**

Serialize to a same-directory temporary file, flush/close, then atomically move to the final manifest filename. Snapshot creation should use stable reads for live sources.

- [ ] **Step 6: Run full test harness GREEN**

Run: `dotnet run --project .\tests\ShaderBridge.Tests\ShaderBridge.Tests.csproj -c Release`

Expected: all storage tests pass.

---

### Task 2: Extend snapshot/VSS models compatibly

**Files:**
- Modify: `src/ShaderBridge/Models/Models.cs`
- Modify: `tests/ShaderBridge.Tests/Program.cs`

**Interfaces:**
- Add `SnapshotSourceType { Live, Vss }`
- Add optional manifest properties: `Pinned`, `SourceType`, `OldDriverFingerprint`, `NewDriverFingerprint`, `VssTransitionCreatedUtc`
- Add `VssCacheRootMapping` with `Name`, `LivePath`, `Kind`, `VolumeRoot`, `ShadowId`, `FrozenPath`
- Extend `VssTransitionState` with `CacheRoots`, `DriverChanged`, `ImportedSnapshotId`

- [ ] **Step 1: Add backward-deserialization tests**

Deserialize representative current v1/v2 manifest JSON without the new fields and assert defaults remain usable: unpinned, Live source, null transition metadata.

- [ ] **Step 2: Add legacy-transition test**

Deserialize existing transition JSON with only shadows and driver fingerprint; assert it remains active/releasable but has zero cache-root mappings.

- [ ] **Step 3: Run tests RED**

Expected: compile failures on the new model members.

- [ ] **Step 4: Add the model fields/enums with backward-compatible defaults**

No snapshot format-number bump is required because chunk representation remains v2.

- [ ] **Step 5: Run tests GREEN**

---

### Task 3: Capture and reconcile VSS cache-root mappings

**Files:**
- Modify: `src/ShaderBridge/Services/VssCheckpointService.cs`
- Modify: `tests/ShaderBridge.Tests/Program.cs`

**Interfaces:**
- Extend `ArmAsync(...)` to persist a `VssCacheRootMapping` for each existing cache root whose volume received a shadow.
- Add `ReconcileAsync(string storeRoot, CancellationToken ct = default) -> Task<VssTransitionState?>`
- Add internal/exposed query helper for checking whether a recorded shadow ID still exists without deleting anything.

- [ ] **Step 1: Add failing mapping test**

Given two roots on one local volume and a known shadow device path, assert arm-time mapping records both original live paths and deterministic frozen paths.

- [ ] **Step 2: Add failing legacy-state reconciliation test**

Given a state with a recorded shadow ID that no longer exists, assert reconciliation removes only that recorded ID; unrecorded shadows are never considered deletion targets.

- [ ] **Step 3: Implement arm-time root capture**

After each successful shadow creation, create mappings only for roots on that volume and persist them immediately with the owned shadow metadata.

- [ ] **Step 4: Implement non-destructive startup reconciliation**

Query Windows for each recorded ID, remove missing IDs/mappings from ShaderBridge state, clear the state file only when no owned shadows remain. Query failure leaves recorded state untouched.

- [ ] **Step 5: Run tests GREEN**

---

### Task 4: Import frozen VSS state into a permanent snapshot

**Files:**
- Modify: `src/ShaderBridge/Services/SnapshotService.cs`
- Modify: `src/ShaderBridge/Services/VssCheckpointService.cs`
- Modify: `src/ShaderBridge/MainWindow.xaml.cs`
- Modify: `src/ShaderBridge/MainWindow.Vss.cs`
- Modify: `src/ShaderBridge/MainWindow.xaml`
- Modify: `tests/ShaderBridge.Tests/Program.cs`

**Interfaces:**
- Add `SnapshotService.CreateFromVssAsync(string storeRoot, VssTransitionState transition, string currentDriverFingerprint, IReadOnlyList<GpuDriverInfo> currentDrivers, CancellationToken ct = default) -> Task<SnapshotManifest>`
- Add `VssCheckpointService.MarkDriverChanged(string storeRoot, string currentDriverFingerprint)`
- UI handlers: `ImportVssSnapshot_Click`, `ImportAndReleaseVss_Click`

- [ ] **Step 1: Add failing VSS-import test**

Create a fake frozen root tree and transition mapping, import it, then assert: source type Vss; old/new fingerprints are recorded; transition timestamp is copied; original live paths appear in snapshot entries; chunk data matches frozen content.

- [ ] **Step 2: Add failing legacy-transition guard test**

A transition with no mappings must throw a clear `InvalidOperationException` on import while remaining releasable.

- [ ] **Step 3: Implement `CreateFromVssAsync`**

Enumerate only recorded frozen cache-root paths. Store their bytes with the immutable-source chunk path, but write each manifest entry's `RootPath` as the original live root so compare/restore semantics stay consistent.

- [ ] **Step 4: Detect driver change against active transition**

When `RefreshDrivers` sees a current fingerprint different from the active transition's armed fingerprint, persist `DriverChanged = true` and update the VSS status UI. Do not auto-release.

- [ ] **Step 5: Add import and import+release controls**

Enable only when an active mapped transition exists and the driver changed. Failed import leaves VSS state untouched. Import+release releases only after snapshot publication succeeds.

- [ ] **Step 6: Reconcile VSS state during startup**

Call `ReconcileAsync` before showing checkpoint status.

- [ ] **Step 7: Run tests GREEN and build the WPF app**

Run:
- `dotnet run --project .\tests\ShaderBridge.Tests\ShaderBridge.Tests.csproj -c Release`
- `dotnet build .\ShaderBridge.sln -c Release`

Expected: both succeed.

---

### Task 5: Snapshot deletion, pinning, retention, and safe garbage collection

**Files:**
- Create: `src/ShaderBridge/Services/StoreMaintenanceService.cs`
- Modify: `src/ShaderBridge/Services/SnapshotService.cs`
- Modify: `src/ShaderBridge/Models/Models.cs`
- Modify: `tests/ShaderBridge.Tests/Program.cs`

**Interfaces:**
- Add `StoreMaintenanceService.DeleteManifest(string storeRoot, string snapshotId)`
- Add `StoreMaintenanceService.SetPinned(string storeRoot, string snapshotId, bool pinned)`
- Add `StoreMaintenanceService.ApplyRetention(string storeRoot, int keepDriverFingerprints) -> RetentionResult`
- Add `StoreMaintenanceService.ScanGarbage(string storeRoot) -> GarbageCollectionPreview`
- Add `StoreMaintenanceService.CollectGarbage(string storeRoot) -> GarbageCollectionResult`
- Add `SnapshotService.TryLoadAllStrict(string storeRoot) -> ManifestLoadResult` where unreadable manifests are reported distinctly rather than silently skipped.

- [ ] **Step 1: Add failing delete/pin tests**

Assert deleting a snapshot removes only its manifest, not chunks. Assert pinning persists in the manifest atomically.

- [ ] **Step 2: Add failing retention test**

Create snapshots spanning at least three driver fingerprints plus a pinned old snapshot; keeping the newest two fingerprints must preserve those two groups and the pinned older snapshot.

- [ ] **Step 3: Add failing GC safety tests**

Cover referenced v2 chunks, referenced v1 objects, true orphan deletion, stale temp cleanup older than 24h, recent temp preservation, and corrupt-manifest fail-closed behavior.

- [ ] **Step 4: Implement strict manifest loading and atomic manifest updates**

GC must receive a complete readable manifest set or refuse destructive cleanup.

- [ ] **Step 5: Implement deletion/pinning/retention**

Retention deletes manifests only; run GC separately after successful pruning.

- [ ] **Step 6: Implement mark-and-sweep GC**

Restrict deletion roots to `chunks` and `objects`; never delete paths derived from `RootPath`/`RelativePath`.

- [ ] **Step 7: Run tests GREEN**

---

### Task 6: Storage statistics and maintenance UI

**Files:**
- Modify: `src/ShaderBridge/Services/StoreMaintenanceService.cs`
- Modify: `src/ShaderBridge/MainWindow.xaml`
- Modify: `src/ShaderBridge/MainWindow.Snapshots.cs`
- Modify: `src/ShaderBridge/MainWindow.Settings.cs`
- Modify: `src/ShaderBridge/Models/Models.cs`
- Modify: `tests/ShaderBridge.Tests/Program.cs`

**Interfaces:**
- Add `StorageStatistics` with `LogicalBytes`, `PhysicalBytes`, `SavedBytes`, `DedupeRatio`, `ManifestCount`, `UniqueObjectCount`, `ReclaimableBytes`
- Add settings: `KeepAllSnapshots = true`, `KeepDriverFingerprints = 3`
- Add snapshot UI handlers for pin/unpin, delete, refresh stats, preview cleanup, clean orphan data.

- [ ] **Step 1: Add failing storage-statistics test**

Create two manifests sharing chunks plus one orphan and assert logical bytes, physical bytes, saved bytes, dedupe ratio, manifest count, unique object count, and reclaimable bytes.

- [ ] **Step 2: Implement statistics from strict manifest/GC scan data**

Do not count manifest JSON size as cache physical bytes.

- [ ] **Step 3: Add Snapshots-tab maintenance controls**

Show source type, pin state, storage stats, and explicit confirmation for delete/clean operations. Preview performs no deletion.

- [ ] **Step 4: Add retention settings**

Default to Keep All. When disabled, clamp keep-driver count to at least 1.

- [ ] **Step 5: Apply retention only after successful snapshot publication**

Automatic baseline/manual/VSS snapshot completion may call retention; failures in retention/GC are logged and must not invalidate the newly created snapshot.

- [ ] **Step 6: Run tests/build GREEN**

---

### Task 7: Documentation and final CI gate

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-09-28-next-storage-improvements.md`
- Verify: `.github/workflows/build.yml`

**Interfaces:**
- No production API changes.

- [ ] **Step 1: Correct the README storage tree**

Show `manifests`, `chunks`, legacy `objects`, and temporary `vss-transition.json`.

- [ ] **Step 2: Document new VSS import workflow**

Explain arm → driver change → import/import+release and legacy unmapped transition limitations.

- [ ] **Step 3: Document retention/GC safety**

Explain pinning, preview cleanup, corrupt-manifest fail-closed behavior, and logical vs physical storage.

- [ ] **Step 4: Push feature branch and open/update PR**

Use `next-storage-improvements` against `main`.

- [ ] **Step 5: Verify the final PR CI run**

Require every stage to succeed:
`Restore → Build → Run storage tests → Publish self-contained win-x64 → Upload portable build`.

- [ ] **Step 6: Merge only after green CI**

After merge, verify the `main` push workflow also succeeds and exposes `ShaderBridge-win-x64`.
