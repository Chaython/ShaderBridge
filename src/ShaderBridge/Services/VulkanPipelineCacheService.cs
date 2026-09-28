using System.Buffers.Binary;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class VulkanPipelineCacheService
{
    public VulkanCacheHeader ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[32];
        var read = stream.Read(header);
        if (read < 32) throw new InvalidDataException("File is too small to contain a Vulkan pipeline cache header.");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        var headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        var vendorId = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        var deviceId = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        var uuid = header[16..32].ToArray();

        if (headerSize < 32) throw new InvalidDataException($"Unexpected Vulkan header size: {headerSize}.");
        if (headerVersion != 1) throw new InvalidDataException($"Unsupported Vulkan pipeline cache header version: {headerVersion}.");
        return new VulkanCacheHeader(headerSize, headerVersion, vendorId, deviceId, uuid);
    }

    public async Task<string> CreateCandidateAsync(string sourcePath, VulkanDeviceInfo device, CancellationToken ct = default)
    {
        var header = ReadHeader(sourcePath);
        if (header.VendorId != device.VendorId || header.DeviceId != device.DeviceId)
            throw new InvalidOperationException(
                $"Cache targets {header.VendorHex}/{header.DeviceHex}, but selected Vulkan device is {device.VendorHex}/{device.DeviceHex}.");

        var dir = Path.GetDirectoryName(sourcePath)!;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        var destination = Path.Combine(dir, $"{name}.shaderbridge-candidate{ext}");
        var suffix = 1;
        while (File.Exists(destination))
            destination = Path.Combine(dir, $"{name}.shaderbridge-candidate-{suffix++}{ext}");

        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, 1024 * 1024, ct);
            output.Position = 16;
            await output.WriteAsync(device.PipelineCacheUuid, ct);
            await output.FlushAsync(ct);
        }
        return destination;
    }
}
