using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed class CraftArchitectFileQuoteProvider : ICraftQuoteProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<string?> readQuotePath;

    public CraftArchitectFileQuoteProvider(Func<string?> readQuotePath)
    {
        this.readQuotePath = readQuotePath ?? throw new ArgumentNullException(nameof(readQuotePath));
    }

    public string ProviderId => "CraftArchitectFile";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(readQuotePath());

    public async Task<CraftAppraisalQuote?> GetQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        var path = readQuotePath();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
            throw new FileNotFoundException("Configured Craft Architect quote file does not exist.", path);

        await using var stream = File.OpenRead(path);
        var quote = await JsonSerializer.DeserializeAsync<CraftAppraisalQuote>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (quote is null)
            throw new InvalidOperationException("Craft Architect quote file did not contain a quote.");

        ValidateQuote(request, quote);
        return quote;
    }

    private static void ValidateQuote(MarketAppraisalRequest request, CraftAppraisalQuote quote)
    {
        if (quote.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported Craft Architect quote schema version {quote.SchemaVersion}.");

        if (quote.ItemId != request.ItemId)
        {
            throw new InvalidOperationException(
                $"Craft Architect quote item {quote.ItemId} does not match selected item {request.ItemId}.");
        }

        if (quote.RequestedQuantity != request.Quantity)
        {
            throw new InvalidOperationException(
                $"Craft Architect quote quantity {quote.RequestedQuantity} does not match requested quantity {request.Quantity}.");
        }
    }
}
