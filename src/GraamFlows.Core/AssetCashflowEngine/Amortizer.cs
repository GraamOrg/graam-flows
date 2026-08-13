using GraamFlows.Objects.TypeEnum;

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
    /// of mdrTime[assetIndex][period] * currentBalance (percent-of-original default basis).</param>
    /// <param name="recoveryLag">Optional per-asset recovery lag in months (graam-harmony #3449).
    /// When an asset's lag is &gt; 0, the recovery on a period-t default is placed at period
    /// t + lag (the liquidation timeline) instead of period t. Recoveries whose lagged period falls
    /// beyond the projection horizon are truncated. Null (or all-zero) preserves same-period recovery.</param>
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
        double[][]? origMdrTime = null,
        int[]? recoveryLag = null)
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
        var rawAmortizationType = assetData.AmortizationType;
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
            var balance = rawCurrentBalance[assetIndex];
            var cashflowBalance = balance;
            var cashflowPrevBalance = balance;
            var ioTerm = rawIOTerm[assetIndex];
            var serviceFee = rawServiceFee[assetIndex] / 1200.0;
            // Principal-repayment style (0 = Amortizing, 1 = Bullet, 2 = PIK).
            var amortType = rawAmortizationType[assetIndex];
            var isBullet = amortType == (int)AmortizationType.Bullet;
            var isPik = amortType == (int)AmortizationType.Pik;
            var rateSteps = rawStepDatesCount[assetIndex];
            var nextRateStepDate = 100000;
            var forbearanceAmt = rawForbearanceAmt[assetIndex];
            var assetRecoveryLag = recoveryLag?[assetIndex] ?? 0;
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
                // Clamp hazards to [0, 1]. A monthly SMM/MDR outside this range
                // is a misconfiguration, and unclamped it lets the per-period
                // principal reductions (schedPrinMdr + defPrin + unschedPrin)
                // exceed the performing balance, driving `balance` negative.
                var smm = Math.Clamp(smmTime[assetIndex][period], 0.0, 1.0);
                var mdr = Math.Clamp(mdrTime[assetIndex][period], 0.0, 1.0);
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
                    else if (isPik)
                    {
                        // PIK: capitalize the coupon into the (contractual) balance
                        // instead of paying it in cash; principal repays only at
                        // maturity (balloon). cashflowPrevBalance is captured BEFORE
                        // capitalization so dqFactor stays consistent with the
                        // performing balance. The cash coupon is zeroed and the
                        // accrued amount is added back to the performing balance
                        // below (see capitalizedInterest).
                        cashflowPrevBalance = cashflowBalance;
                        cashflowBalance += interestPaid;
                        principal = age < term ? 0 : cashflowBalance;
                        cashflowBalance -= principal;

                        if (cashflowBalance <= 0)
                        {
                            cashflowBalance = 0;
                            hasCashflow = false;
                        }
                    }
                    else
                    {
                        // Bullet forces an interest-only scheduled payment until
                        // maturity, so (payment − interest) yields zero scheduled
                        // principal and the residual balloons at maturity via the
                        // age >= term branch. Amortizing keeps its level-pay
                        // scheduledPayment (unchanged).
                        var actualPayment = age < term
                            ? (isBullet ? interestPaid : scheduledPayment)
                            : cashflowBalance + interestPaid;
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

                // dqFactor re-scales the contractual schedule (interestPaid,
                // principal — tracked on cashflowBalance, which sees only
                // scheduled amortization) onto the actual performing balance
                // (schedBal, net of prior defaults/prepays).
                var dqFactor = cashflowPrevBalance > 0
                    ? schedBal / cashflowPrevBalance
                    : 1.0;
                if (double.IsNaN(dqFactor) || double.IsInfinity(dqFactor)) dqFactor = 1.0;

                var interest = interestPaid * dqFactor;
                var schedPrin = principal * dqFactor;

                // PIK: the period coupon is not paid in cash — it is capitalized
                // into the balance. Zero the cash interest and remember the accrued
                // amount so it can be added back to the performing balance below,
                // mirroring the contractual capitalization done above. Non-PIK
                // assets leave capitalizedInterest at zero (no-op).
                var capitalizedInterest = 0.0;
                if (isPik)
                {
                    capitalizedInterest = interest;
                    interest = 0.0;
                }

                // Reference calc standard (graam-harmony #3449), reconciled to the
                // byte-validated reference-engine oracle:
                //   * default is assessed on the BEGIN performing balance
                //     (defPrin = mdr · schedBal), and
                //   * scheduled principal is haircut by (1 − mdr).
                // Both are the ORIGINAL engine behavior; the oracle confirmed them
                // correct, so an earlier revision that moved default onto
                // schedBal − schedPrin and dropped the haircut was reverted.
                // The one genuine loss-path fix retained here is prepay/default
                // PARALLELISM: prepay is taken off the balance after (full)
                // scheduled principal WITHOUT subtracting the period's default.
                var balPost = Math.Max(schedBal - schedPrin, 0.0);

                // ORIGMDR: default dollars are a fraction of the ORIGINAL balance,
                // not the current performing balance. Capped at the performing
                // (begin) balance and re-expressed as an effective current-balance
                // hazard so every downstream use of `mdr` (scheduled-principal
                // haircut, forbearance writedown, defPrin) stays consistent.
                // Shariff 7/21/26 ORIGMDR likely should not be calculated in the
                // amortizer. MDR should be calculated from ORIGMDR upstream.
                if (origMdrTime?[assetIndex] != null)
                {
                    var origDefaultDollars = Math.Min(origMdrTime[assetIndex][period] * origBalance, schedBal);
                    mdr = schedBal > 0 ? origDefaultDollars / schedBal : 0.0;
                }

                var defPrin = mdr * schedBal;

                unadvInterest = interest * del * (1 - delAdvInt) - beginBalance * serviceFee * del * (1 - delAdvInt);
                var schedPrinMdr = schedPrin * (1 - mdr);
                unadvPrincipal = schedPrinMdr * del * (1 - delAdvPrin);
                schedPrinMdr -= unadvPrincipal;
                interest -= unadvInterest + beginBalance * serviceFee * del * (1 - delAdvInt);

                var defaultedPrincipal = defPrin;
                var recoveryPrincipal = defaultedPrincipal - defaultedPrincipal * sev;

                // Prepayment (unscheduled principal), assessed in PARALLEL with
                // default off the balance after (full) scheduled principal — the
                // period's default is NOT subtracted from the base (the
                // oracle-confirmed prepay/default parallelism).
                //   ABS : rate · original balance, capped at the balance left after
                //         scheduled principal and defaults (dollar-based auto-ABS,
                //         capacity-limited so defaults take priority).
                //   SMM : smm · (schedBal − schedPrin).
                double unschedPrin;
                if (absTime != null)
                {
                    var absRate = absTime[assetIndex][period];
                    var maxPrepay = Math.Max(balPost - defPrin, 0.0);
                    unschedPrin = Math.Min(absRate * rawOriginalBalance[assetIndex], maxPrepay);
                }
                else
                {
                    unschedPrin = smm * balPost;
                }
                var unscheduledPrincipal = unschedPrin;

                // capitalizedInterest is zero for every non-PIK asset, so this
                // reduces to the original balance recurrence. For PIK it grows the
                // performing balance by the (already-zeroed) cash coupon, matching
                // the contractual capitalization applied to cashflowBalance above.
                balance = schedBal - schedPrinMdr - defPrin - unschedPrin + capitalizedInterest;
                // Non-negative guard: reachable only under hazard misconfiguration
                // (e.g. mdr = 1). Well-formed inputs never trip it, so it does not
                // affect the tie-out.
                if (balance < 0) balance = 0;
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
                    // Balloon/maturity default of the residual balance. recoveryPrincipal
                    // was computed above from defPrin only, so this chunk must carry its
                    // own severity-adjusted recovery — otherwise it recovers zero
                    // regardless of the `sev` assumption.
                    defaultedPrincipal += balance;
                    recoveryPrincipal += balance * (1 - sev);
                    balance = 0;
                }
                else
                {
                    delinqBalance = dqBal;
                }

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
                // Recovery lag (graam-harmony #3449): a period-t default's recovery
                // lands at t + lag (the liquidation timeline). Recoveries whose
                // lagged period falls beyond the projection horizon are truncated.
                var recoveryPeriod = period + assetRecoveryLag;
                if (recoveryPeriod < maxPeriods)
                    resultRecoveryPrincipal[recoveryPeriod] += recoveryPrincipal;
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