using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketAcquisitionSegmentedJsonlWriter : IDisposable
{
    internal const long DefaultSegmentSizeBytes = 8L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string directory;
    private readonly long segmentSizeBytes;
    private readonly List<MarketAcquisitionRouteDiagnosticTraceSegment> segments = [];
    private FileStream? stream;
    private IncrementalHash? hash;
    private string? fileName;
    private long firstSequence;
    private long lastSequence;
    private long bytesWritten;
    private int segmentOrdinal;
    private bool disposed;

    public MarketAcquisitionSegmentedJsonlWriter(
        string directory,
        long segmentSizeBytes = DefaultSegmentSizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (segmentSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentSizeBytes));

        this.directory = directory;
        this.segmentSizeBytes = segmentSizeBytes;
    }

    public IReadOnlyList<MarketAcquisitionRouteDiagnosticTraceSegment> Segments => segments;

    public void Write(long sequence, string json)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var bytes = Utf8NoBom.GetBytes(json + Environment.NewLine);
        if (stream != null && bytesWritten > 0 && bytesWritten + bytes.LongLength > segmentSizeBytes)
            CloseSegment();

        EnsureSegment(sequence);
        stream!.Write(bytes);
        hash!.AppendData(bytes);
        lastSequence = sequence;
        bytesWritten += bytes.LongLength;
    }

    public void Flush()
    {
        if (!disposed)
            stream?.Flush();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        CloseSegment();
        disposed = true;
    }

    private void EnsureSegment(long sequence)
    {
        if (stream != null)
            return;

        segmentOrdinal++;
        fileName = $"trace-{segmentOrdinal:0000}.jsonl";
        stream = new FileStream(
            Path.Combine(directory, fileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        firstSequence = sequence;
        lastSequence = sequence;
        bytesWritten = 0;
    }

    private void CloseSegment()
    {
        if (stream == null || hash == null || fileName == null)
            return;

        stream.Dispose();
        segments.Add(new MarketAcquisitionRouteDiagnosticTraceSegment
        {
            FileName = fileName,
            FirstSequence = firstSequence,
            LastSequence = lastSequence,
            ByteLength = bytesWritten,
            Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
        });
        hash.Dispose();
        stream = null;
        hash = null;
        fileName = null;
    }
}
