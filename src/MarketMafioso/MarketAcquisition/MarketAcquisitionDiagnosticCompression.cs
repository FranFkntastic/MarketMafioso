using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MarketMafioso.MarketAcquisition;

public interface IMarketAcquisitionDiagnosticCompressor
{
    MarketAcquisitionDiagnosticCompressedFile Compress(string sourcePath);
}

public sealed class MarketAcquisitionGzipDiagnosticCompressor : IMarketAcquisitionDiagnosticCompressor
{
    public MarketAcquisitionDiagnosticCompressedFile Compress(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The diagnostic artifact to compress does not exist.", sourcePath);

        var targetPath = sourcePath + ".gz";
        var sourceIdentity = MeasureRaw(sourcePath);
        if (File.Exists(targetPath))
        {
            var existing = MeasureGzip(targetPath);
            if (existing.ByteLength != sourceIdentity.ByteLength ||
                !existing.Sha256.Equals(sourceIdentity.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Existing compressed artifact '{targetPath}' does not match '{sourcePath}'.");
            }

            return BuildResult(sourcePath, targetPath, sourceIdentity);
        }

        var temporaryPath = targetPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false))
            {
                input.CopyTo(gzip);
            }

            var compressedIdentity = MeasureGzip(temporaryPath);
            if (compressedIdentity.ByteLength != sourceIdentity.ByteLength ||
                !compressedIdentity.Sha256.Equals(sourceIdentity.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Compressed diagnostic artifact '{temporaryPath}' failed lossless verification.");
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
            return BuildResult(sourcePath, targetPath, sourceIdentity);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static MarketAcquisitionDiagnosticCompressedFile BuildResult(
        string sourcePath,
        string targetPath,
        FileIdentity sourceIdentity) =>
        new()
        {
            SourceFileName = Path.GetFileName(sourcePath),
            StoredFileName = Path.GetFileName(targetPath),
            ContentEncoding = "gzip",
            RawByteLength = sourceIdentity.ByteLength,
            RawSha256 = sourceIdentity.Sha256,
            StoredByteLength = new FileInfo(targetPath).Length,
            StoredSha256 = HashFile(targetPath),
        };

    private static FileIdentity MeasureRaw(string path) =>
        new(new FileInfo(path).Length, HashFile(path));

    private static FileIdentity MeasureGzip(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long byteLength = 0;
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            byteLength += read;
        }

        return new FileIdentity(byteLength, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record FileIdentity(long ByteLength, string Sha256);
}

public sealed record MarketAcquisitionDiagnosticCompressedFile
{
    public required string SourceFileName { get; init; }
    public required string StoredFileName { get; init; }
    public required string ContentEncoding { get; init; }
    public required long RawByteLength { get; init; }
    public required string RawSha256 { get; init; }
    public required long StoredByteLength { get; init; }
    public required string StoredSha256 { get; init; }
}
