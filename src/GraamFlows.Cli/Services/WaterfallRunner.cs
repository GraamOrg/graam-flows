using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Factories;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.RulesEngine;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Cli.Services;

public class WaterfallResult
{
    public Dictionary<string, List<TrancheCashflowDto>> TrancheCashflows { get; set; } = new();
    public CollateralCashflows? CollateralCashflows { get; set; }
    public List<TriggerResultDto> TriggerResults { get; set; } = new();
    public DateTime? TerminationDate { get; set; }
    public WaterfallSummaryDto Summary { get; set; } = new();
}

public class WaterfallRunner
{
    public WaterfallResult Run(
        DealModelFile dealModel,
        List<IAsset> assets,
        DateTime projectionDate,
        double cpr,
        double cdr,
        double sev,
        double dq,
        Dictionary<string, FactorEntry>? factors = null,
        bool runToCall = false,
        bool useAbsPrepayment = false)
    {
        // Propagate closing date from top-level DealModelFile to DealDto
        if (dealModel.ClosingDate.HasValue && !dealModel.Deal.ClosingDate.HasValue)
            dealModel.Deal.ClosingDate = dealModel.ClosingDate;

        // Build deal structure from DTO
        var deal = BuildDeal(dealModel.Deal, projectionDate, factors);
        foreach (var asset in assets)
            deal.Assets.Add(asset);

        // Create assumptions
        var anchorAbsT = DateUtil.CalcAbsT(projectionDate);

        // Extract weighted average remaining term (WAM) for ABS-to-SMM amortization adjustment
        // This ensures the SMM conversion accounts for balance declining from scheduled amortization
        var wam = dealModel.PoolStratification?.WeightedAverageRemainingTerm.HasValue == true
            ? (int)Math.Round(dealModel.PoolStratification.WeightedAverageRemainingTerm.Value)
            : 0;

        // Use ABS prepayment convention for Auto ABS deals (prepay as % of original balance)
        // Otherwise use standard CPR convention (prepay as % of current balance)
        var assumps = useAbsPrepayment
            ? DealLevelAssumptions.CreateAbsAssumptions(projectionDate, anchorAbsT, cpr, cdr, sev, dq, 0, wam)
            : DealLevelAssumptions.CreateConstAssumptions(projectionDate, anchorAbsT, cpr, cdr, sev, dq);

        // Enable clean-up call trigger if requested
        if (runToCall)
            assumps.RunToCall = true;

        // Create rate provider
        var rateProvider = new ConstantRateProvider(5.0);

        // Create CfCore and generate cashflows
        var cfCore = new CfCore(projectionDate, deal);
        var collateralCashflows = cfCore.GenerateAssetCashflows(rateProvider, assumps);
        var dealCashflows = cfCore.GenerateTrancheCashflows(collateralCashflows, rateProvider, assumps);

        // Convert to result
        return ConvertToResult(dealCashflows, collateralCashflows);
    }

    private static IDeal BuildDeal(DealDto dto, DateTime factorDate, Dictionary<string, FactorEntry>? factors = null)
    {
        var deal = new Deal(dto.DealName, factorDate);
        // Always use ComposableStructure as it's the only supported engine
        deal.CashflowEngine = "ComposableStructure";
        deal.WaterfallType = "ComposableStructure";
        deal.InterestTreatment = dto.InterestTreatment ?? "Guaranteed";

        var dealStructures = dto.DealStructures ?? dto.ClassGroups;
        var dealVariables = dto.DealVariables ?? dto.Variables;

        // Build tranches
        foreach (var trancheDto in dto.Tranches)
        {
            var effectiveFactor = trancheDto.Factor;
            var effectiveOriginalBalance = trancheDto.OriginalBalance;

            if (factors != null && factors.TryGetValue(trancheDto.TrancheName, out var factorEntry))
            {
                if (factorEntry.Balance.HasValue)
                {
                    effectiveOriginalBalance = factorEntry.Balance.Value;
                    effectiveFactor = 1.0;
                }
                else if (factorEntry.Factor.HasValue)
                {
                    effectiveFactor = factorEntry.Factor.Value;
                }
            }

            var tranche = new Tranche
            {
                TrancheName = trancheDto.TrancheName,
                DealName = dto.DealName,
                OriginalBalance = effectiveOriginalBalance,
                Factor = effectiveFactor,
                CouponType = trancheDto.CouponType,
                FixedCoupon = trancheDto.FixedCoupon ?? 0,
                FloaterSpread = trancheDto.FloaterSpread ?? 0,
                FloaterIndex = trancheDto.FloaterIndex,
                Cap = trancheDto.Cap,
                Floor = trancheDto.Floor,
                PayFrequency = trancheDto.PayFrequency,
                PayDelay = trancheDto.PayDelay,
                PayDay = trancheDto.PayDay,
                DayCount = trancheDto.DayCount,
                BusinessDayConvention = trancheDto.BusinessDayConvention,
                CashflowType = trancheDto.CashflowType,
                TrancheType = MapTrancheType(trancheDto.TrancheType),
                ClassReference = trancheDto.ClassReference ?? trancheDto.TrancheName,
                FirstPayDate = trancheDto.FirstPayDate ?? factorDate,
                StatedMaturityDate = trancheDto.StatedMaturityDate ?? trancheDto.LegalMaturityDate ?? factorDate.AddYears(10),
                LegalMaturityDate = trancheDto.LegalMaturityDate ?? trancheDto.StatedMaturityDate?.AddYears(2) ?? factorDate.AddYears(12),
                FirstSettleDate = dto.ClosingDate.HasValue
                    ? dto.ClosingDate.Value
                    : trancheDto.FirstPayDate.HasValue
                        ? trancheDto.FirstPayDate.Value.AddMonths(-1)
                        : factorDate,
                HolidayCalendar = "Settlement",
                CouponFormula = trancheDto.CouponFormula,
                Deal = deal,
                ReserveConfig = trancheDto.ReserveConfig != null
                    ? new ReserveAccountConfig
                    {
                        TargetPct = trancheDto.ReserveConfig.TargetPct,
                        TargetBase = trancheDto.ReserveConfig.TargetBase ?? "CutoffPoolBalance",
                        CutoffPoolBalance = trancheDto.ReserveConfig.CutoffPoolBalance ?? dto.BalanceAtIssuance ?? 0,
                        CapAtNoteBalance = trancheDto.ReserveConfig.CapAtNoteBalance ?? true
                    }
                    : null
            };

            deal.Tranches.Add(tranche);
        }

        // Build deal structures
        if (dealStructures != null && dealStructures.Any())
            foreach (var dsDto in dealStructures)
            {
                var ds = new DealStructure
                {
                    DealName = dto.DealName,
                    ClassGroupName = dsDto.ClassGroupName,
                    SubordinationOrder = dsDto.SubordinationOrder,
                    PayFrom = dsDto.PayFrom,
                    GroupNum = dsDto.GroupNum,
                    ExchangableTranche = dsDto.ExchangableTranche,
                    ClassTags = dsDto.ClassTags
                };
                deal.DealStructures.Add(ds);
            }
        else
            foreach (var trancheDto in dto.Tranches.OrderBy(t => t.SubordinationOrder))
            {
                var ds = new DealStructure
                {
                    DealName = dto.DealName,
                    ClassGroupName = trancheDto.ClassReference ?? trancheDto.TrancheName,
                    SubordinationOrder = trancheDto.SubordinationOrder,
                    PayFrom = "Sequential",
                    GroupNum = trancheDto.GroupNum
                };
                deal.DealStructures.Add(ds);
            }

        // Build triggers
        if (dto.Triggers != null && dto.Triggers.Any())
            foreach (var triggerDto in dto.Triggers)
            {
                var trigger = new DealTrigger
                {
                    DealName = dto.DealName,
                    TriggerName = triggerDto.TriggerName,
                    TriggerType = triggerDto.TriggerType,
                    TriggerFormula = triggerDto.TriggerFormula,
                    TriggerParam = triggerDto.TriggerParam,
                    TriggerParam2 = triggerDto.TriggerParam2,
                    IsMandatory = triggerDto.IsMandatory,
                    PossibleValues = triggerDto.PossibleValues,
                    GroupNum = triggerDto.GroupNum
                };
                deal.DealTriggers.Add(trigger);
            }

        // Build pay rules
        var payRuleDtos = dto.PayRules ?? new List<PayRuleDto>();

        if (dto.Waterfall != null)
        {
            var generatedRules = WaterfallBuilder.BuildPayRules(dto.Waterfall);
            payRuleDtos = payRuleDtos.Concat(generatedRules).ToList();
        }

        if (dto.UnifiedWaterfall != null)
        {
            UnifiedWaterfallBuilder.ValidateSteps(dto.UnifiedWaterfall, dto.DealName);

            // Always use ComposableStructure as it's the only supported engine
            deal.CashflowEngine = "ComposableStructure";
            deal.WaterfallType = "ComposableStructure";
            if (dto.UnifiedWaterfall.ExecutionOrder != null && dto.UnifiedWaterfall.ExecutionOrder.Any())
                deal.ExecutionOrder = dto.UnifiedWaterfall.ExecutionOrder;

            if (dealStructures == null || !dealStructures.Any())
            {
                deal.DealStructures.Clear();
                var generatedStructures =
                    UnifiedWaterfallBuilder.BuildDealStructures(dto.UnifiedWaterfall, dto.Tranches);
                foreach (var dsDto in generatedStructures)
                {
                    var ds = new DealStructure
                    {
                        DealName = dto.DealName,
                        ClassGroupName = dsDto.ClassGroupName,
                        SubordinationOrder = dsDto.SubordinationOrder,
                        PayFrom = dsDto.PayFrom,
                        GroupNum = dsDto.GroupNum
                    };
                    deal.DealStructures.Add(ds);
                }
            }

            var unifiedRules = UnifiedWaterfallBuilder.BuildPayRules(dto.UnifiedWaterfall);
            payRuleDtos = payRuleDtos.Concat(unifiedRules).ToList();
        }

        if (payRuleDtos.Any())
        {
            var order = 0;
            foreach (var ruleDto in payRuleDtos.OrderBy(r => r.Priority))
            {
                var rule = new PayRule
                {
                    DealName = dto.DealName,
                    RuleName = ruleDto.RuleName,
                    ClassGroupName = ruleDto.ClassGroupName,
                    Formula = ruleDto.Formula,
                    RuleExecutionOrder = order++
                };
                deal.PayRules.Add(rule);
            }
        }

        // Build deal variables
        if (dealVariables != null && dealVariables.Any())
            foreach (var varDto in dealVariables)
            {
                var variable = new DealVariables
                {
                    DealName = dto.DealName,
                    VariableName = varDto.VariableName,
                    VariableValue = varDto.VariableValue,
                    VariableValue2 = varDto.VariableValue2,
                    GroupNum = varDto.GroupNum,
                    IsForecastable = varDto.IsForecastable
                };
                deal.DealVariables.Add(variable);
            }

        // Set OC target config
        var ocTarget = dto.UnifiedWaterfall?.Steps
            .FirstOrDefault(s => s.Type.Equals("EXCESS_TURBO", StringComparison.OrdinalIgnoreCase))?.OcTarget;
        if (ocTarget != null)
        {
            var floorAmt = ocTarget.FloorAmt;
            if (floorAmt == 0 && ocTarget.FloorPct.HasValue && ocTarget.CutoffBalance.HasValue)
                floorAmt = ocTarget.FloorPct.Value * ocTarget.CutoffBalance.Value;

            // Only use initial pool balance when explicitly requested (useInitialBalance: true).
            // Default: use current pool balance per standard prospectus language like
            // "X% of aggregate Collateral Balance as of the end of the related collection period"
            double? initialPoolBalance = null;
            if (ocTarget.UseInitialBalance)
                initialPoolBalance = ocTarget.CutoffBalance ?? dto.BalanceAtIssuance;

            deal.OcTargetConfig = new OcTargetConfig
            {
                TargetPct = ocTarget.TargetPct,
                FloorAmt = floorAmt,
                InitialPoolBalance = initialPoolBalance > 0 ? initialPoolBalance : null,
                FormulaType = ocTarget.FormulaType ?? "max"
            };
        }

        // Build scheduled variables
        if (dto.ScheduledVariables != null && dto.ScheduledVariables.Any())
            foreach (var schedDto in dto.ScheduledVariables)
            {
                var schedVar = new ScheduledVariable
                {
                    DealName = dto.DealName,
                    ScheduleVariableName = schedDto.VariableName,
                    BeginDate = schedDto.BeginDate,
                    EndDate = schedDto.EndDate,
                    ValueNum = schedDto.Value,
                    GroupNum = schedDto.GroupNum.ToString()
                };
                deal.ScheduledVariables.Add(schedVar);
            }

        // Build expenses
        if (dto.Expenses != null && dto.Expenses.Any())
            foreach (var expDto in dto.Expenses)
            {
                var expenseTranche = new GraamFlows.Waterfall.Tranche(false)
                {
                    TrancheName = expDto.ExpenseName,
                    DealName = dto.DealName,
                    OriginalBalance = 0,
                    Factor = 1.0,
                    CouponType = "Formula",
                    CouponTypeEnum = CouponType.Formula,
                    CashflowType = "Expense",
                    CashflowTypeEnum = CashflowType.Expense,
                    CouponFormula = expDto.Formula,
                    TrancheType = "Reference",
                    TrancheTypeEnum = TrancheTypeEnum.Reference,
                    ClassReference = expDto.ExpenseName,
                    PayFrequency = 12,
                    PayDelay = 0,
                    PayDay = 25,
                    DayCount = "30/360",
                    BusinessDayConvention = "Following",
                    HolidayCalendar = "Settlement",
                    Deal = deal
                };
                deal.Tranches.Add(expenseTranche);

                var expenseStructure = new DealStructure
                {
                    DealName = dto.DealName,
                    ClassGroupName = expDto.ExpenseName,
                    SubordinationOrder = 0,
                    PayFrom = "Expense",
                    GroupNum = expDto.GroupNum.ToString()
                };
                deal.DealStructures.Add(expenseStructure);
            }

        // Build exchange shares
        if (dto.ExchangeShares != null && dto.ExchangeShares.Any())
            foreach (var exDto in dto.ExchangeShares)
            foreach (var share in exDto.Shares)
            {
                var exchShare = new ExchShare
                {
                    DealName = dto.DealName,
                    ClassGroupName = exDto.ExchangeTranche,
                    TrancheName = share.TrancheName,
                    Quantity = share.ShareAmount
                };
                deal.ExchShares.Add(exchShare);
            }

        deal.BalanceAtIssuance = dto.BalanceAtIssuance ?? deal.Tranches.Sum(t => t.OriginalBalance);
        deal.RuleAssembly = RulesBuilder.CompileRules(deal);

        return deal;
    }

    private static WaterfallResult ConvertToResult(DealCashflows dealCashflows, CollateralCashflows collateralCashflows)
    {
        var result = new WaterfallResult
        {
            CollateralCashflows = collateralCashflows,
            Summary = new WaterfallSummaryDto
            {
                TranchesSummary = new Dictionary<string, TrancheSummaryDto>()
            }
        };

        // Convert tranche cashflows
        foreach (var trancheCf in dealCashflows.TrancheCashflows)
        {
            if (trancheCf.Key.TrancheTypeEnum == TrancheTypeEnum.Certificate)
                continue;

            var trancheName = trancheCf.Key.TrancheName;
            var cashflowList = new List<TrancheCashflowDto>();
            var period = 0;

            foreach (var cf in trancheCf.Value.Cashflows.OrderBy(c => c.Key))
            {
                period++;
                cashflowList.Add(new TrancheCashflowDto
                {
                    Period = period,
                    CashflowDate = cf.Value.CashflowDate,
                    BeginBalance = cf.Value.BeginBalance,
                    Balance = cf.Value.Balance,
                    ScheduledPrincipal = cf.Value.ScheduledPrincipal,
                    UnscheduledPrincipal = cf.Value.UnscheduledPrincipal,
                    Interest = cf.Value.Interest,
                    Coupon = cf.Value.Coupon,
                    EffectiveCoupon = cf.Value.EffectiveCoupon,
                    Expense = cf.Value.Expense,
                    ExpenseShortfall = cf.Value.ExpenseShortfall,
                    Writedown = cf.Value.Writedown,
                    CumWritedown = cf.Value.CumWritedown,
                    Factor = cf.Value.Factor,
                    CreditSupport = cf.Value.CreditSupport,
                    BeginCreditSupport = cf.Value.BeginCreditSupport,
                    InterestShortfall = cf.Value.InterestShortfall,
                    AccumInterestShortfall = cf.Value.AccumInterestShortfall,
                    InterestShortfallPayback = cf.Value.InterestShortfallPayback,
                    ExcessInterest = cf.Value.ExcessInterest,
                    IndexValue = cf.Value.IndexValue,
                    FloaterMargin = cf.Value.FloaterMargin,
                    AccrualDays = cf.Value.AccrualDays,
                    IsLockedOut = cf.Value.IsLockedOut
                });
            }

            result.TrancheCashflows[trancheName] = cashflowList;

            var lastCf = cashflowList.LastOrDefault();
            result.Summary.TranchesSummary[trancheName] = new TrancheSummaryDto
            {
                TotalPrincipal = cashflowList.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal),
                TotalInterest = cashflowList.Sum(c => c.Interest),
                TotalExpense = cashflowList.Sum(c => c.Expense),
                TotalWritedown = cashflowList.Sum(c => c.Writedown),
                FinalBalance = lastCf?.Balance ?? 0,
                FinalFactor = lastCf?.Factor ?? 0
            };
        }

        result.Summary.TotalPeriods = result.TrancheCashflows.Values.FirstOrDefault()?.Count ?? 0;

        // Convert class cashflows for expense/certificate/reserve tranches
        foreach (var classCf in dealCashflows.ClassCashflows)
        {
            if (classCf.Key.CashflowTypeEnum != CashflowType.Expense &&
                classCf.Key.CashflowTypeEnum != CashflowType.Reserve &&
                classCf.Key.TrancheTypeEnum != TrancheTypeEnum.Certificate &&
                classCf.Key.TrancheTypeEnum != TrancheTypeEnum.CapFundsReserve)
                continue;

            var trancheName = classCf.Key.TrancheName;
            if (result.TrancheCashflows.ContainsKey(trancheName))
                continue;

            var cashflowList = new List<TrancheCashflowDto>();
            var period = 0;

            foreach (var cf in classCf.Value.Cashflows.OrderBy(c => c.Key))
            {
                period++;
                cashflowList.Add(new TrancheCashflowDto
                {
                    Period = period,
                    CashflowDate = cf.Value.CashflowDate,
                    BeginBalance = cf.Value.BeginBalance,
                    Balance = cf.Value.Balance,
                    ScheduledPrincipal = cf.Value.ScheduledPrincipal,
                    UnscheduledPrincipal = cf.Value.UnscheduledPrincipal,
                    Interest = cf.Value.Interest,
                    Expense = cf.Value.Expense,
                    Writedown = cf.Value.Writedown,
                    Factor = cf.Value.Factor
                });
            }

            if (cashflowList.Any() || classCf.Key.TrancheTypeEnum == TrancheTypeEnum.Certificate)
            {
                result.TrancheCashflows[trancheName] = cashflowList;

                var lastCf = cashflowList.LastOrDefault();
                result.Summary.TranchesSummary[trancheName] = new TrancheSummaryDto
                {
                    TotalPrincipal = cashflowList.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal),
                    TotalInterest = cashflowList.Sum(c => c.Interest),
                    TotalExpense = cashflowList.Sum(c => c.Expense),
                    TotalWritedown = cashflowList.Sum(c => c.Writedown),
                    FinalBalance = lastCf?.Balance ?? 0,
                    FinalFactor = lastCf?.Factor ?? 0
                };
            }
        }

        // Convert trigger results
        if (dealCashflows.TriggerResults != null)
        {
            var period = 0;
            foreach (var tr in dealCashflows.TriggerResults)
            {
                period++;
                result.TriggerResults.Add(new TriggerResultDto
                {
                    Period = period,
                    CashflowDate = tr.CashflowDate,
                    TriggerName = tr.TriggerName,
                    Triggered = tr.Passed,
                    Value = tr.ActualValue
                });
            }
        }

        if (dealCashflows.EarliestTerminationDates.Any())
            result.TerminationDate = dealCashflows.EarliestTerminationDates.Values.Min();

        return result;
    }

    /// <summary>
    /// Maps tranche type strings from JSON to valid TrancheTypeEnum values
    /// </summary>
    private static string MapTrancheType(string trancheType)
    {
        return trancheType?.ToLowerInvariant() switch
        {
            "reserve" => "CapFundsReserve",
            "modeling" => "Certificate",  // Modeling tranches (CERTIFICATE, residual) should be Certificate type
            "residual" => "Certificate",  // Residual tranches are also Certificate type
            "notional" => "Reference",
            _ => trancheType ?? "Offered"
        };
    }

}
