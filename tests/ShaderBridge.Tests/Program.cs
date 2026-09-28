using ShaderBridge.Models;
using ShaderBridge.Services;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] Pattern(int length, byte seed)
{
    var data = new byte[length];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(seed + i % 251);
    return data;
}

var temp = Path.Combine(Path.GetTempPath(), "ShaderBridge.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var store = Path.Combine(temp, "store");
    var a = Path.Combine(temp, "a.bin");
    var b = Path.Combine(temp, "b.bin");
    var restored = Path.Combine(temp, "restored.bin");

    var bytesA = Pattern(ChunkStore.ChunkSize * 2 + 123, 7);
    await File.WriteAllBytesAsync(a, bytesA);

    var bytesB = (byte[])bytesA.Clone();
    bytesB[ChunkStore.ChunkSize + 17] ^= 0x7F;
    await File.WriteAllBytesAsync(b, bytesB);

    var chunkStore = new ChunkStore();
    var chunksA = await chunkStore.StoreFileAsync(a, store);
    var statsAfterA = chunkStore.GetStats(store);
    var chunksB = await chunkStore.StoreFileAsync(b, store);
    var statsAfterB = chunkStore.GetStats(store);

    Assert(chunksA.Count == 3, "Expected three chunks for first test file.");
    Assert(chunksB.Count == 3, "Expected three chunks for second test file.");
    Assert(statsAfterB.ChunkCount == statsAfterA.ChunkCount + 1,
        $"Only one changed chunk should be added. Before={statsAfterA.ChunkCount}, after={statsAfterB.ChunkCount}.");

    await chunkStore.RehydrateAsync(chunksA, restored, store);
    Assert(File.ReadAllBytes(restored).SequenceEqual(bytesA), "Chunk rehydration did not reproduce original bytes.");

    var empty = Path.Combine(temp, "empty.bin");
    var emptyRestored = Path.Combine(temp, "empty-restored.bin");
    await File.WriteAllBytesAsync(empty, []);
    var emptyChunks = await chunkStore.StoreFileAsync(empty, store);
    Assert(emptyChunks.Count == 0, "Empty file should have zero data chunks.");
    await chunkStore.RehydrateAsync(emptyChunks, emptyRestored, store);
    Assert(File.Exists(emptyRestored) && new FileInfo(emptyRestored).Length == 0, "Empty file did not round-trip.");

    Assert(VssCheckpointService.GetVolumeRoot(@"C:\Users\Test\cache.bin") == @"C:\",
        "Local drive volume extraction failed.");
    Assert(VssCheckpointService.GetVolumeRoot(@"\\server\share\cache.bin") is null,
        "UNC paths must not be accepted for VSS volume mapping.");

    var mapped = VssCheckpointService.MapPathToShadow(
        @"C:\Users\Test\cache.bin",
        @"C:\",
        @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy42\");
    Assert(mapped == @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy42\Users\Test\cache.bin",
        $"Unexpected mapped shadow path: {mapped}");

    var mismatchRejected = false;
    try
    {
        _ = VssCheckpointService.MapPathToShadow(
            @"D:\Games\cache.bin",
            @"C:\",
            @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy42\");
    }
    catch (ArgumentException) { mismatchRejected = true; }
    Assert(mismatchRejected, "Volume mismatch must be rejected.");

    Console.WriteLine("ShaderBridge storage tests passed.");
}
finally
{
    try { Directory.Delete(temp, true); } catch { }
}
