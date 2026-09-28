using System.Security.Cryptography;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed record ChunkStoreStats(long ChunkCount, long Bytes);
public sealed record ChunkStoreResult(string FullSha256, long Size, IReadOnlyList<SnapshotChunk> Chunks);

public sealed class ChunkStore
{
    public const int ChunkSize = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<SnapshotChunk>> StoreFileAsync(
        string sourcePath,
        string storeRoot,
        CancellationToken ct = default) =>
        (await StoreFileDetailedAsync(sourcePath, storeRoot, ct)).Chunks;

    public async Task<ChunkStoreResult> StoreFileDetailedAsync(
        string sourcePath,
        string storeRoot,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ChunkRoot(storeRoot));
        var chunks = new List<SnapshotChunk>();
        long total = 0;
        using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[ChunkSize];
        while (true)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await input.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
                if (n == 0) break;
                read += n;
            }

            if (read == 0) break;

            fullHash.AppendData(buffer, 0, read);
            total += read;
            var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
            await EnsureChunkAsync(storeRoot, hash, buffer.AsMemory(0, read), ct);
            chunks.Add(new SnapshotChunk { Sha256 = hash, Size = read });

            if (read < buffer.Length) break;
        }

        return new ChunkStoreResult(Convert.ToHexString(fullHash.GetHashAndReset()), total, chunks);
    }

    public async Task RehydrateAsync(
        IReadOnlyList<SnapshotChunk> chunks,
        string destinationPath,
        string storeRoot,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".shaderbridge-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var chunk in chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = ChunkPath(storeRoot, chunk.Sha256);
                    if (!File.Exists(source))
                        throw new FileNotFoundException($"Missing snapshot chunk {chunk.Sha256}.", source);

                    await using var input = new FileStream(
                        source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != chunk.Size)
                        throw new InvalidDataException($"Snapshot chunk {chunk.Sha256} has unexpected size.");

                    await input.CopyToAsync(output, ChunkSize, ct);
                }
                await output.FlushAsync(ct);
            }

            File.Move(temp, destinationPath, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public ChunkStoreStats GetStats(string storeRoot)
    {
        var root = ChunkRoot(storeRoot);
        if (!Directory.Exists(root)) return new ChunkStoreStats(0, 0);
        long count = 0, bytes = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                count++;
                bytes += new FileInfo(file).Length;
            }
            catch { }
        }
        return new ChunkStoreStats(count, bytes);
    }

    private static async Task EnsureChunkAsync(
        string storeRoot,
        string hash,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        var destination = ChunkPath(storeRoot, hash);
        if (File.Exists(destination)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, data.ToArray(), ct);
            try { File.Move(temp, destination, false); }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temp);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string ChunkRoot(string storeRoot) => Path.Combine(storeRoot, "chunks");
    private static string ChunkPath(string storeRoot, string hash) =>
        Path.Combine(ChunkRoot(storeRoot), hash[..2], hash);
}
