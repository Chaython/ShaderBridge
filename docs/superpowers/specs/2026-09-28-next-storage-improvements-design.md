# ShaderBridge Next Storage Improvements Design

## Goal

Make ShaderBridge safer during live cache mutation, complete the VSS driver-transition workflow, and keep permanent storage bounded over time without blocking game or driver writes.

## Scope

This batch implements three connected improvements:

1. Stable snapshot reads.
2. Automatic VSS post-driver-update extraction into permanent chunk storage.
3. Snapshot retention, deletion, garbage collection, and storage statistics.

Content-defined chunking, Steam AppID/name resolution, engine-specific cache parsers, and compatibility-history learning remain out of scope for this batch.

## Design principles

- Live shader-cache folders remain fully writable at all times.
- Never inject into games, graphics APIs, anti-cheat processes, or drivers.
- Existing v1 and v2 snapshot manifests remain readable.
- Existing v2 4 MiB chunk storage remains the permanent storage format for this batch.
- VSS is an optional optimization and safety mechanism, not a requirement for normal snapshots.
- Destructive cleanup is explicit and limited to data proven unreferenced by every retained manifest.
- A crash or cancellation must not silently orphan VSS ownership state or corrupt manifests.

## 1. Stable snapshot reads

### Problem

The current chunk store opens live cache files with read/write/delete sharing. A game or driver can modify a file while ShaderBridge is hashing and chunking it, creating a snapshot that never existed as a coherent file state.

### Behavior

A new stable-read path records file identity metadata before reading and rechecks it after reading:

- file length;
- last-write UTC;
- creation UTC where available.

If any checked value changes during the read, the attempt is discarded and retried up to three times with a short bounded delay. The incomplete chunks may already exist in the content-addressed store; they are harmless and will later be eligible for garbage collection.

If the file is still changing after three attempts, ShaderBridge skips the file, records a clear activity-log warning, and continues the rest of the snapshot rather than failing the entire snapshot.

When creating a permanent snapshot from an active VSS transition, ShaderBridge prefers the frozen VSS path and does not need live-file stability retries for those source files.

### Manifest safety

Snapshot manifests are written using:

1. serialize to a temporary file in the manifest directory;
2. flush and close;
3. atomically move/replace into the final manifest filename.

A partially written JSON manifest must never appear as a completed snapshot.

## 2. VSS post-update extraction

### Current behavior

ShaderBridge can create and track VSS checkpoints but currently does not import pre-update cache content from the frozen shadow view.

### Transition detection

Each VSS transition state already records the driver fingerprint at arm time.

When ShaderBridge later detects a different current driver fingerprint while an active transition exists, it marks the transition as `DriverChanged` and exposes a new action:

**Import pre-update cache state**

The app must not automatically release the VSS checkpoint merely because a new driver is detected.

### Frozen cache mapping

For every cache root that existed when the checkpoint was armed, the transition state stores:

- cache-root name;
- live path;
- cache kind;
- volume root;
- corresponding shadow-copy ID;
- mapped frozen VSS path.

This mapping is captured at arm time so a later cache scan cannot accidentally change which directories belong to that transition.

### Import operation

The import operation creates a normal permanent snapshot manifest whose source is the frozen VSS paths rather than the live paths.

The resulting manifest records:

- that it was imported from VSS;
- transition creation time;
- old driver fingerprint;
- current/new driver fingerprint;
- original live root paths;
- normal per-file hash/chunk metadata.

Only cache files under the recorded cache roots are imported. ShaderBridge never archives unrelated files merely because they exist on the same VSS volume.

Because permanent storage is content-addressed, unchanged chunks already present in the store are reused.

### Release behavior

After a successful VSS import, the UI offers **Import and release** and **Keep checkpoint** options.

If release partially fails, successfully deleted shadow IDs are removed from the transition journal immediately while failed IDs remain recorded for retry.

On startup, ShaderBridge reconciles recorded VSS state:

- if a recorded shadow still exists, keep it;
- if Windows already removed it, remove that entry from the journal;
- if no shadows remain, clear the transition-state file.

Reconciliation must never delete an unrecorded VSS shadow.

## 3. Snapshot retention and garbage collection

### Snapshot deletion

The Snapshots tab gains **Delete snapshot**.

Deleting a snapshot removes only its manifest. It does not immediately delete chunk objects.

Legacy v1 object files are treated the same way during garbage collection.

### Retention settings

Settings gain:

- `Keep all snapshots` (default);
- or `Keep last N driver fingerprints`, with N >= 1.

Automatic pruning runs only after a new snapshot completes successfully.

A driver fingerprint group includes every snapshot created under that fingerprint. The newest N distinct driver fingerprints are retained.

Manual snapshots may be pinned. Pinned snapshots are never automatically pruned.

### Garbage collection

Garbage collection is mark-and-sweep:

1. Read every retained manifest.
2. Mark every referenced v2 chunk SHA-256.
3. Mark every referenced v1 whole-file object SHA-256.
4. Enumerate the chunk/object stores.
5. Delete only content-addressed files not referenced by any retained manifest.
6. Ignore temporary/in-progress files younger than 24 hours; stale temporary files older than 24 hours may be removed.
7. Empty hash-prefix directories may be deleted afterward.

GC never follows arbitrary paths from a manifest for deletion; it only deletes files underneath ShaderBridge's own `chunks` and legacy `objects` stores.

### Storage statistics

The UI reports:

- logical bytes represented by retained snapshots;
- physical chunk/object bytes stored;
- deduplicated bytes saved;
- dedupe ratio;
- number of manifests;
- number of unique chunks/legacy objects;
- reclaimable orphan bytes found by a dry-run GC scan.

GC has a **Preview cleanup** operation and a separate **Clean orphan data** action.

## 4. Models and compatibility

`SnapshotManifest` gains optional metadata without breaking older JSON:

- `Pinned`;
- `SourceType` = Live | Vss;
- `OldDriverFingerprint`;
- `NewDriverFingerprint`;
- `VssTransitionCreatedUtc`.

Missing properties deserialize to legacy defaults.

The v2 chunk format does not change in this batch.

`VssTransitionState` gains the recorded cache-root mappings and transition status. Existing transition-state JSON without mappings is considered legacy; it remains releasable but cannot perform a VSS import because ShaderBridge cannot prove which frozen directories belonged to that transition.

## 5. UI changes

### Dashboard

The VSS status area displays:

- active/none;
- old driver fingerprint summary;
- whether a driver change has been detected;
- count of tracked volumes/cache roots;
- checkpoint age.

When a driver change is detected, enable:

- **Import pre-update cache state**
- **Import and release**

### Snapshots

Add:

- Pin/unpin snapshot
- Delete snapshot
- Refresh storage stats
- Preview cleanup
- Clean orphan data

Snapshot details show Live or VSS source.

### Settings

Add retention controls:

- Keep all snapshots
- Keep last N driver versions

## 6. Error handling

- A single unstable or unreadable file is skipped with a log entry.
- A failed VSS import leaves the VSS checkpoint intact.
- A failed manifest write does not create a visible completed snapshot.
- A failed GC deletion logs the path and continues.
- Corrupt/unreadable manifests block destructive GC by default. ShaderBridge must refuse cleanup rather than risk deleting referenced data it could not discover.
- VSS reconciliation failures are non-destructive and leave recorded state intact.

## 7. Testing

The dependency-free test harness is expanded to cover:

- stable-read retry when file metadata changes;
- stable-read failure after retry exhaustion;
- atomic manifest publication;
- VSS live-to-shadow root mapping from recorded transition roots;
- VSS-import manifest metadata;
- deletion of a snapshot manifest without immediate chunk deletion;
- GC retains every referenced v2 chunk;
- GC retains referenced legacy v1 objects;
- GC removes only true orphans;
- GC refuses destructive cleanup if any manifest is unreadable;
- retention keeps the newest N distinct driver fingerprints plus pinned snapshots;
- storage statistics calculate logical/physical/saved bytes correctly;
- backward deserialization of current v1/v2 manifests.

CI remains the final gate: restore, build, storage tests, self-contained win-x64 publish, artifact upload.

## Success criteria

The batch is complete when:

- live cache mutation cannot silently produce a mixed-state completed snapshot;
- an armed VSS checkpoint can be converted into a permanent pre-update snapshot after a driver change;
- old snapshots can be pruned without leaking permanent chunk storage;
- garbage collection cannot delete content referenced by a readable retained manifest;
- existing v1/v2 snapshots remain restorable;
- Windows CI and artifact publishing are green.
