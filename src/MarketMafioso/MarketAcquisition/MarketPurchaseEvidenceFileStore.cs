using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketPurchaseEvidenceFileStore : IMarketPurchaseEvidenceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly string backupPath;

    public MarketPurchaseEvidenceFileStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A state path is required.", nameof(path));
        this.path = path;
        backupPath = path + ".bak";
    }

    public MarketPurchaseEvidenceSnapshot? Load() => LoadCandidate(path) ?? LoadCandidate(backupPath);

    public void Save(MarketPurchaseEvidenceSnapshot snapshot)
    {
        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: true);
        AtomicJsonFile.Write(path, ToDocument(snapshot), JsonOptions);
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

    private static Document ToDocument(MarketPurchaseEvidenceSnapshot snapshot)
    {
        var state = snapshot.State;
        return new Document
        {
            Version = 1,
            Kind = state?.Kind,
            Intent = state?.Intent,
            Evidence = state is Confirmed confirmed ? confirmed.Evidence
                : state is ConflictingPacket conflicting ? conflicting.Evidence : null,
            TimedOutAtUtc = state is TimedOutIndeterminate timedOut ? timedOut.TimedOutAtUtc : null,
            Observations = [.. snapshot.Observations],
        };
    }

    private static MarketPurchaseEvidenceSnapshot FromDocument(Document document)
    {
        if (document.Version != 1)
            throw new InvalidDataException($"Unsupported purchase evidence state version {document.Version}.");
        MarketPurchaseEvidenceState? state = document.Kind switch
        {
            null => null,
            MarketPurchaseEvidenceStateKind.Pending when document.Intent is not null => new Pending(document.Intent),
            MarketPurchaseEvidenceStateKind.Confirmed when document.Intent is not null && document.Evidence is not null => new Confirmed(document.Intent, document.Evidence),
            MarketPurchaseEvidenceStateKind.TimedOutIndeterminate when document.Intent is not null && document.TimedOutAtUtc is not null => new TimedOutIndeterminate(document.Intent, document.TimedOutAtUtc.Value),
            MarketPurchaseEvidenceStateKind.ConflictingPacket when document.Intent is not null && document.Evidence is not null => new ConflictingPacket(document.Intent, document.Evidence),
            _ => throw new InvalidDataException("Purchase evidence state is incomplete."),
        };
        return new MarketPurchaseEvidenceSnapshot { State = state, Observations = document.Observations.ToArray() };
    }

    private sealed record Document
    {
        public int Version { get; init; }
        public MarketPurchaseEvidenceStateKind? Kind { get; init; }
        public MarketPurchaseIntent? Intent { get; init; }
        public MarketPurchasePacketObservation? Evidence { get; init; }
        public DateTimeOffset? TimedOutAtUtc { get; init; }
        public List<MarketPurchasePacketObservation> Observations { get; init; } = [];
    }
}
