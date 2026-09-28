# ShaderBridge

**Carry your shader work across GPU driver updates.**

ShaderBridge is a Windows shader-cache preservation and migration utility. It stays completely outside game processes: no DLL proxying, injection, API hooks, debugger attachment, kernel driver, or anti-cheat interaction.

## MVP features

- Detects installed display-driver versions from Windows' display-device registry class.
- Discovers common NVIDIA, AMD, Intel, Windows D3D and Steam shader-cache roots.
- Supports custom cache directories.
- Creates 4 MiB content-addressed SHA-256 chunks, so unchanged regions inside large cache databases are stored only once.
- Compares a prior snapshot with the current on-disk caches (unchanged / modified / missing / new).
- Conservatively restores **missing game/custom files only**; known driver-native and Windows D3D caches are skipped after driver changes.
- Watches discovered cache directories while ShaderBridge is running and logs creates/deletes/renames/changes.
- Polls for GPU-driver fingerprint changes and raises a tray notification.
- Reads Vulkan `VkPipelineCacheHeaderVersionOne` headers.
- Queries the current Vulkan loader/device for vendor ID, device ID and `pipelineCacheUUID`.
- Experimental Vulkan lab can create a **copy** of an old cache with the current `pipelineCacheUUID`. It never overwrites the original and does not claim that the implementation-defined payload became compatible.
- Automatic once-per-driver baseline snapshots (enabled by default).
- Optional short-lived VSS copy-on-write checkpoints around GPU-driver updates; live shader-cache writes remain fully writable.
- Optional start-with-Windows and minimize-to-tray operation.
- No third-party NuGet dependencies.

## Safety boundary

ShaderBridge intentionally does **not**:

- place `d3d12.dll`, `dxgi.dll`, `vulkan-1.dll`, etc. beside a game;
- inject or load code into game processes;
- patch executables or process memory;
- bypass or interact with anti-cheat software;
- blindly rewrite Direct3D 12 cached PSO blobs;
- deny write access to live shader-cache directories;
- automatically restore known driver-native caches after a driver update.

The Vulkan UUID migration feature is an experiment on an offline copy. A pipeline cache's payload is implementation-defined and may still be rejected by the new driver.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK to build
- Vulkan loader only if you want to use the Vulkan Lab

## Build

From PowerShell:

```powershell
./build.ps1
```

or:

```powershell
dotnet build .\ShaderBridge.sln -c Release
```

## Publish a portable EXE

```powershell
./publish.ps1
```

Output:

```text
artifacts\win-x64\ShaderBridge.exe
```

The publish script creates a self-contained Windows x64 single-file build, so the target PC does not need a separate .NET runtime installed.

## First-use workflow

1. Start ShaderBridge before your next GPU-driver update.
2. **Scan caches**.
3. **Create snapshot** while the old driver is installed.
4. Leave ShaderBridge running in the notification area if you want filesystem and driver-change logging.
5. Install the new GPU driver normally.
6. Open **Snapshots → Compare** to see exactly what was invalidated or rewritten.
7. Use **Restore missing** only for the conservative game/custom restore path.
8. For a Vulkan cache, use **Vulkan Lab → Inspect → Probe Vulkan → Create UUID-migrated candidate copy** if you want to test the header-spoofing hypothesis on a disposable copy.

## Storage layout

Default location:

```text
%LOCALAPPDATA%\ShaderBridge\Store\
├─ manifests\
│  └─ <snapshot-id>.json
└─ objects\
   ├─ 0A\
   │  └─ 0A...SHA256
   └─ FF\
      └─ FF...SHA256
```

Version-2 manifests preserve original paths, timestamps, cache categories, full-file hashes, ordered 4 MiB chunk hashes and driver metadata. The chunk store is deduplicated by SHA-256. Existing version-1 whole-file snapshots remain readable for restores.

## Driver-update checkpoint workflow

For the lowest temporary storage overhead around a driver update:

1. Scan caches.
2. Click **Arm driver update (VSS)** and approve the Windows elevation prompt.
3. Install the GPU driver normally. ShaderBridge does not deny or intercept writes; Windows VSS preserves overwritten disk blocks through copy-on-write.
4. Compare/create permanent chunked snapshots as needed.
5. Click **Release VSS checkpoint** when the transition is finished.

ShaderBridge records the exact shadow-copy IDs it created and only releases those IDs. VSS is optional: if it is unavailable, normal 4 MiB chunk-deduplicated snapshots continue to work.

## Current limitations / next steps

VSS checkpoints are volume-level and should be short-lived. Future work can import selected pre-update files directly from the VSS device view before release, add content-defined chunking, engine-specific cache parsers, Steam app-name resolution, and a compatibility history database across driver revisions.

## GitHub Actions build

The included `.github/workflows/build.yml` builds on `windows-latest` and uploads a self-contained `ShaderBridge-win-x64` artifact on pushes, pull requests, or manual workflow runs. CI artifacts can be downloaded from the corresponding GitHub Actions run.
