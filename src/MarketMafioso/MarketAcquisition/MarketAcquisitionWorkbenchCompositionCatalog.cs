using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionWorkbenchCompositionResult(
    bool Success,
    string Message,
    MarketAcquisitionWorkbenchComposition? Composition = null);

public sealed class MarketAcquisitionWorkbenchCompositionCatalog
{
    private readonly IMarketAcquisitionWorkbenchCompositionStore store;
    private readonly List<MarketAcquisitionWorkbenchComposition> compositions;

    public MarketAcquisitionWorkbenchCompositionCatalog(IMarketAcquisitionWorkbenchCompositionStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        var snapshot = store.Load();
        compositions = snapshot.Compositions.ToList();
        SelectedCompositionId = snapshot.SelectedCompositionId;
        Source = snapshot.Source;
    }

    public IReadOnlyList<MarketAcquisitionWorkbenchComposition> Compositions => compositions;

    public string? SelectedCompositionId { get; private set; }

    public MarketAcquisitionWorkbenchComposition? SelectedComposition =>
        compositions.FirstOrDefault(composition => composition.Id == SelectedCompositionId);

    public string Status { get; private set; } = string.Empty;

    /// <summary>Bumped whenever <see cref="Status"/> is set, so the panel can tell a fresh message from a stale one.</summary>
    public int StatusVersion { get; private set; }

    public MarketAcquisitionWorkbenchCompositionSource? Source { get; private set; }

    public MarketAcquisitionWorkbenchComposition? Find(string? id) =>
        id is null ? null : compositions.FirstOrDefault(composition => composition.Id == id);

    /// <summary>The source composition, when it is still linked to the given Workbench identity.</summary>
    public MarketAcquisitionWorkbenchComposition? SourceFor(string workbenchRequestId) =>
        Source is { } source && source.WorkbenchRequestId == workbenchRequestId ? Find(source.CompositionId) : null;

    public void LinkSource(string compositionId, string workbenchRequestId, bool loadedFromComposition)
    {
        if (Find(compositionId) is null || string.IsNullOrWhiteSpace(workbenchRequestId))
            return;
        Source = new(compositionId, workbenchRequestId, loadedFromComposition);
        Persist();
    }

    /// <summary>Returns why <paramref name="name"/> cannot be used, or null when it can.</summary>
    public string? NameProblem(string name, string? exceptId = null) => ValidateName(name, exceptId);

    public bool Select(string? id)
    {
        if (id is not null && compositions.All(composition => composition.Id != id))
            return false;

        SelectedCompositionId = id;
        Persist();
        return true;
    }

    public MarketAcquisitionWorkbenchCompositionResult SaveNew(
        string name,
        MarketAcquisitionRequestDocument document,
        DateTimeOffset? nowUtc = null)
    {
        var validation = ValidateName(name);
        if (validation is not null)
            return Fail(validation);
        if (document.Lines.Count == 0)
            return Fail("Add at least one Workbench line before saving a composition.");

        var composition = MarketAcquisitionWorkbenchComposition.FromDocument(name, document, nowUtc ?? DateTimeOffset.UtcNow);
        compositions.Add(composition);
        SelectedCompositionId = composition.Id;
        Source = new(composition.Id, document.LocalRequestId, false);
        SetStatus($"Saved {composition.Name}.");
        Persist();
        return new MarketAcquisitionWorkbenchCompositionResult(true, Status, composition);
    }

    public MarketAcquisitionWorkbenchCompositionResult Update(
        string compositionId,
        MarketAcquisitionRequestDocument document,
        DateTimeOffset? nowUtc = null)
    {
        var target = Find(compositionId);
        if (target is null)
            return Fail("That composition is no longer available.");
        if (document.Lines.Count == 0)
            return Fail("An empty Workbench cannot overwrite a saved composition.");

        var updated = target.WithDocument(document, nowUtc ?? DateTimeOffset.UtcNow);
        Replace(updated);
        Source = new(updated.Id, document.LocalRequestId, Source?.CompositionId == updated.Id && Source.LoadedFromComposition);
        SetStatus($"Updated {updated.Name} from the Workbench.");
        Persist();
        return new MarketAcquisitionWorkbenchCompositionResult(true, Status, updated);
    }

    public MarketAcquisitionWorkbenchCompositionResult RenameSelected(string name, DateTimeOffset? nowUtc = null)
    {
        var selected = SelectedComposition;
        if (selected is null)
            return Fail("Select a saved composition to rename.");
        var validation = ValidateName(name, selected.Id);
        if (validation is not null)
            return Fail(validation);

        var updated = selected with { Name = name.Trim(), UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow };
        Replace(updated);
        SetStatus($"Renamed composition to {updated.Name}.");
        Persist();
        return new MarketAcquisitionWorkbenchCompositionResult(true, Status, updated);
    }

    public MarketAcquisitionWorkbenchCompositionResult DuplicateSelected(DateTimeOffset? nowUtc = null)
    {
        var selected = SelectedComposition;
        if (selected is null)
            return Fail("Select a saved composition to duplicate.");

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var duplicate = selected with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NextCopyName(selected.Name),
            SweepDataCenters = selected.SweepDataCenters.ToList(),
            Lines = MarketAcquisitionWorkbenchComposition.CloneLines(selected.Lines),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        compositions.Add(duplicate);
        SelectedCompositionId = duplicate.Id;
        SetStatus($"Duplicated {selected.Name} as {duplicate.Name}.");
        Persist();
        return new MarketAcquisitionWorkbenchCompositionResult(true, Status, duplicate);
    }

    public MarketAcquisitionWorkbenchCompositionResult DeleteSelected()
    {
        var selected = SelectedComposition;
        if (selected is null)
            return Fail("Select a saved composition to delete.");

        compositions.Remove(selected);
        SelectedCompositionId = compositions.OrderBy(composition => composition.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Id;
        if (Source?.CompositionId == selected.Id)
            Source = null;
        SetStatus($"Deleted {selected.Name}.");
        Persist();
        return new MarketAcquisitionWorkbenchCompositionResult(true, Status, selected);
    }

    private string? ValidateName(string name, string? exceptId = null)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0)
            return "Enter a composition name.";
        if (normalized.Length > 80)
            return "Composition names must be 80 characters or fewer.";
        return compositions.Any(composition => composition.Id != exceptId && composition.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ? $"A composition named {normalized} already exists."
            : null;
    }

    private string NextCopyName(string sourceName)
    {
        var candidate = $"{sourceName} copy";
        var suffix = 2;
        while (compositions.Any(composition => composition.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{sourceName} copy {suffix++}";
        return candidate;
    }

    private void Replace(MarketAcquisitionWorkbenchComposition composition)
    {
        var index = compositions.FindIndex(candidate => candidate.Id == composition.Id);
        if (index >= 0)
            compositions[index] = composition;
    }

    private void SetStatus(string message)
    {
        Status = message;
        StatusVersion++;
    }

    private MarketAcquisitionWorkbenchCompositionResult Fail(string message)
    {
        SetStatus(message);
        return new MarketAcquisitionWorkbenchCompositionResult(false, message);
    }

    private void Persist() => store.Save(new MarketAcquisitionWorkbenchCompositionStoreSnapshot(
        compositions.OrderBy(composition => composition.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        SelectedCompositionId,
        Source));
}
