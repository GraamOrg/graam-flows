namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     High-performance asset cashflow generator. Uses a struct-of-arrays layout
///     (AssetDataArrays) over the asset set — "parallel arrays" in the data-structure
///     sense, not thread-level parallelism. Execution is a single sequential pass per
///     asset group. Modeled after the Haxe runner for cache locality and minimal allocations.
/// </summary>
public static class Amortizer
{
    /// <summary>
    ///     Generate aggregated cashflows for a group of assets using array-based processing.
    ///
    ///     Assumption arrays are jagged 2-D: outer index is asset (matches <see cref="AssetDataArrays"/>
    ///     ordering), inner index is period. Uniform-per-group assumptions are expressed by passing
    ///     the same row reference (or value-identical rows) for every asset. Per-asset assumptions
    ///     (graam-flows#5) are expressed by giving each asset a distinct row.
    /// </summary>
    /// <param name="absTime">Optional ABS prepay rate matrix. When provided, prepay is calculated as
    /// absTime[assetIndex][period] * originalBalance instead of smmTime[assetIndex][period] * currentBalance.
    /// This matches the ABS convention where prepay is expressed as a percentage of original balance
    /// per period.</param>
    /// <param name="origMdrTime">Optional ORIGMDR default matrix (per-asset, monthly fraction of
    /// original balance). When an asset's inner array is non-null, that period's default is
    /// origMdrTime[assetIndex][period] * originalBalance (capped at the performing balance) instead
    /// of mdrTime[assetIndex][period] * currentBalance. Matches Intex ICMODEFAULT_VORIGMDR.</param>
    public static CashflowResultArrays GenerateCashflows(
        AssetDataArrays assetData,
        int startTime,
        int endTime,
        double[][] smmTime,
        double[][] mdrTime,
        double[][] sevTime,
        double[][] delTime,
        double[][] delAdvIntTime,
        double[][] delAdvPrinTime,
        double[][] forbRecovPpayTime,
        double[][] forbRecovMaturityTime,
        double[][] forbRecovDefaultTime,
        double[][] allMarketRates,
        double[][]? absTime = null,
        double[][]? origMdrTime = null)
    {
        var maxPeriods = Math.Min(endTime - startTime + 1, 720);
        var results = new CashflowResultArrays(maxPeriods);

        // Local references to input arrays for faster access
        var rawOriginalDate = assetData.OriginalDate;
        var rawOriginalBalance = assetData.OriginalBalance;
        var rawOriginalInterestRate = assetData.OriginalInterestRate;
        var rawCurrentInterestRate = assetData.CurrentInterestRate;
        var rawOriginalAmortizationTerm = assetData.OriginalAmortizationTerm;
        var rawCurrentBalance = assetData.CurrentBalance;
        var rawServiceFee = assetData.ServiceFee;
        var rawDebtService = assetData.DebtService;
        var rawInitialAdjustmentPeriod = assetData.InitialAdjustmentPeriod;
        var rawAdjustmentPeriod = assetData.AdjustmentPeriod;
        var rawIndexName = assetData.IndexName;
        var rawIndexMargin = assetData.IndexMargin;
        var rawLifeAdjustmentCap = assetData.LifeAdjustmentCap;
        var rawLifeAdjustmentFloor = assetData.LifeAdjustmentFloor;
        var rawAdjustmentCap = assetData.AdjustmentCap;
        var rawIOTerm = assetData.IOTerm;
        var rawForbearanceAmt = assetData.ForbearanceAmt;
        var rawStepDatesCount = assetData.StepDatesCount;
        var rawStepDatesList = assetData.StepDatesList;
        var rawStepRatesList = assetData.StepRatesList;

        // Local references to output arrays
        var resultBeginBalance = results.BeginBalance;
        var resultBalance = results.Balance;
        var resultScheduledPrincipal = results.ScheduledPrincipal;
        var resultUnscheduledPrincipal = results.UnscheduledPrincipal;
        var resultInterest = results.Interest;
        var resultNetInterest = results.NetInterest;
        var resultServiceFee = results.ServiceFee;
        var resultDefaultedPrincipal = results.DefaultedPrincipal;
        var resultRecoveryPrincipal = results.RecoveryPrincipal;
        var resultDelinqBalance = results.DelinqBalance;
        var resultUnAdvancedPrincipal = results.UnAdvancedPrincipal;
        var resultUnAdvancedInterest = results.UnAdvancedInterest;
        var resultAdvancedPrincipal = results.AdvancedPrincipal;
        var resultAdvancedInterest = results.AdvancedInterest;
        var resultForbearanceRecovery = results.ForbearanceRecovery;
        var resultForbearanceLiquidated = results.ForbearanceLiquidated;
        var resultAccumForbearance = results.AccumForbearance;
        var resultWAM = results.WAM;
        var resultWALA = results.WALA;

        var assetCount = assetData.AssetCount;
        var nextAssetStepDatesIndex = 0;

        for (var assetIndex = 0; assetIndex < assetCount; assetIndex++)
        {
            var survivalFactor = 1.0;
            var balance = rawCurrentBalance[assetIndex];
            var cashflowBalance = balance;
            var cashflowPrevBalance = balance;
            var ioTerm = rawIOTerm[assetIndex];
            var serviceFee = rawServiceFee[assetIndex] / 1200.0;
            var rateSteps = rawStepDatesCount[assetIndex];
            var nextRateStepDate = 100000;
            var forbearanceAmt = rawForbearanceAmt[assetIndex];
            var assetStepDatesIndex = nextAssetStepDatesIndex;

            var origBalance = rawOriginalBalance[assetIndex];
            var term = rawOriginalAmortizationTerm[assetIndex];
            var annRatePct = rawCurrentInterestRate[assetIndex] > 0
                ? rawCurrentInterestRate[assetIndex]
                : rawOriginalInterestRate[assetIndex];
            var rate = annRatePct / 1200.0;
            var debtService = rawDebtService[assetIndex];
            var adjustmentPeriod = rawAdjustmentPeriod[assetIndex];
            var initialAdjustmentPeriod = rawInitialAdjustmentPeriod[assetIndex];
            var currentAdjustmentPeriod = -1;
            var marketRates = rawIndexName[assetIndex] > 0 && allMarketRates != null
                ? allMarketRates[rawIndexName[assetIndex]]
                : null;

            var age = startTime - rawOriginalDate[assetIndex] - 1;
            var hasCashflow = true;
            double scheduledPayment = 0;
            double interestPaid = 0, principal = 0, unadvPrincipal = 0, unadvInterest = 0;

            if (age < 0) age = 0;

            if (rateSteps > 0)
            {
                nextAssetStepDatesIndex += rateSteps;
                nextRateStepDate = rawStepDatesList[assetStepDatesIndex] + 1;
                while (assetStepDatesIndex < nextAssetStepDatesIndex &&
                       rawStepDatesList[assetStepDatesIndex] <= startTime)
                {
                    assetStepDatesIndex++;
                    if (assetStepDatesIndex < nextAssetStepDatesIndex)
                        nextRateStepDate = rawStepDatesList[assetStepDatesIndex] + 1;
                }
            }

            // Remove forbearance from balance
            if (forbearanceAmt > 0)
            {
                cashflowBalance -= forbearanceAmt;
                balance = cashflowBalance;
            }

            // Calculate initial scheduled payment
            if (ioTerm > 0 && age <= ioTerm)
                scheduledPayment = Math.Round(balance * rate * 100.0) / 100.0;
            else if (debtService > 0)
                scheduledPayment = debtService;
            else
            {
                // No debtService and no original balance supplied — amortize off
                // the current balance rather than AmortizingPayment(0, …) = 0,
                // which would yield a sub-interest payment and capitalize interest
                // into principal every period (the pool balance grows). The engine
                // models no negative-amortization product, so a zero original
                // balance is a misconfiguration; fall back to the current balance.
                var amortBalance = origBalance > 0 ? origBalance : balance;
                scheduledPayment = Math.Round(AmortizingPayment(amortBalance, rate, term) * 100.0) / 100.0;
            }

            for (var absT = startTime; absT <= endTime; absT++)
            {
                if (balance < 1 || !hasCashflow)
                    break;

                var period = absT - startTime;
                if (period >= maxPeriods)
                    break;

                // Get assumption values for this asset at this period. Outer
                // index is asset (per graam-flows#5: per-asset assumptions);
                // inner index is period.
                var smm = smmTime[assetIndex][period];
                var mdr = mdrTime[assetIndex][period];
                var sev = sevTime[assetIndex][period];
                var del = delTime[assetIndex][period];
                var delAdvInt = delAdvIntTime[assetIndex][period];
                var delAdvPrin = delAdvPrinTime[assetIndex][period];
                var forbRecovPpay = forbRecovPpayTime[assetIndex][period];
                var forbRecovMaturity = forbRecovMaturityTime[assetIndex][period];
                var forbRecovDefault = forbRecovDefaultTime[assetIndex][period];

                // FRM cashflow generation
                if (age > term)
                {
                    hasCashflow = false;
                }
                else
                {
                    age++;

                    // Step rate adjustment
                    if (absT == nextRateStepDate && assetStepDatesIndex < nextAssetStepDatesIndex)
                    {
                        annRatePct = rawStepRatesList[assetStepDatesIndex];
                        assetStepDatesIndex++;
                        if (assetStepDatesIndex < nextAssetStepDatesIndex)
                            nextRateStepDate = rawStepDatesList[assetStepDatesIndex] + 1;

                        rate = annRatePct / 1200.0;
                        scheduledPayment =
                            Math.Round(AmortizingPayment(cashflowBalance, rate, term - (age - 1)) * 100.0) / 100.0;
                    }

                    // ARM adjustment
                    if (initialAdjustmentPeriod > 0)
                    {
                        if (currentAdjustmentPeriod == -1)
                            currentAdjustmentPeriod = age - 1 <= initialAdjustmentPeriod
                                ? initialAdjustmentPeriod - (age - 1)
                                : adjustmentPeriod - (age - initialAdjustmentPeriod) % adjustmentPeriod;

                        if (currentAdjustmentPeriod == 0)
                        {
                            currentAdjustmentPeriod = adjustmentPeriod;

                            var prevRate = annRatePct;
                            var indexRate = marketRates != null && period > 0 ? marketRates[period - 1] : 0;
                            var mortgageRate = rawIndexMargin[assetIndex] + indexRate;

                            if (mortgageRate - prevRate > rawAdjustmentCap[assetIndex])
                                mortgageRate = prevRate + rawAdjustmentCap[assetIndex];

                            if (mortgageRate > rawLifeAdjustmentCap[assetIndex])
                                mortgageRate = rawLifeAdjustmentCap[assetIndex];

                            if (mortgageRate < rawLifeAdjustmentFloor[assetIndex])
                                mortgageRate = rawLifeAdjustmentFloor[assetIndex];

                            annRatePct = mortgageRate;
                            rate = annRatePct / 1200.0;
                            scheduledPayment =
                                Math.Round(AmortizingPayment(cashflowBalance, rate, term - (age - 1)) * 100.0) / 100.0;
                        }

                        currentAdjustmentPeriod--;
                    }

                    interestPaid = rate * cashflowBalance;

                    if (age <= ioTerm)
                    {
                        principal = 0;
                        cashflowPrevBalance = cashflowBalance;
                        if (age == ioTerm)
                            scheduledPayment =
                                Math.Round(AmortizingPayment(cashflowBalance, rate, term - age) * 100.0) / 100.0;
                    }
                    else
                    {
                        var actualPayment = age < term ? scheduledPayment : cashflowBalance + interestPaid;
                        // Backstop: scheduled principal can never be negative. A
                        // payment below the period's interest would capitalize
                        // interest into principal (balance grows) — the engine has
                        // no negative-amortization product, so a sub-interest
                        // payment is always a misconfiguration. Clamp at zero. For
                        // a normal amortizing payment (payment > interest) the Max
                        // is a no-op, so well-formed loans are unaffected.
                        principal = Math.Min(Math.Max(actualPayment - interestPaid, 0), cashflowBalance);

                        cashflowPrevBalance = cashflowBalance;
                        cashflowBalance -= principal;

                        if (cashflowBalance <= 0)
                        {
                            cashflowBalance = 0;
                            hasCashflow = false;
                        }
                    }
                }

                // Dynamic asset calculations
                var beginBalance = balance;
                var schedBal = balance;

                // ORIGMDR: default dollars are a fraction of the ORIGINAL balance
                // (Intex ICMODEFAULT_VORIGMDR), not the current performing
                // balance. Capped at the performing balance, then re-expressed as
                // an effective current-balance hazard so every downstream use of
                // `mdr` (scheduled-principal reduction, survival factor,
                // forbearance writedown, defPrin) stays consistent.
                if (origMdrTime?[assetIndex] != null)
                {
                    var origDefaultDollars = Math.Min(origMdrTime[assetIndex][period] * origBalance, schedBal);
                    mdr = schedBal > 0 ? origDefaultDollars / schedBal : 0.0;
                }

                var dqFactor = cashflowPrevBalance * survivalFactor > 0
                    ? schedBal / (cashflowPrevBalance * survivalFactor)
                    : 1.0;
                if (double.IsNaN(dqFactor) || double.IsInfinity(dqFactor)) dqFactor = 1.0;

                var defPrin = mdr * schedBal;
                var interest = survivalFactor * interestPaid * dqFactor;
                var schedPrin = survivalFactor * principal * dqFactor;
                var schedPrinMdr = schedPrin * (1 - mdr);

                unadvInterest = interest * del * (1 - delAdvInt) - beginBalance * serviceFee * del * (1 - delAdvInt);
                unadvPrincipal = schedPrinMdr * del * (1 - delAdvPrin);

                schedPrinMdr -= unadvPrincipal;
                interest -= unadvInterest + beginBalance * serviceFee * del * (1 - delAdvInt);

                var defaultedPrincipal = defPrin;
                var recoveryPrincipal = defaultedPrincipal - defaultedPrincipal * sev;

                // Calculate prepayment (unscheduled principal)
                // For ABS prepay: prepay = absRate * originalBalance, capped at available balance
                // For CPR/SMM: prepay = smm * (balance - scheduled principal)
                double unschedPrin;
                if (absTime != null)
                {
                    // ABS prepay: percentage of original balance per period (per-asset)
                    var absRate = absTime[assetIndex][period];
                    var maxPrepay = Math.Max(schedBal - schedPrin + unadvPrincipal - defPrin, 0);
                    unschedPrin = Math.Min(absRate * rawOriginalBalance[assetIndex], maxPrepay);
                }
                else
                {
                    // Standard SMM prepay: applied to balance after scheduled principal
                    // This matches the CPR convention where SMM is conditional on the balance
                    // not already scheduled to amortize, and ensures VPR output round-trips.
                    unschedPrin = Math.Max(schedBal - schedPrin + unadvPrincipal, 0) * smm;
                }
                var unscheduledPrincipal = unschedPrin;

                balance = schedBal - schedPrinMdr - defPrin - unschedPrin;
                var dqBal = balance * del;

                // Cleanup near maturity
                double cleanup = 0;
                if (balance < 4 && balance > 0 &&
                    rawOriginalDate[assetIndex] + rawOriginalAmortizationTerm[assetIndex] - absT < 3)
                {
                    cleanup = balance;
                    balance = 0;
                }

                var scheduledPrincipalOut = schedPrinMdr + cleanup;
                var effectiveServiceFee = (beginBalance + forbearanceAmt) * serviceFee;
                effectiveServiceFee -= effectiveServiceFee * del * (1 - delAdvInt);
                var netInterest = interest - effectiveServiceFee;

                // Forbearance handling
                double forbearanceRecovery = 0;
                double forbearanceLiquidated = 0;
                if (forbearanceAmt > 0)
                {
                    var beginForbearanceAmt = forbearanceAmt;
                    var forbRecov = forbearanceAmt * smm;
                    var forbearanceWritedown = forbearanceAmt * mdr;
                    forbearanceAmt -= forbRecov + forbearanceWritedown;

                    forbRecov *= forbRecovPpay >= 0 ? forbRecovPpay : 1;
                    forbRecov += forbearanceWritedown * (forbRecovDefault >= 0 ? forbRecovDefault : 1 - sev);

                    if (!hasCashflow && forbearanceAmt > 0)
                    {
                        var forbearanceRecoveryMaturity = forbearanceAmt * forbRecovMaturity;
                        forbRecov += forbearanceRecoveryMaturity;
                        forbearanceAmt = 0;
                    }

                    forbearanceRecovery = forbRecov;
                    forbearanceLiquidated = beginForbearanceAmt - forbearanceAmt;
                }

                double delinqBalance = 0;
                if (!hasCashflow && balance > 0 && unadvPrincipal > 0)
                {
                    defaultedPrincipal += balance;
                    balance = 0;
                }
                else
                {
                    delinqBalance = dqBal;
                }

                survivalFactor *= 1.0 - (mdr + smm);

                // Weighted average calculations
                var prevBeginBal = resultBeginBalance[period];
                if (prevBeginBal + beginBalance > 0)
                {
                    resultWALA[period] = (prevBeginBal * resultWALA[period] + beginBalance * age) /
                                         (prevBeginBal + beginBalance);
                    resultWAM[period] = (prevBeginBal * resultWAM[period] + beginBalance * (term - age)) /
                                        (prevBeginBal + beginBalance);
                }

                // Aggregate results into period arrays
                resultBeginBalance[period] += beginBalance;
                resultBalance[period] += balance;
                resultScheduledPrincipal[period] += scheduledPrincipalOut;
                resultUnscheduledPrincipal[period] += unscheduledPrincipal;
                resultInterest[period] += interest;
                resultNetInterest[period] += netInterest;
                resultServiceFee[period] += effectiveServiceFee;
                resultDefaultedPrincipal[period] += defaultedPrincipal;
                resultRecoveryPrincipal[period] += recoveryPrincipal;
                resultDelinqBalance[period] += delinqBalance;
                resultUnAdvancedPrincipal[period] += unadvPrincipal;
                resultUnAdvancedInterest[period] += unadvInterest;
                resultAdvancedPrincipal[period] += (schedPrinMdr + unadvPrincipal) * del * delAdvPrin;
                resultAdvancedInterest[period] += (interest + unadvInterest) * del * delAdvInt -
                                                  effectiveServiceFee * del * delAdvInt;
                resultForbearanceRecovery[period] += forbearanceRecovery;
                resultForbearanceLiquidated[period] += forbearanceLiquidated;
                resultAccumForbearance[period] += forbearanceAmt;

                // Handle end of projection
                if (absT == endTime && balance > 0)
                {
                    resultUnscheduledPrincipal[period] += balance;
                    balance = 0;
                    break;
                }
            }
        }

        results.ComputeNumberOfPeriods();
        return results;
    }

    private static double AmortizingPayment(double balance, double monthlyRate, int remainingTerm)
    {
        if (remainingTerm <= 0)
            return balance;
        if (monthlyRate <= 0)
            return balance / remainingTerm;

        return balance * (monthlyRate * Math.Pow(1 + monthlyRate, remainingTerm)) /
               (Math.Pow(1 + monthlyRate, remainingTerm) - 1);
    }
}