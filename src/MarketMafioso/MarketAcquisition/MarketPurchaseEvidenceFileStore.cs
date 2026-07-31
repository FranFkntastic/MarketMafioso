using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketPurchaseEvidenceFileStore : IMarketPurchaseEvidenceStateStore
{
    private const int CurrentVersion = 2;
    private const int CurrentJournalVersion = 1;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string path;
    private readonly string backupPath;
    private readonly string journalPath;
    private MarketPurchaseEvidenceSnapshot? lastSnapshot;

    public MarketPurchaseEvidenceFileStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A purchase evidence state path is required.", nameof(path));
        this.path = path;
        backupPath = path + ".bak";
        journalPath = path + ".journal";
    }

    public MarketPurchaseEvidenceSnapshot? Load()
    {
        lock (gate)
        {
            lastSnapshot = LoadCore();
            return lastSnapshot;
        }
    }

    public void Save(MarketPurchaseEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            lastSnapshot ??= LoadCore();
            if (lastSnapshot is null)
            {
                WriteMirroredBase(snapshot);
                lastSnapshot = snapshot;
                return;
            }

            if (snapshot.Revision != lastSnapshot.Revision + 1)
                throw new InvalidDataException(
                    $"Purchase evidence revision must advance exactly once from {lastSnapshot.Revision} to {lastSnapshot.Revision + 1}.");

            AppendJournal(ToJournalEntry(lastSnapshot, snapshot));
            lastSnapshot = snapshot;
        }
    }

    private MarketPurchaseEvidenceSnapshot? LoadCore()
    {
        var primary = LoadCandidate(path);
        var backup = LoadCandidate(backupPath);
        var baseline = primary switch
        {
            null => backup,
            _ when backup is null || primary.Revision >= backup.Revision => primary,
            _ => backup,
        };
        if (baseline is null)
        {
            if (File.Exists(journalPath) && new FileInfo(journalPath).Length > 0)
                throw new InvalidDataException(
                    "Purchase evidence journal exists without a recoverable base checkpoint.");
            return null;
        }

        var recovered = ReplayJournal(baseline);
        if (File.Exists(journalPath) || primary is null || primary.Revision != recovered.Revision)
        {
            WriteMirroredBase(recovered);
            File.Delete(journalPath);
        }

        return recovered;
    }

    private void WriteMirroredBase(MarketPurchaseEvidenceSnapshot snapshot)
    {
        var document = ToDocument(snapshot);
        AtomicJsonFile.Write(path, document, JsonOptions);
        try
        {
            AtomicJsonFile.Write(backupPath, document, JsonOptions);
        }
        catch (IOException)
        {
            // The primary atomic checkpoint is authoritative. A later load repairs the mirror.
        }
        catch (UnauthorizedAccessException)
        {
            // The primary atomic checkpoint is authoritative. A later load repairs the mirror.
        }
    }

    private void AppendJournal(JournalEntry entry)
    {
        var fullPath = Path.GetFullPath(journalPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        using var stream = new FileStream(
            fullPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        TrimUncommittedTrailingFragment(stream);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(payload);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static void TrimUncommittedTrailingFragment(FileStream stream)
    {
        if (stream.Length == 0)
            return;

        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n')
            return;

        for (var offset = stream.Length - 2; offset >= 0; offset--)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            if (stream.ReadByte() != '\n')
                continue;
            stream.SetLength(offset + 1);
            stream.Flush(flushToDisk: true);
            return;
        }

        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
    }

    private MarketPurchaseEvidenceSnapshot ReplayJournal(MarketPurchaseEvidenceSnapshot baseline)
    {
        if (!File.Exists(journalPath))
            return baseline;

        var text = File.ReadAllText(journalPath, Utf8WithoutBom);
        var hasTerminatedFinalRecord = text.EndsWith('\n');
        var lines = text.Split('\n');
        var current = baseline;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (index == lines.Length - 1 && !hasTerminatedFinalRecord)
                break;

            JournalEntry entry;
            try
            {
                entry = JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions)
                    ?? throw new InvalidDataException("Purchase evidence journal entry is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Purchase evidence journal contains a corrupt committed entry.", exception);
            }

            if (entry.Version != CurrentJournalVersion)
                throw new InvalidDataException($"Unsupported purchase evidence journal version {entry.Version}.");
            if (entry.Revision <= current.Revision)
                continue;
            if (entry.Revision != current.Revision + 1)
                throw new InvalidDataException(
                    $"Purchase evidence journal skips revision {current.Revision + 1}.");
            current = ApplyJournalEntry(current, entry);
        }

        return current;
    }

    private static JournalEntry ToJournalEntry(
        MarketPurchaseEvidenceSnapshot previous,
        MarketPurchaseEvidenceSnapshot next) => new()
    {
        Version = CurrentJournalVersion,
        Revision = next.Revision,
        State = next.State is null ? null : ToStateDocument(next.State),
        ObservationCount = next.Observations.Count,
        AppendedObservations = FindAppended(previous.Observations, next.Observations).ToList(),
        HistoryCount = next.History.Count,
        AppendedHistory = FindAppended(previous.History, next.History)
            .Select(entry => new HistoryDocument
            {
                State = ToStateDocument(entry.TerminalState),
                Disposition = entry.Disposition,
                ResolvedAtUtc = entry.ResolvedAtUtc,
                Resolution = entry.Resolution,
            })
            .ToList(),
    };

    private static MarketPurchaseEvidenceSnapshot ApplyJournalEntry(
        MarketPurchaseEvidenceSnapshot current,
        JournalEntry entry)
    {
        if (entry.ObservationCount < 0 ||
            entry.ObservationCount > MarketPurchaseEvidenceCoordinator.MaxObservationHistory ||
            entry.HistoryCount < 0 ||
            entry.HistoryCount > MarketPurchaseEvidenceCoordinator.MaxResolvedAttemptHistory)
            throw new InvalidDataException("Purchase evidence journal entry exceeds its bounded schema.");

        var observations = current.Observations
            .Concat(entry.AppendedObservations)
            .TakeLast(entry.ObservationCount)
            .ToList();
        var history = current.History
            .Concat(entry.AppendedHistory.Select(FromHistoryDocument))
            .TakeLast(entry.HistoryCount)
            .ToList();
        if (observations.Count != entry.ObservationCount || history.Count != entry.HistoryCount)
            throw new InvalidDataException("Purchase evidence journal entry cannot reconstruct its declared state.");

        return FromDocument(new Document
        {
            Version = CurrentVersion,
            Revision = entry.Revision,
            State = entry.State,
            Observations = observations,
            History = history.Select(ToHistoryDocument).ToList(),
        });
    }

    private static IReadOnlyList<T> FindAppended<T>(IReadOnlyList<T> previous, IReadOnlyList<T> next)
    {
        for (var overlap = Math.Min(previous.Count, next.Count); overlap >= 0; overlap--)
        {
            if (previous.Skip(previous.Count - overlap).SequenceEqual(next.Take(overlap)))
                return next.Skip(overlap).ToArray();
        }

        throw new InvalidDataException("Purchase evidence history is not append-only.");
    }

    private static MarketPurchaseEvidenceSnapshot? LoadCandidate(string candidatePath)
    {
        if (!File.Exists(candidatePath))
            return null;
        try
        {
            var document = AtomicJsonFile.Read<Document>(candidatePath, JsonOptions)
                ?? throw new InvalidDataException("Purchase evidence state is empty.");
            return FromDocument(document);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static Document ToDocument(MarketPurchaseEvidenceSnapshot snapshot) => new()
    {
        Version = CurrentVersion,
        Revision = snapshot.Revision,
        State = snapshot.State is null ? null : ToStateDocument(snapshot.State),
        Observations = snapshot.Observations.ToList(),
        History = snapshot.History.Select(entry => new HistoryDocument
        {
            State = ToStateDocument(entry.TerminalState),
            Disposition = entry.Disposition,
            ResolvedAtUtc = entry.ResolvedAtUtc,
            Resolution = entry.Resolution,
        }).ToList(),
    };

    private static MarketPurchaseEvidenceSnapshot FromDocument(Document document)
    {
        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported purchase evidence state version {document.Version}.");
        if (document.Revision < 0 || document.Observations.Count > MarketPurchaseEvidenceCoordinator.MaxObservationHistory ||
            document.History.Count > MarketPurchaseEvidenceCoordinator.MaxResolvedAttemptHistory)
            throw new InvalidDataException("Purchase evidence state exceeds its bounded schema.");

        var state = document.State is null ? null : FromStateDocument(document.State);
        var history = document.History.Select(FromHistoryDocument).ToArray();
        return new MarketPurchaseEvidenceSnapshot
        {
            Revision = document.Revision,
            State = state,
            Observations = document.Observations.ToArray(),
            History = history,
        };
    }

    private static HistoryDocument ToHistoryDocument(MarketPurchaseEvidenceHistoryEntry entry) => new()
    {
        State = ToStateDocument(entry.TerminalState),
        Disposition = entry.Disposition,
        ResolvedAtUtc = entry.ResolvedAtUtc,
        Resolution = entry.Resolution,
    };

    private static MarketPurchaseEvidenceHistoryEntry FromHistoryDocument(HistoryDocument entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Resolution))
            throw new InvalidDataException("Purchase evidence history has no resolution.");
        var terminal = FromStateDocument(entry.State);
        if (terminal is PendingMarketPurchase)
            throw new InvalidDataException("Purchase evidence history contains a pending intent.");
        if (!Enum.IsDefined(entry.Disposition) ||
            entry.Disposition == MarketPurchaseTerminalDisposition.AppliedExactlyOnce &&
            terminal is not ConfirmedMarketPurchase)
            throw new InvalidDataException("Purchase evidence history has an invalid terminal disposition.");
        return new MarketPurchaseEvidenceHistoryEntry(
            terminal,
            entry.Disposition,
            entry.ResolvedAtUtc,
            entry.Resolution);
    }

    private static StateDocument ToStateDocument(MarketPurchaseEvidenceState state) => new()
    {
        Kind = state.Kind,
        Intent = state.Intent,
        Evidence = state is ConfirmedMarketPurchase confirmed ? confirmed.Evidence
            : state is ConflictingMarketPurchasePacket conflicting ? conflicting.Evidence : null,
        TimedOutAtUtc = state is TimedOutIndeterminateMarketPurchase timedOut ? timedOut.TimedOutAtUtc : null,
        PendingPhase = state is PendingMarketPurchase pending ? pending.Phase : null,
        ConfirmationSubmittedAtUtc = state is PendingMarketPurchase submitted ? submitted.ConfirmationSubmittedAtUtc : null,
    };

    private static MarketPurchaseEvidenceState FromStateDocument(StateDocument document)
    {
        if (document.Intent is null)
            throw new InvalidDataException("Purchase evidence state has no intent.");
        MarketPurchaseEvidenceState state = document.Kind switch
        {
            MarketPurchaseEvidenceStateKind.Pending => new PendingMarketPurchase(
                document.Intent,
                document.PendingPhase ?? PendingMarketPurchasePhase.ArmedBeforeConfirmation,
                document.ConfirmationSubmittedAtUtc),
            MarketPurchaseEvidenceStateKind.Confirmed when document.Evidence is not null =>
                new ConfirmedMarketPurchase(document.Intent, document.Evidence),
            MarketPurchaseEvidenceStateKind.TimedOutIndeterminate when document.TimedOutAtUtc is not null =>
                new TimedOutIndeterminateMarketPurchase(document.Intent, document.TimedOutAtUtc.Value),
            MarketPurchaseEvidenceStateKind.ConflictingPacket when document.Evidence is not null =>
                new ConflictingMarketPurchasePacket(document.Intent, document.Evidence),
            _ => throw new InvalidDataException("Purchase evidence state is incomplete."),
        };
        ValidateState(state);
        return state;
    }

    private static void ValidateState(MarketPurchaseEvidenceState state)
    {
        var intent = state.Intent;
        if (string.IsNullOrWhiteSpace(intent.IntentId) || string.IsNullOrWhiteSpace(intent.RouteId) ||
            string.IsNullOrWhiteSpace(intent.RouteRunId) || string.IsNullOrWhiteSpace(intent.AttemptId) ||
            string.IsNullOrWhiteSpace(intent.LineId) || string.IsNullOrWhiteSpace(intent.ListingId) ||
            string.IsNullOrWhiteSpace(intent.WorldName) || string.IsNullOrWhiteSpace(intent.PacketFloor.Epoch) ||
            intent.PacketFloor.Sequence < 0 || intent.ItemId == 0 || intent.Quantity == 0 ||
            intent.UnitPrice == 0 || intent.WorldId == 0 || (ulong)intent.UnitPrice * intent.Quantity != intent.TotalGil ||
            intent.DeadlineUtc <= intent.ArmedAtUtc)
            throw new InvalidDataException("Purchase evidence intent is invalid.");

        if (state is PendingMarketPurchase pending)
        {
            if (!Enum.IsDefined(pending.Phase) ||
                pending.Phase == PendingMarketPurchasePhase.ArmedBeforeConfirmation && pending.ConfirmationSubmittedAtUtc is not null ||
                pending.Phase == PendingMarketPurchasePhase.ConfirmationSubmitted &&
                (pending.ConfirmationSubmittedAtUtc is not DateTimeOffset submittedAtUtc ||
                 submittedAtUtc < intent.ArmedAtUtc || submittedAtUtc > intent.DeadlineUtc))
                throw new InvalidDataException("Pending purchase evidence has an invalid submission phase.");
            return;
        }

        if (state is TimedOutIndeterminateMarketPurchase timedOut)
        {
            if (timedOut.TimedOutAtUtc != intent.DeadlineUtc)
                throw new InvalidDataException("Indeterminate purchase evidence has an invalid deadline.");
            return;
        }

        var evidence = state switch
        {
            ConfirmedMarketPurchase confirmed => confirmed.Evidence,
            ConflictingMarketPurchasePacket conflicting => conflicting.Evidence,
            _ => throw new InvalidDataException("Purchase evidence state is unsupported."),
        };
        if (!evidence.Position.IsAfter(intent.PacketFloor))
            throw new InvalidDataException("Terminal packet evidence does not follow the intent floor.");
        if (state is ConfirmedMarketPurchase &&
            (evidence.ObservedAtUtc < intent.ArmedAtUtc || evidence.ObservedAtUtc > intent.DeadlineUtc ||
             evidence.ItemId != intent.ItemId || evidence.IsHighQuality != intent.IsHighQuality ||
             evidence.Quantity != intent.Quantity))
            throw new InvalidDataException("Confirmed packet evidence does not match its purchase intent.");
    }

    private sealed record Document
    {
        public int Version { get; init; }
        public long Revision { get; init; }
        public StateDocument? State { get; init; }
        public List<MarketPurchasePacketObservation> Observations { get; init; } = [];
        public List<HistoryDocument> History { get; init; } = [];
    }

    private sealed record StateDocument
    {
        public MarketPurchaseEvidenceStateKind Kind { get; init; }
        public MarketPurchaseIntent? Intent { get; init; }
        public MarketPurchasePacketObservation? Evidence { get; init; }
        public DateTimeOffset? TimedOutAtUtc { get; init; }
        public PendingMarketPurchasePhase? PendingPhase { get; init; }
        public DateTimeOffset? ConfirmationSubmittedAtUtc { get; init; }
    }

    private sealed record HistoryDocument
    {
        public StateDocument State { get; init; } = new();
        public MarketPurchaseTerminalDisposition Disposition { get; init; }
        public DateTimeOffset ResolvedAtUtc { get; init; }
        public string Resolution { get; init; } = string.Empty;
    }

    private sealed record JournalEntry
    {
        public int Version { get; init; }
        public long Revision { get; init; }
        public StateDocument? State { get; init; }
        public int ObservationCount { get; init; }
        public List<MarketPurchasePacketObservation> AppendedObservations { get; init; } = [];
        public int HistoryCount { get; init; }
        public List<HistoryDocument> AppendedHistory { get; init; } = [];
    }
}
