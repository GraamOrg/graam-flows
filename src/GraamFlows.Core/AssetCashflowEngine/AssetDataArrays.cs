using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Util;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     Parallel arrays for asset data - optimized for cache-friendly processing.
///     Converts IList&lt;IAsset&gt; into structure-of-arrays format.
/// </summary>
public class AssetDataArrays
{
    public AssetDataArrays(IList<IAsset> assets)
    {
        AssetCount = assets.Count;

        // Allocate arrays
        OriginalDate = new int[AssetCount];
        OriginalBalance = new double[AssetCount];
        OriginalInterestRate = new double[AssetCount];
        CurrentInterestRate = new double[AssetCount];
        OriginalAmortizationTerm = new int[AssetCount];
        CurrentBalance = new double[AssetCount];
        ServiceFee = new double[AssetCount];
        DebtService = new double[AssetCount];

        InitialAdjustmentPeriod = new int[AssetCount];
        AdjustmentPeriod = new int[AssetCount];
        IndexName = new int[AssetCount];
        IndexMargin = new double[AssetCount];
        LifeAdjustmentCap = new double[AssetCount];
        LifeAdjustmentFloor = new double[AssetCount];
        AdjustmentCap = new double[AssetCount];

        IOTerm = new int[AssetCount];
        ForbearanceAmt = new double[AssetCount];

        StepDatesCount = new int[AssetCount];

        // First pass: count total step dates/rates
        var totalSteps = 0;
        for (var i = 0; i < AssetCount; i++)
        {
            var asset = assets[i];
            var stepCount = CountSteps(asset.StepDatesList);
            StepDatesCount[i] = stepCount;
            totalSteps += stepCount;
        }

        StepDatesList = new int[totalSteps];
        StepRatesList = new double[totalSteps];

        // Second pass: populate arrays
        var stepIndex = 0;
        for (var i = 0; i < AssetCount; i++)
        {
            var asset = assets[i];

            OriginalDate[i] = DateUtil.CalcAbsT(asset.OriginalDate);
            OriginalBalance[i] = asset.OriginalBalance;
            OriginalInterestRate[i] = asset.OriginalInterestRate;
            CurrentInterestRate[i] = asset.CurrentInterestRate;
            OriginalAmortizationTerm[i] = asset.OriginalAmortizationTerm;
            CurrentBalance[i] = asset.CurrentBalance;
            ServiceFee[i] = asset.ServiceFee;
            DebtService[i] = asset.DebtService;

            InitialAdjustmentPeriod[i] = asset.InitialAdjustmentPeriod;
            AdjustmentPeriod[i] = asset.AdjustmentPeriod;
            IndexName[i] = (int)asset.IndexName;
            IndexMargin[i] = asset.IndexMargin;
            LifeAdjustmentCap[i] = asset.LifeAdjustmentCap ?? 100.0;
            LifeAdjustmentFloor[i] = asset.LifeAdjustmentFloor ?? 0.0;
            AdjustmentCap[i] = asset.AdjustmentCap ?? 100.0;

            IOTerm[i] = asset.IOTerm ?? 0;
            ForbearanceAmt[i] = asset.ForbearanceAmt ?? 0;

            // Parse and flatten step dates/rates
            stepIndex = ParseStepData(asset.StepDatesList, asset.StepRatesList, stepIndex);
        }
    }

    public int AssetCount { get; }

    // Core loan data
    public int[] OriginalDate { get; }
    public double[] OriginalBalance { get; }
    public double[] OriginalInterestRate { get; }
    public double[] CurrentInterestRate { get; }
    public int[] OriginalAmortizationTerm { get; }
    public double[] CurrentBalance { get; }
    public double[] ServiceFee { get; }
    public double[] DebtService { get; }

    // ARM data
    public int[] InitialAdjustmentPeriod { get; }
    public int[] AdjustmentPeriod { get; }
    public int[] IndexName { get; }
    public double[] IndexMargin { get; }
    public double[] LifeAdjustmentCap { get; }
    public double[] LifeAdjustmentFloor { get; }
    public double[] AdjustmentCap { get; }

    // IO and forbearance
    public int[] IOTerm { get; }
    public double[] ForbearanceAmt { get; }

    // Step rates - flattened arrays with counts
    public int[] StepDatesCount { get; }
    public int[] StepDatesList { get; }
    public double[] StepRatesList { get; }

    private static int CountSteps(string stepDatesList)
    {
        if (string.IsNullOrEmpty(stepDatesList))
            return 0;

        var count = 1;
        for (var i = 0; i < stepDatesList.Length; i++)
            if (stepDatesList[i] == ',')
                count++;
        return count;
    }

    private int ParseStepData(string stepDatesList, string stepRatesList, int startIndex)
    {
        if (string.IsNullOrEmpty(stepDatesList))
            return startIndex;

        var dates = stepDatesList.Split(',');
        var rates = stepRatesList?.Split(',') ?? Array.Empty<string>();

        for (var i = 0; i < dates.Length; i++)
        {
            if (int.TryParse(dates[i].Trim(), out var date))
                StepDatesList[startIndex] = date;

            if (i < rates.Length && double.TryParse(rates[i].Trim(), out var rate))
                StepRatesList[startIndex] = rate;

            startIndex++;
        }

        return startIndex;
    }
}