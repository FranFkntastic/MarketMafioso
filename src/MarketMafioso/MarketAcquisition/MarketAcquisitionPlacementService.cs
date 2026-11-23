using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>A reserve rule's identity in Quartermaster: the item and NqOnly or HqOnly.</summary>
public readonly record struct PlacementRuleKey(uint ItemId, string Quality);

/// <summary>
/// Quartermaster's placement matrix as last read: every reserve rule of the owner, plus the buy list's items that
/// have none. Owner is the character Quartermaster read it for.
/// </summary>
public sealed record PlacementView(
    string ProviderInstanceId,
    QuartermasterOwnerScope Owner,
    bool OwnerMatches,
    ImmutableArray<QuartermasterPlacementCharacter> Characters,
    ImmutableDictionary<PlacementRuleKey, QuartermasterPlacementItem> Items,
    ImmutableArray<PlacementRuleKey> Rules,
    long Version)
{
    public static PlacementView Empty { get; } = new(string.Empty, new(null, null, null, null), false, [],
        ImmutableDictionary<PlacementRuleKey, QuartermasterPlacementItem>.Empty, [], 0);

    public QuartermasterPlacementCharacter? Character(ulong localContentId) =>
        Characters.FirstOrDefault(character => character.Is(localContentId));
}

/// <summary>One placement editor action queued from the Deposition tab.</summary>
public sealed record PlacementEditRequest(
    string Operation,
    string OperationLabel,
    QuartermasterPlacementCharacter Character,
    QuartermasterPlacementDestination? Destination,
    IReadOnlyList<QuartermasterPlacementEditItem> Items);

/// <summary>
/// The Deposition tab's status line: what the last edit did. Problem marks a refusal or failure; it stays until a later
/// edit replaces it. A success is transient (see <see cref="MarketAcquisitionPlacementService.Status"/>).
/// </summary>
public sealed record PlacementStatus(string Text, bool Problem);

/// <summary>
/// Framework-ticked cache behind the Deposition tab. GetPlacement and EditPlacement run only in <see cref="Tick"/>;
/// the panel reads <see cref="View"/> and queues edits, so Draw never calls IPC. Refreshes on Quartermaster's Changed
/// notification, when the buy list's items change, and after every edit.
/// </summary>
public sealed class MarketAcquisitionPlacementService : IDisposable
{
    private static readonly TimeSpan VisibleWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VisibleRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CapabilityInterval = TimeSpan.FromSeconds(5);
    // Quartermaster can signal Changed on many frames in a row during its own runs; reads are coalesced to this.
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SuccessStatusLifetime = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SuccessStatusMinimum = TimeSpan.FromSeconds(3);

    private readonly QuartermasterIpcClient quartermaster;
    private readonly QuartermasterPlacementClient placement;
    private readonly Func<QuartermasterOwnerScope> currentOwner;
    private readonly Func<MarketAcquisitionRequestDocument?> currentDocument;
    private readonly Action<string> warn;
    private readonly Func<uint, bool> canBeHq;
    private readonly Dictionary<uint, bool> hqVariants = new();
    private readonly object gate = new();
    private readonly List<PlacementEditRequest> pendingEdits = [];
    private readonly HashSet<PlacementRuleKey> pendingRules = [];
    private DateTimeOffset lastVisibleUtc = DateTimeOffset.MinValue;
    private DateTimeOffset fetchedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset capabilityCheckedAtUtc = DateTimeOffset.MinValue;
    private bool dirty = true, disposed;
    private int planShape;
    private long version;
    private string? lastLogged;
    private ulong lastOwnerContentId;
    private DateTimeOffset statusSetAtUtc;

    public MarketAcquisitionPlacementService(
        QuartermasterIpcClient quartermaster,
        QuartermasterPlacementClient placement,
        Func<QuartermasterOwnerScope> currentOwner,
        Func<MarketAcquisitionRequestDocument?> currentDocument,
        Func<uint, bool> canBeHq,
        Action<string> warn)
    {
        this.quartermaster = quartermaster;
        this.placement = placement;
        this.currentOwner = currentOwner;
        this.currentDocument = currentDocument;
        this.canBeHq = canBeHq;
        this.warn = warn;
        quartermaster.Changed += OnQuartermasterChanged;
    }

    /// <summary>True while Quartermaster advertises placementEdit.v1 and exposes its channels.</summary>
    public bool HasPlacementEdit { get; private set; }

    public PlacementView View { get; private set; } = PlacementView.Empty;

    /// <summary>Why the last read failed; null after a good read.</summary>
    public string? ReadProblem { get; private set; }

    /// <summary>
    /// What the last edit did. A refusal or failure stays until a later edit; a success clears on the first good read
    /// once it has been shown a moment, after a short lifetime, or when the tab is reopened.
    /// </summary>
    public PlacementStatus? Status { get; private set; }

    /// <summary>Called from Draw while the Deposition tab is on screen; reopening the tab retires a success message.</summary>
    public void MarkVisible()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastVisibleUtc > VisibleWindow && Status is { Problem: false })
            Status = null;
        lastVisibleUtc = now;
    }

    public void ClearStatus() => Status = null;

    /// <summary>
    /// The rules a buy-list line maps to: one for HQ or NQ only, both for either quality, and only the NQ rule for an
    /// item the game never makes in HQ.
    /// </summary>
    public ImmutableArray<PlacementRuleKey> RulesFor(uint itemId, string hqPolicy) =>
        hqPolicy switch
        {
            "HQOnly" or "HqOnly" => [new(itemId, "HqOnly")],
            "NQOnly" or "NqOnly" => [new(itemId, "NqOnly")],
            _ when !HasHqVariant(itemId) => [new(itemId, "NqOnly")],
            _ => [new(itemId, "NqOnly"), new(itemId, "HqOnly")],
        };

    private bool HasHqVariant(uint itemId)
    {
        if (hqVariants.TryGetValue(itemId, out var known))
            return known;
        bool hq;
        try { hq = canBeHq(itemId); }
        catch (Exception) { hq = true; }
        hqVariants[itemId] = hq;
        return hq;
    }

    public bool IsPending(PlacementRuleKey rule)
    {
        lock (gate)
            return pendingRules.Contains(rule);
    }

    /// <summary>Queues an edit; it is sent on the next tick and the matrix is read again right after.</summary>
    public void RequestEdit(PlacementEditRequest edit)
    {
        if (edit.Items.Count == 0)
            return;
        lock (gate)
        {
            pendingEdits.Add(edit);
            foreach (var item in edit.Items)
                pendingRules.Add(new(item.ItemId, item.Quality));
        }
    }

    public void Tick()
    {
        if (disposed)
            return;
        var now = DateTimeOffset.UtcNow;
        if (now - capabilityCheckedAtUtc >= CapabilityInterval || dirty)
        {
            capabilityCheckedAtUtc = now;
            HasPlacementEdit = quartermaster.TryGetCapabilities(out var advertised, out _) && advertised is not null &&
                               advertised.Capabilities.Contains(QuartermasterPlacementClient.PlacementEditCapability) &&
                               placement.CanEdit;
        }
        if (!HasPlacementEdit)
        {
            if (View.Version != 0 || pendingEdits.Count > 0)
                Reset();
            return;
        }

        // A relog makes the last read another character's: editing stops at once and the matrix is read again.
        var ownerId = currentOwner() is { IsAvailable: true, LocalContentId: { } id } ? id : 0UL;
        if (ownerId != lastOwnerContentId)
        {
            lastOwnerContentId = ownerId;
            dirty = true;
            if (View.OwnerMatches)
                View = View with { OwnerMatches = false };
        }

        if (Status is { Problem: false } && now - statusSetAtUtc >= SuccessStatusLifetime)
            Status = null;

        var edited = FlushEdits();
        var shape = PlanShape(TryDocument());
        if (shape != planShape)
        {
            planShape = shape;
            dirty = true;
        }
        var visible = now - lastVisibleUtc <= VisibleWindow;
        if (!edited && !(visible && (dirty || now - fetchedAtUtc >= VisibleRefreshInterval)))
            return;
        // Own edits read back at once; anything else waits out the interval so a burst of Changed is one read.
        if (!edited && now - fetchedAtUtc < MinimumRefreshInterval)
            return;
        Refresh(now);
    }

    private void Reset()
    {
        lock (gate)
        {
            pendingEdits.Clear();
            pendingRules.Clear();
        }
        View = PlacementView.Empty;
        ReadProblem = null;
        dirty = true;
    }

    private static int PlanShape(MarketAcquisitionRequestDocument? document)
    {
        if (document is null)
            return 0;
        var hash = new HashCode();
        foreach (var line in document.Lines)
        {
            hash.Add(line.ItemId);
            hash.Add(line.HqPolicy);
        }
        return hash.ToHashCode();
    }

    // ---- Edits ------------------------------------------------------------------------------------

    private bool FlushEdits()
    {
        PlacementEditRequest[] edits;
        lock (gate)
        {
            if (pendingEdits.Count == 0)
                return false;
            edits = pendingEdits.ToArray();
            pendingEdits.Clear();
        }
        foreach (var edit in edits)
        {
            try { Apply(edit); }
            catch (Exception ex)
            {
                SetStatus(new($"{edit.OperationLabel} failed: {ex.Message}", true));
                LogOnce($"Placement edit failed: {ex.Message}");
            }
        }
        return true;
    }

    private void SetStatus(PlacementStatus status)
    {
        Status = status;
        statusSetAtUtc = DateTimeOffset.UtcNow;
    }

    private void Apply(PlacementEditRequest edit)
    {
        var view = View;
        var owner = currentOwner();
        // The view's characters (and who is the owner) belong to whoever was logged in when it was read: sending them
        // for another character would edit that character's own homes instead.
        if (!owner.IsAvailable || owner.LocalContentId != view.Owner.LocalContentId)
        {
            SetStatus(new($"{edit.OperationLabel} not sent: logged in as another character.", true));
            dirty = true;
            return;
        }
        if (string.IsNullOrEmpty(view.ProviderInstanceId) || !view.OwnerMatches)
        {
            SetStatus(new($"{edit.OperationLabel} not sent: Quartermaster's placement is not loaded for this character.", true));
            return;
        }
        // Quartermaster allows at most MaxItems per edit; a larger selection goes in consecutive edits.
        var applied = 0;
        var unchanged = 0;
        var notes = new List<string>();
        QuartermasterPlacementEditOutcome? firstRefusal = null;
        string? requestRefusal = null;
        foreach (var chunk in edit.Items.Chunk(QuartermasterPlacementClient.MaxItems))
        {
            if (!placement.TryEditPlacement(
                    view.ProviderInstanceId,
                    owner,
                    edit.Operation,
                    chunk,
                    edit.Character.IsOwner ? null : (edit.Character.LocalContentId, edit.Character.HomeWorldId),
                    QuartermasterPlacementOperations.NeedsDestination(edit.Operation) ? edit.Destination : null,
                    allOrNothing: false,
                    out var ack,
                    out var error))
            {
                requestRefusal ??= error;
                break;
            }
            applied += ack!.Items.Count(item => item.Outcome == "applied");
            unchanged += ack.Items.Count(item => item.Outcome == "unchanged");
            firstRefusal ??= ack.Items.FirstOrDefault(item => item.IsRefused);
            if (ack.IsRejected && firstRefusal is null)
                requestRefusal ??= $"{ack.ErrorCode ?? "rejected"}: {ack.Message}";
            foreach (var item in ack.Items)
            {
                if (item.RemoteHomesMovedFrom is { } from)
                    notes.Add($"{item.ItemName}: homes moved from {Name(from)} to {edit.Character.CharacterName}");
                else if (item.RemoteHomesKeptOn is { } kept)
                    notes.Add($"{item.ItemName}: homes on {Name(kept)} kept");
            }
            if (ack.IsRejected && ack.Items.All(item => item.Outcome != "applied"))
                break;
        }

        var refusal = firstRefusal is { } refused
            ? $"{refused.ItemName}: {(string.IsNullOrWhiteSpace(refused.Message) ? refused.Code : refused.Message)}"
            : requestRefusal;
        var text = refusal is null
            ? applied > 0
                ? $"{edit.OperationLabel}: {applied:N0} item{(applied == 1 ? string.Empty : "s")} changed."
                : $"{edit.OperationLabel}: nothing to change."
            : applied > 0
                ? $"{edit.OperationLabel}: {applied:N0} item{(applied == 1 ? string.Empty : "s")} changed; refused for {refusal}"
                : $"{edit.OperationLabel} refused for {refusal}";
        if (notes.Count > 0)
            text += " " + string.Join("; ", notes.Take(2)) + (notes.Count > 2 ? $"; {notes.Count - 2:N0} more." : ".");
        SetStatus(new(text, refusal is not null));
        if (refusal is not null)
            LogOnce($"Placement edit: {text}");
        dirty = true;
    }

    private static string Name(QuartermasterOwnerScope character) =>
        string.IsNullOrWhiteSpace(character.CharacterName) ? "another character" : character.CharacterName!;

    // ---- Refresh ----------------------------------------------------------------------------------

    private void OnQuartermasterChanged(QuartermasterChanged change) => dirty = true;

    private void Refresh(DateTimeOffset now)
    {
        dirty = false;
        fetchedAtUtc = now;
        try
        {
            if (!placement.TryGetPlacement(null, out var all, out var error))
            {
                Fail(error);
                return;
            }
            var items = all!.Items.ToDictionary(item => new PlacementRuleKey(item.ItemId, item.Quality));
            var rules = all.Items.Select(item => new PlacementRuleKey(item.ItemId, item.Quality)).Distinct().ToImmutableArray();
            // Buy-list items without a rule are read by id: they show with no homes and can be given some.
            var missing = (TryDocument()?.Lines ?? [])
                .Where(line => line.ItemId > 0)
                .SelectMany(line => RulesFor(line.ItemId, line.HqPolicy))
                .Distinct()
                .Where(key => !items.ContainsKey(key))
                .ToArray();
            foreach (var chunk in missing.Chunk(QuartermasterPlacementClient.MaxItems))
            {
                if (!placement.TryGetPlacement(chunk.Select(key => (key.ItemId, key.Quality)).ToArray(), out var named, out error))
                {
                    Fail(error);
                    return;
                }
                foreach (var item in named!.Items)
                    items.TryAdd(new PlacementRuleKey(item.ItemId, item.Quality), item);
            }

            var owner = currentOwner();
            // Content id alone identifies the character; its world can change.
            var ownerMatches = owner.IsAvailable && all.Owner.LocalContentId == owner.LocalContentId;
            View = new PlacementView(all.ProviderInstanceId, all.Owner, ownerMatches, all.Characters,
                items.ToImmutableDictionary(), rules, ++version);
            ReadProblem = null;
            if (Status is { Problem: false } && now - statusSetAtUtc >= SuccessStatusMinimum)
                Status = null;
            lock (gate)
                if (pendingEdits.Count == 0)
                    pendingRules.Clear();
        }
        catch (Exception ex)
        {
            Fail($"Placement refresh failed: {ex.Message}");
        }
    }

    private void Fail(string error)
    {
        ReadProblem = error;
        // Edits waiting on this read are no longer pending: the rows show what was last read.
        lock (gate)
            if (pendingEdits.Count == 0)
                pendingRules.Clear();
        LogOnce(error);
    }

    private void LogOnce(string message)
    {
        if (string.Equals(lastLogged, message, StringComparison.Ordinal))
            return;
        lastLogged = message;
        warn($"[MarketMafioso] {message}");
    }

    private MarketAcquisitionRequestDocument? TryDocument()
    {
        try { return currentDocument(); }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        quartermaster.Changed -= OnQuartermasterChanged;
    }
}
