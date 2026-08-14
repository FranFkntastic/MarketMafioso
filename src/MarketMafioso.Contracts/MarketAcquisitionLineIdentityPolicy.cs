using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLineIdentityPolicy
{
    public static bool AreQualityDomainsDisjoint(IEnumerable<string?> hqPolicies)
    {
        ArgumentNullException.ThrowIfNull(hqPolicies);
        var hasNormal = false;
        var hasHigh = false;
        foreach (var policy in hqPolicies)
        {
            if (string.Equals(policy?.Trim(), "NQOnly", StringComparison.OrdinalIgnoreCase))
            {
                if (hasNormal)
                    return false;
                hasNormal = true;
                continue;
            }

            if (string.Equals(policy?.Trim(), "HQOnly", StringComparison.OrdinalIgnoreCase))
            {
                if (hasHigh)
                    return false;
                hasHigh = true;
                continue;
            }

            return false;
        }

        return true;
    }
}
