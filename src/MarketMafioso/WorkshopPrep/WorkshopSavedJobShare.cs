using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopSavedJobParseResult(
    bool Success,
    string Message,
    string Name,
    IReadOnlyList<WorkshopPrepQueueItem> Items)
{
    public static WorkshopSavedJobParseResult Fail(string message) => new(false, message, string.Empty, []);
}

/// <summary>
/// Portable text form of a workshop saved job. Workshop project ids are game-data row ids, so a
/// saved job means the same thing on every character, instance, and machine.
/// </summary>
public static class WorkshopSavedJobShare
{
    public const string FormatId = "marketmafioso.workshop-saved-job";
    public const int CurrentVersion = 1;
    public const int MaxQuantity = 9999;
    public const int MaxNameLength = 128;
    public const int MaxTextLength = 256 * 1024;
    public const string FileExtension = ".json";

    private const string NotASavedJob = "That is not a MarketMafioso workshop saved job.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Export(
        WorkshopFrozenQueue queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(projects);

        var projectNames = BuildProjectNames(projects);
        var document = new ShareDocument(
            FormatId,
            CurrentVersion,
            queue.Name,
            nowUtc,
            queue.Items
                .Select(item => new ShareItem(
                    item.WorkshopItemId,
                    projectNames.GetValueOrDefault(item.WorkshopItemId),
                    item.Quantity))
                .ToList());
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static WorkshopSavedJobParseResult Parse(string? text, IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (string.IsNullOrWhiteSpace(text))
            return WorkshopSavedJobParseResult.Fail("No saved job text was found.");

        if (text.Length > MaxTextLength)
            return WorkshopSavedJobParseResult.Fail("That text is too large to be a workshop saved job.");

        // Tolerate chat wrappers such as Discord code fences around the JSON object.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return WorkshopSavedJobParseResult.Fail(NotASavedJob);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.AsMemory(start, end - start + 1));
        }
        catch (JsonException)
        {
            return WorkshopSavedJobParseResult.Fail(NotASavedJob);
        }

        using (document)
            return ParseRoot(document.RootElement, projects);
    }

    public static string BuildFileName(string jobName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((jobName ?? string.Empty)
            .Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        return cleaned.Length == 0 ? "workshop-saved-job" : cleaned;
    }

    private static WorkshopSavedJobParseResult ParseRoot(
        JsonElement root,
        IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out var format) ||
            format.ValueKind != JsonValueKind.String ||
            !string.Equals(format.GetString(), FormatId, StringComparison.Ordinal))
        {
            return WorkshopSavedJobParseResult.Fail(NotASavedJob);
        }

        if (!root.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version) ||
            version < 1)
        {
            return WorkshopSavedJobParseResult.Fail("Saved job has no valid format version.");
        }

        if (version > CurrentVersion)
            return WorkshopSavedJobParseResult.Fail("That saved job was exported by a newer MarketMafioso. Update to import it.");

        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()!.Trim()
            : string.Empty;
        if (name.Length == 0)
            return WorkshopSavedJobParseResult.Fail("Saved job has no name.");

        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength].TrimEnd();

        if (!root.TryGetProperty("items", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array ||
            itemsElement.GetArrayLength() == 0)
        {
            return WorkshopSavedJobParseResult.Fail("Saved job contains no projects.");
        }

        var projectNames = BuildProjectNames(projects);
        var items = new List<WorkshopPrepQueueItem>();
        var unknown = new List<string>();
        foreach (var element in itemsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("workshopItemId", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetUInt32(out var workshopItemId) ||
                !element.TryGetProperty("quantity", out var quantityElement) ||
                quantityElement.ValueKind != JsonValueKind.Number ||
                !quantityElement.TryGetInt32(out var quantity) ||
                quantity < 1 ||
                quantity > MaxQuantity)
            {
                return WorkshopSavedJobParseResult.Fail(
                    $"Saved job contains an invalid project entry. Quantities must be 1 to {MaxQuantity}.");
            }

            if (!projectNames.ContainsKey(workshopItemId))
            {
                var exportedName = element.TryGetProperty("name", out var itemName) && itemName.ValueKind == JsonValueKind.String
                    ? itemName.GetString()
                    : null;
                unknown.Add(string.IsNullOrWhiteSpace(exportedName)
                    ? $"#{workshopItemId}"
                    : $"{exportedName.Trim()} (#{workshopItemId})");
                continue;
            }

            var existing = items.FirstOrDefault(x => x.WorkshopItemId == workshopItemId);
            if (existing == null)
            {
                items.Add(new WorkshopPrepQueueItem { WorkshopItemId = workshopItemId, Quantity = quantity });
                continue;
            }

            if (existing.Quantity + quantity > MaxQuantity)
            {
                return WorkshopSavedJobParseResult.Fail(
                    $"Saved job contains an invalid project entry. Quantities must be 1 to {MaxQuantity}.");
            }

            existing.Quantity += quantity;
        }

        // A job missing some of its projects would silently build less than what was shared.
        if (unknown.Count > 0)
        {
            const int shown = 3;
            var listed = string.Join(", ", unknown.Take(shown));
            if (unknown.Count > shown)
                listed += $", +{unknown.Count - shown} more";

            return WorkshopSavedJobParseResult.Fail(
                $"Saved job uses workshop projects this game data does not have: {listed}.");
        }

        return new WorkshopSavedJobParseResult(true, $"Read saved job {name}.", name, items);
    }

    private static Dictionary<uint, string> BuildProjectNames(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var names = new Dictionary<uint, string>();
        foreach (var project in projects)
            names.TryAdd(project.WorkshopItemId, project.Name);

        return names;
    }

    private sealed record ShareDocument(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("exportedAt")] DateTime ExportedAt,
        [property: JsonPropertyName("items")] IReadOnlyList<ShareItem> Items);

    private sealed record ShareItem(
        [property: JsonPropertyName("workshopItemId")] uint WorkshopItemId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("quantity")] int Quantity);
}
