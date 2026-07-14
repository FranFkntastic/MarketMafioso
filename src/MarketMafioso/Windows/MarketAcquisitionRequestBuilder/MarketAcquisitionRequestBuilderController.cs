using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed class MarketAcquisitionRequestBuilderController
{
    private readonly Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest;
    private readonly Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest;
    private readonly Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted;
    private readonly Action<MarketAcquisitionRequestDocument> persistDocument;

    public MarketAcquisitionRequestBuilderController(
        Configuration config,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted)
        : this(
            MarketAcquisitionRequestDocumentPersistence.Restore(config),
            syncRequest,
            refreshRequest,
            documentAdopted,
            CreatePersistence(config))
    {
    }

    internal MarketAcquisitionRequestBuilderController(
        MarketAcquisitionRequestDocument document,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted,
        Action<MarketAcquisitionRequestDocument> persistDocument)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        this.syncRequest = syncRequest ?? throw new ArgumentNullException(nameof(syncRequest));
        this.refreshRequest = refreshRequest ?? throw new ArgumentNullException(nameof(refreshRequest));
        this.documentAdopted = documentAdopted ?? throw new ArgumentNullException(nameof(documentAdopted));
        this.persistDocument = persistDocument ?? throw new ArgumentNullException(nameof(persistDocument));
    }

    public MarketAcquisitionRequestDocument Document { get; private set; }

    public MarketAcquisitionRequestDocument? PendingRemoteDocument { get; private set; }

    public MarketAcquisitionRequestView? PendingRemoteRequest { get; private set; }

    public int SelectedLineIndex { get; private set; } = -1;

    public string Status { get; private set; } = "Request builder ready.";

    public bool IsSyncing { get; private set; }

    public bool IsRefreshing { get; private set; }

    public string CurrentIntentHash => MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(Document);

    public void SetStatus(string status) => Status = status;

    public void MarkPlanPrepared(string planHash)
    {
        Document = Document with { LastPlanHash = planHash, UpdatedAtUtc = DateTimeOffset.UtcNow };
        SaveDocument();
    }

    public void AdoptRequest(MarketAcquisitionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Document = MarketAcquisitionRequestDocumentMapper.FromRequestView(request);
        ResetRemoteAndSelection();
        Status = "Loaded request into builder.";
        SaveDocument();
        documentAdopted(Document, request);
    }

    public bool AdoptRestoredRequestIfSafe(MarketAcquisitionRequestView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShouldAdoptRestoredRequest(request))
            return false;

        Document = MarketAcquisitionRequestDocumentMapper.FromRequestView(request);
        ResetRemoteAndSelection();
        Status = "Loaded restored request into builder.";
        SaveDocument();
        return true;
    }

    public void EnsureCharacterScope(string characterName, string world)
    {
        if (string.Equals(Document.TargetCharacterName, characterName, StringComparison.Ordinal) &&
            string.Equals(Document.TargetWorld, world, StringComparison.Ordinal))
        {
            return;
        }

        Document = Document with
        {
            TargetCharacterName = characterName,
            TargetWorld = world,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        SaveDocument();
    }

    public void UpdateRouteScope(RequestRouteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CommitLocalEdit(RequestDocumentMutation.ApplyRouteScope(Document, scope));
    }

    public bool SelectLine(int index)
    {
        if (index < 0 || index >= Document.Lines.Count)
            return false;

        SelectedLineIndex = index;
        return true;
    }

    public void ClearSelection() => SelectedLineIndex = -1;

    public bool SetLineMaxUnitPrice(int index, uint maxUnitPrice, string message)
    {
        if (!SelectLine(index))
            return false;

        CommitLocalEdit(RequestDocumentMutation.ApplyMaxUnitPrice(Document, index, maxUnitPrice), message);
        return true;
    }

    public bool SetLineGilCap(int index, uint gilCap, string message)
    {
        if (!SelectLine(index))
            return false;

        var line = Document.Lines[index];
        CommitLocalEdit(RequestDocumentMutation.ApplyPricing(Document, index, line.MaxUnitPrice, gilCap), message);
        return true;
    }

    public bool ApplyLineEdit(
        int index,
        string quantityMode,
        uint targetQuantity,
        uint maxQuantity,
        string hqPolicy,
        uint maxUnitPrice,
        uint gilCap,
        string message)
    {
        if (!SelectLine(index))
            return false;

        CommitLocalEdit(
            RequestDocumentMutation.ApplyLineEdit(
                Document,
                index,
                quantityMode,
                targetQuantity,
                maxQuantity,
                hqPolicy,
                maxUnitPrice,
                gilCap),
            message);
        return true;
    }

    public void ApplyEditorLine(MarketAcquisitionRequestLineDocument line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (SelectedLineIndex >= 0 && SelectedLineIndex < Document.Lines.Count)
        {
            CommitLocalEdit(RequestDocumentMutation.ReplaceLine(Document, SelectedLineIndex, line), "Local request updated.");
            return;
        }

        Document = RequestDocumentMutation.AddLine(Document, line);
        SelectedLineIndex = -1;
        FinishLocalEdit("Local request updated.");
    }

    public int AddLines(IEnumerable<MarketAcquisitionRequestLineDocument> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var added = 0;
        foreach (var line in lines)
        {
            if (line.ItemId == 0 || Document.Lines.Any(existing => existing.ItemId == line.ItemId))
                continue;
            Document = RequestDocumentMutation.AddLine(Document, line);
            added++;
        }
        if (added == 0)
        {
            Status = "Outfitter items are already present in the local request.";
            return 0;
        }
        SelectedLineIndex = -1;
        FinishLocalEdit($"Added {added:N0} Outfitter line{(added == 1 ? string.Empty : "s")}.");
        return added;
    }

    public bool RemoveLine(int index)
    {
        if (index < 0 || index >= Document.Lines.Count)
            return false;

        Document = RequestDocumentMutation.RemoveLine(Document, index);
        SelectedLineIndex = -1;
        FinishLocalEdit("Line removed.");
        return true;
    }

    public async Task SyncAsync(string characterName, string world)
    {
        if (IsSyncing || IsRefreshing)
            return;

        IsSyncing = true;
        try
        {
            var operationDocument = Document;
            var scopedDocument = operationDocument with
            {
                TargetCharacterName = characterName,
                TargetWorld = world,
            };
            var outcome = await syncRequest(scopedDocument).ConfigureAwait(false);
            if (ReferenceEquals(Document, operationDocument))
            {
                Document = outcome.Document;
                ResetRemoteAndSelection();
                Status = outcome.StatusMessage;
            }
            else
            {
                Document = MergeSyncMetadata(Document, outcome.Document);
                PendingRemoteDocument = null;
                PendingRemoteRequest = null;
                Status = $"{outcome.StatusMessage} Local edits made during sync were preserved.";
            }

            SaveDocument();
        }
        catch (Exception ex)
        {
            Document = Document with { SyncStatus = "SyncFailed", UpdatedAtUtc = DateTimeOffset.UtcNow };
            Status = $"Sync failed: {ex.Message}";
            SaveDocument();
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing || IsSyncing)
            return;

        IsRefreshing = true;
        try
        {
            var operationDocument = Document;
            var outcome = await refreshRequest(operationDocument).ConfigureAwait(false);
            if (ReferenceEquals(Document, operationDocument))
            {
                Document = outcome.Document;
                PendingRemoteDocument = outcome.RemoteDocument;
                PendingRemoteRequest = outcome.RemoteRequest;
                Status = outcome.StatusMessage;
            }
            else
            {
                var remoteDocument = outcome.RemoteDocument ?? outcome.Document;
                Document = MergeRemoteMetadata(Document, remoteDocument);
                PendingRemoteDocument = remoteDocument;
                PendingRemoteRequest = outcome.RemoteRequest;
                Status = $"{outcome.StatusMessage} Local edits made during refresh were preserved.";
            }

            SaveDocument();
        }
        catch (Exception ex)
        {
            Status = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public bool AdoptRemote()
    {
        if (PendingRemoteDocument is null)
            return false;

        Document = PendingRemoteDocument;
        var remote = PendingRemoteRequest;
        ResetRemoteAndSelection();
        Status = "Loaded server copy.";
        SaveDocument();
        documentAdopted(Document, remote);
        return true;
    }

    public void ClearDraft(string characterName, string world)
    {
        Document = MarketAcquisitionRequestDocument.CreateDefault(characterName, world);
        ResetRemoteAndSelection();
        Status = "Local request cleared.";
        SaveDocument();
    }

    private void CommitLocalEdit(MarketAcquisitionRequestDocument updated, string? message = null)
    {
        Document = updated;
        FinishLocalEdit(message);
    }

    private void FinishLocalEdit(string? message)
    {
        PendingRemoteDocument = null;
        PendingRemoteRequest = null;
        if (message is not null)
            Status = message;
        SaveDocument();
    }

    private void ResetRemoteAndSelection()
    {
        PendingRemoteDocument = null;
        PendingRemoteRequest = null;
        SelectedLineIndex = -1;
    }

    private bool ShouldAdoptRestoredRequest(MarketAcquisitionRequestView request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            return false;

        if (string.IsNullOrWhiteSpace(Document.RemoteRequestId))
            return true;

        if (!string.Equals(Document.RemoteRequestId, request.Id, StringComparison.Ordinal))
            return true;

        if (Document.SyncStatus.Equals("LocalEdits", StringComparison.OrdinalIgnoreCase) ||
            Document.SyncStatus.Equals("RemoteChanged", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Document.Lines.Count == 0 ||
               Document.RemoteRevision != request.Revision ||
               !Document.SyncStatus.Equals("SyncedClean", StringComparison.OrdinalIgnoreCase);
    }

    private void SaveDocument() => persistDocument(Document);

    private static MarketAcquisitionRequestDocument MergeSyncMetadata(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestDocument synced) =>
        current with
        {
            RemoteRequestId = synced.RemoteRequestId,
            RemoteRevision = synced.RemoteRevision,
            RemoteOrigin = synced.RemoteOrigin,
            LastSyncedHash = synced.LastSyncedHash,
            RemoteHash = synced.RemoteHash,
            SyncStatus = "LocalEdits",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static MarketAcquisitionRequestDocument MergeRemoteMetadata(
        MarketAcquisitionRequestDocument current,
        MarketAcquisitionRequestDocument remote) =>
        current with
        {
            RemoteRequestId = remote.RemoteRequestId,
            RemoteRevision = remote.RemoteRevision,
            RemoteOrigin = remote.RemoteOrigin,
            RemoteHash = remote.RemoteHash,
            SyncStatus = "RemoteChanged",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static Action<MarketAcquisitionRequestDocument> CreatePersistence(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return document =>
        {
            MarketAcquisitionRequestDocumentPersistence.Save(config, document);
            config.Save();
        };
    }
}
