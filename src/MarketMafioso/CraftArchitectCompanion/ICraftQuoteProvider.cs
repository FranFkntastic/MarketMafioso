using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public interface ICraftQuoteProvider
{
    string ProviderId { get; }
    bool IsConfigured { get; }
    Task<CraftAppraisalQuote?> GetQuoteAsync(MarketAppraisalRequest request, CancellationToken cancellationToken = default);
}
