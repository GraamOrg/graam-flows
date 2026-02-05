using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Cli.Services;

public class CollateralBuilder
{
    public List<IAsset> BuildAssets(DealModelFile dealModel, bool useSinglePool = false)
    {
        // For WAL validation, use single-pool model with weighted average characteristics
        // The prospectus WAL tables use aggregate pool characteristics, not individual pools
        if (useSinglePool && dealModel.PoolStratification != null)
            return BuildFromPoolStratificationSummary(dealModel.PoolStratification, dealModel);

        // Priority 1: Use pool stratification if present
        if (dealModel.PoolStratification?.Pools != null && dealModel.PoolStratification.Pools.Count > 0)
            return BuildFromPoolStratification(dealModel.PoolStratification, dealModel);

        // Priority 2: Use explicit assets from collateral section
        if (dealModel.Collateral?.Assets != null && dealModel.Collateral.Assets.Count > 0)
            return BuildFromAssetList(dealModel.Collateral.Assets, dealModel);

        // Priority 3: Use collateral summary to create synthetic asset
        if (dealModel.Collateral != null)
            return BuildFromCollateralSummary(dealModel.Collateral, dealModel);

        // Priority 4: Use pool stratification summary if present
        if (dealModel.PoolStratification != null)
            return BuildFromPoolStratificationSummary(dealModel.PoolStratification, dealModel);

        // Priority 5: Create synthetic asset from tranche sum
        return BuildSyntheticFromTranches(dealModel);
    }

    private List<IAsset> BuildFromPoolStratification(PoolStratificationSection poolStrat, DealModelFile dealModel)
    {
        var assets = new List<IAsset>();
        var firstPayDate = GetFirstPayDate(dealModel);

        foreach (var pool in poolStrat.Pools!)
        {
            var wam = pool.RemainingTermMonths;

            var asset = new Asset
            {
                AssetId = $"POOL_{pool.PoolNum}",
                AssetName = $"Pool {pool.PoolNum}",
                GroupNum = "1",
                CurrentBalance = pool.AggregateBalance,
                BalanceAtIssuance = pool.AggregateBalance,
                OriginalBalance = pool.AggregateBalance,
                CurrentInterestRate = pool.GrossApr,
                OriginalInterestRate = pool.GrossApr,
                OriginalAmortizationTerm = wam,
                OriginalDate = firstPayDate,
                ServiceFee = 0.0,
                LoanStatus = "Current",
                InterestRateType = InterestRateType.FRM,
                OriginalLTV = 100,
                IndexName = MarketDataInstEnum.None,
                IsIO = false
            };
            assets.Add(asset);
        }

        return assets;
    }

    private List<IAsset> BuildFromPoolStratificationSummary(PoolStratificationSection poolStrat, DealModelFile dealModel)
    {
        var firstPayDate = GetFirstPayDate(dealModel);
        var wam = poolStrat.WeightedAverageRemainingTerm.HasValue ? (int)Math.Round(poolStrat.WeightedAverageRemainingTerm.Value) : 60;
        var balance = poolStrat.TotalBalance ?? dealModel.Deal.Tranches.Sum(t => t.OriginalBalance);

        // For WAL validation, model as a single pool with remaining term (same approach as individual pools).
        // This ensures scheduled principal matches the prospectus assumption of level amortization
        // over the weighted average remaining term.
        var asset = new Asset
        {
            AssetId = "POOL_SUMMARY",
            AssetName = "Pool Summary",
            GroupNum = "1",
            CurrentBalance = balance,
            BalanceAtIssuance = balance,
            OriginalBalance = balance,
            CurrentInterestRate = poolStrat.WeightedAverageApr ?? 5.0,
            OriginalInterestRate = poolStrat.WeightedAverageApr ?? 5.0,
            OriginalAmortizationTerm = wam, // Use remaining term for correct payment calculation
            OriginalDate = firstPayDate, // Set to first pay date since we're using remaining term
            ServiceFee = 0.0,
            LoanStatus = "Current",
            InterestRateType = InterestRateType.FRM,
            OriginalLTV = 100,
            IndexName = MarketDataInstEnum.None,
            IsIO = false
        };

        return [asset];
    }

    private List<IAsset> BuildFromAssetList(List<AssetEntry> assetEntries, DealModelFile dealModel)
    {
        var assets = new List<IAsset>();
        var firstPayDate = GetFirstPayDate(dealModel);

        for (var i = 0; i < assetEntries.Count; i++)
        {
            var entry = assetEntries[i];
            var originalTerm = entry.OriginalTerm ?? entry.RemainingTerm + 12;
            var wala = originalTerm - entry.RemainingTerm;
            var originationDate = entry.OriginationDate ?? firstPayDate.AddMonths(-wala);

            var asset = new Asset
            {
                AssetId = entry.AssetId ?? $"ASSET_{i + 1}",
                AssetName = entry.AssetId ?? $"Asset {i + 1}",
                GroupNum = entry.GroupNum,
                CurrentBalance = entry.Balance,
                BalanceAtIssuance = entry.Balance,
                OriginalBalance = entry.Balance,
                CurrentInterestRate = entry.InterestRate,
                OriginalInterestRate = entry.InterestRate,
                OriginalAmortizationTerm = originalTerm,
                OriginalDate = originationDate,
                ServiceFee = entry.ServiceFee ?? 0.0,
                LoanStatus = entry.LoanStatus ?? "Current",
                InterestRateType = InterestRateType.FRM,
                OriginalLTV = 100,
                IndexName = MarketDataInstEnum.None,
                IsIO = false
            };
            assets.Add(asset);
        }

        return assets;
    }

    private List<IAsset> BuildFromCollateralSummary(CollateralSection collateral, DealModelFile dealModel)
    {
        var firstPayDate = GetFirstPayDate(dealModel);
        var wam = collateral.Wam.HasValue ? (int)Math.Round(collateral.Wam.Value) : 60;
        var originalTerm = collateral.OriginalTerm ?? wam + 12;
        var wala = collateral.Wala.HasValue ? (int)collateral.Wala.Value : originalTerm - wam;
        var balance = collateral.TotalBalance ?? dealModel.Deal.Tranches.Sum(t => t.OriginalBalance);

        var asset = new Asset
        {
            AssetId = "POOL_SUMMARY",
            AssetName = "Pool Summary",
            GroupNum = "1",
            CurrentBalance = balance,
            BalanceAtIssuance = balance,
            OriginalBalance = balance,
            CurrentInterestRate = collateral.Wac ?? 5.0,
            OriginalInterestRate = collateral.Wac ?? 5.0,
            OriginalAmortizationTerm = originalTerm,
            OriginalDate = firstPayDate.AddMonths(-wala),
            ServiceFee = collateral.ServiceFee ?? 0.0,
            LoanStatus = "Current",
            InterestRateType = InterestRateType.FRM,
            OriginalLTV = 100,
            IndexName = MarketDataInstEnum.None,
            IsIO = false
        };

        return [asset];
    }

    private List<IAsset> BuildSyntheticFromTranches(DealModelFile dealModel)
    {
        var firstPayDate = GetFirstPayDate(dealModel);
        var totalBalance = dealModel.Deal.Tranches.Sum(t => t.OriginalBalance * t.Factor);

        // Estimate WAC from tranche coupons
        var weightedCoupon = dealModel.Deal.Tranches
            .Where(t => t.FixedCoupon.HasValue && t.OriginalBalance > 0)
            .Sum(t => t.FixedCoupon!.Value * t.OriginalBalance * t.Factor);
        var wac = totalBalance > 0 ? weightedCoupon / totalBalance : 5.0;

        // Add spread to get pool WAC (tranches pay less than pool earns)
        wac += 1.0; // Assume 100bps excess spread

        var asset = new Asset
        {
            AssetId = "SYNTHETIC_POOL",
            AssetName = "Synthetic Pool",
            GroupNum = "1",
            CurrentBalance = totalBalance,
            BalanceAtIssuance = totalBalance,
            OriginalBalance = totalBalance,
            CurrentInterestRate = wac,
            OriginalInterestRate = wac,
            OriginalAmortizationTerm = 60, // Default 5-year amortization
            OriginalDate = firstPayDate.AddMonths(-6), // Assume 6 months seasoned
            ServiceFee = 0.0,
            LoanStatus = "Current",
            InterestRateType = InterestRateType.FRM,
            OriginalLTV = 100,
            IndexName = MarketDataInstEnum.None,
            IsIO = false
        };

        return [asset];
    }

    private static DateTime GetFirstPayDate(DealModelFile dealModel)
    {
        var firstPayDate = dealModel.Deal.Tranches
            .Where(t => t.FirstPayDate.HasValue && t.FirstPayDate != default)
            .Select(t => t.FirstPayDate!.Value)
            .DefaultIfEmpty(DateTime.Today.AddMonths(1))
            .Min();

        return firstPayDate;
    }
}
