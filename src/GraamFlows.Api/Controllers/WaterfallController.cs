using System.Diagnostics;
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
using Microsoft.AspNetCore.Mvc;

namespace GraamFlows.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WaterfallController : ControllerBase
{
    private readonly ILogger<WaterfallController> _logger;

    public WaterfallController(ILogger<WaterfallController> logger)
    {
        _logger = logger;
    }

    [HttpPost]
    public ActionResult<WaterfallResponse> Execute([FromBody] WaterfallRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Waterfall: deal {DealName}, {TrancheCount} tranches, {CollateralCashflowCount} collateral cashflows, type {WaterfallType}",
            request.Deal.DealName, request.Deal.Tranches.Count, request.CollateralCashflows.Count, request.Deal.WaterfallType);

        try
        {
            // Build deal from DTO, applying factors if provided
            var deal = BuildDeal(request.Deal, request.ProjectionDate, request.Factors);

            // Convert collateral cashflows
            var collateralCashflows = ConvertCollateralCashflows(request.CollateralCashflows);

            // Create rate provider
            var rateProvider = BuildRateProvider(request.MarketRates);

            // Create assumptions
            var anchorAbsT = DateUtil.CalcAbsT(request.ProjectionDate);
            var assumps = DealLevelAssumptions.CreateConstAssumptions(request.ProjectionDate, anchorAbsT, 0, 0, 0);

            // Add trigger forecasts if provided
            if (request.TriggerForecasts != null)
                foreach (var tf in request.TriggerForecasts)
                    assumps.AddTriggerForecast(tf.TriggerName, tf.GroupNum.ToString(), tf.AlwaysTrigger);

            // Get waterfall engine
            var waterfallEngine = WaterfallFactory.GetWaterfall(deal.CashflowEngine);

            // Execute waterfall
            var firstProjDate = collateralCashflows.PeriodCashflows.FirstOrDefault()?.CashflowDate ??
                                request.ProjectionDate;
            var dealCashflows = waterfallEngine.Waterfall(deal, rateProvider, firstProjDate, collateralCashflows,
                assumps, new TrancheAllocator());

            // Convert to response
            var response = ConvertToResponse(dealCashflows);

            stopwatch.Stop();
            var totalTrancheCashflows = response.TrancheCashflows.Values.Sum(cfs => cfs.Count);
            _logger.LogInformation("Waterfall completed: {TrancheCount} tranches output, {TotalCashflows} total cashflows, {TotalPeriods} periods, elapsed {ElapsedMs}ms",
                response.TrancheCashflows.Count, totalTrancheCashflows, response.Summary.TotalPeriods, stopwatch.ElapsedMilliseconds);

            return Ok(response);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Waterfall failed after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return BadRequest(new { error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    private static IDeal BuildDeal(DealDto dto, DateTime factorDate, Dictionary<string, FactorEntry>? factors = null)
    {
        var deal = new Deal(dto.DealName, factorDate);
        deal.CashflowEngine = dto.WaterfallType;
        deal.InterestTreatment = dto.InterestTreatment ?? "Guaranteed";

        // Handle alias: use ClassGroups if DealStructures is null
        var dealStructures = dto.DealStructures ?? dto.ClassGroups;
        // Handle alias: use Variables if DealVariables is null
        var dealVariables = dto.DealVariables ?? dto.Variables;

        // Build tranches
        foreach (var trancheDto in dto.Tranches)
        {
            // Determine factor: Factors dictionary overrides trancheDto.Factor if provided
            var effectiveFactor = trancheDto.Factor;
            var effectiveOriginalBalance = trancheDto.OriginalBalance;

            if (factors != null && factors.TryGetValue(trancheDto.TrancheName, out var factorEntry))
            {
                if (factorEntry.Balance.HasValue)
                {
                    // For OC/Modeling tranches: use explicit balance, set factor to 1
                    effectiveOriginalBalance = factorEntry.Balance.Value;
                    effectiveFactor = 1.0;
                }
                else if (factorEntry.Factor.HasValue)
                {
                    // Factors dictionary overrides tranche-level factor
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
                TrancheType = trancheDto.TrancheType,
                ClassReference = trancheDto.ClassReference ?? trancheDto.TrancheName,
                FirstPayDate = trancheDto.FirstPayDate ?? factorDate,
                StatedMaturityDate = trancheDto.StatedMaturityDate ?? trancheDto.LegalMaturityDate ?? factorDate.AddYears(10),
                LegalMaturityDate = trancheDto.LegalMaturityDate ?? trancheDto.StatedMaturityDate?.AddYears(2) ?? factorDate.AddYears(12),
                // FirstSettleDate determines first-period accrual start.
                // Priority: deal ClosingDate > FirstPayDate - 1 month > factorDate
                FirstSettleDate = dto.ClosingDate
                    ?? (trancheDto.FirstPayDate.HasValue
                        ? trancheDto.FirstPayDate.Value.AddMonths(-1)
                        : factorDate),
                HolidayCalendar = "Settlement",
                CouponFormula = trancheDto.CouponFormula,
                Deal = deal
            };
            deal.Tranches.Add(tranche);
        }

        // Build deal structures (supports classGroups alias)
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
            // Auto-generate deal structures from tranches
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

        // Build pay rules - either from structured waterfall or direct DSL
        var payRuleDtos = dto.PayRules ?? new List<PayRuleDto>();

        // If structured waterfall is provided, generate PayRules from it
        if (dto.Waterfall != null)
        {
            var generatedRules = WaterfallBuilder.BuildPayRules(dto.Waterfall);
            payRuleDtos = payRuleDtos.Concat(generatedRules).ToList();
        }

        // If unified waterfall is provided, auto-generate DealStructures and PayRules
        if (dto.UnifiedWaterfall != null)
        {
            // Validate required steps
            UnifiedWaterfallBuilder.ValidateSteps(dto.UnifiedWaterfall, dto.DealName);

            // Determine waterfall type: ComposableStructure if explicitly set or ExecutionOrder provided
            if (dto.WaterfallType == "ComposableStructure" || dto.UnifiedWaterfall.ExecutionOrder != null)
            {
                deal.CashflowEngine = "ComposableStructure";
                deal.WaterfallType = "ComposableStructure";
                // Only set ExecutionOrder if explicitly provided (engine uses default if null/empty)
                if (dto.UnifiedWaterfall.ExecutionOrder != null && dto.UnifiedWaterfall.ExecutionOrder.Any())
                    deal.ExecutionOrder = dto.UnifiedWaterfall.ExecutionOrder;

                // Set waterfall order (interleaving mode)
                if (!string.IsNullOrEmpty(dto.UnifiedWaterfall.WaterfallOrder))
                {
                    deal.WaterfallOrder = dto.UnifiedWaterfall.WaterfallOrder.ToLowerInvariant() switch
                    {
                        "interestfirst" => WaterfallOrderEnum.InterestFirst,
                        "principalfirst" => WaterfallOrderEnum.PrincipalFirst,
                        _ => WaterfallOrderEnum.Standard
                    };
                }
            }
            else
            {
                // Default to UnifiedStructure for backward compatibility
                deal.CashflowEngine = "UnifiedStructure";
            }

            // Auto-generate DealStructures from tranches + writedown order
            // If no explicit structures provided, clear auto-generated ones and regenerate correctly
            if (dealStructures == null || !dealStructures.Any())
            {
                deal.DealStructures.Clear(); // Clear auto-generated from tranches
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

            // Generate PayRules from unified waterfall
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

        // Build deal variables (supports variables alias)
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

        // Set OC target config directly on deal (if EXCESS_TURBO step has ocTarget)
        var ocTarget = dto.UnifiedWaterfall?.Steps
            .FirstOrDefault(s => s.Type.Equals("EXCESS_TURBO", StringComparison.OrdinalIgnoreCase))?.OcTarget;
        if (ocTarget != null)
        {
            // Calculate floor amount if not provided directly
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

        // Build expenses as tranches with CashflowType=Expense
        if (dto.Expenses != null && dto.Expenses.Any())
            foreach (var expDto in dto.Expenses)
            {
                // Use Core Tranche which has settable enum properties (vs Domain.Tranche with computed)
                var expenseTranche = new GraamFlows.Waterfall.Tranche(false)
                {
                    TrancheName = expDto.ExpenseName,
                    DealName = dto.DealName,
                    OriginalBalance = 0,
                    Factor = 1.0,
                    CouponType = "Formula",
                    CouponTypeEnum = CouponType.Formula, // Required for formula compilation
                    CashflowType = "Expense",
                    CashflowTypeEnum = CashflowType.Expense, // Required for proper filtering
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

                // Create DealStructure with PayFrom=Expense so it's picked up by ExpenseClasses
                var expenseStructure = new DealStructure
                {
                    DealName = dto.DealName,
                    ClassGroupName = expDto.ExpenseName,
                    SubordinationOrder = 0, // Expenses paid first
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

        // Calculate balance at issuance (use provided value or sum from tranches)
        deal.BalanceAtIssuance = dto.BalanceAtIssuance ?? deal.Tranches.Sum(t => t.OriginalBalance);

        // Always compile rules - GenericExecutor requires RulesHost class with Reset() method
        // Even simple waterfalls need the base RulesHost infrastructure
        deal.RuleAssembly = RulesBuilder.CompileRules(deal);

        return deal;
    }

    private static CollateralCashflows ConvertCollateralCashflows(List<PeriodCashflowDto> dtos)
    {
        var periodCashflows = new List<PeriodCashflows>();

        foreach (var dto in dtos)
        {
            var cf = new PeriodCashflows
            {
                CashflowDate = dto.CashflowDate,
                GroupNum = dto.GroupNum,
                BeginBalance = dto.BeginBalance,
                Balance = dto.Balance,
                ScheduledPrincipal = dto.ScheduledPrincipal,
                UnscheduledPrincipal = dto.UnscheduledPrincipal,
                Interest = dto.Interest,
                NetInterest = dto.NetInterest,
                ServiceFee = dto.ServiceFee,
                DefaultedPrincipal = dto.DefaultedPrincipal,
                RecoveryPrincipal = dto.RecoveryPrincipal,
                CollateralLoss = dto.CollateralLoss,
                DelinqBalance = dto.DelinqBalance,
                ForbearanceRecovery = dto.ForbearanceRecovery,
                ForbearanceLiquidated = dto.ForbearanceLiquidated,
                WAC = dto.Wac,
                WAM = dto.Wam,
                WALA = dto.Wala,
                VPR = dto.Vpr,
                CDR = dto.Cdr,
                SEV = dto.Sev,
                DQ = dto.Dq,
                CumDefaultedPrincipal = dto.CumDefaultedPrincipal,
                CumCollateralLoss = dto.CumCollateralLoss
            };
            periodCashflows.Add(cf);
        }

        return new CollateralCashflows(periodCashflows);
    }

    private static IRateProvider BuildRateProvider(Dictionary<string, List<double[]>>? marketRates)
    {
        if (marketRates == null || !marketRates.Any())
            return new ConstantRateProvider(5.0);

        // Build a MarketData object mapping each instrument to its spot rate.
        // Input format: {"Sofr30Avg": [[term, rate], ...], "Libor3M": [[term, rate], ...]}
        // We use the shortest-term rate for each instrument as the current spot rate.
        var marketData = new MarketData();
        foreach (var (instName, points) in marketRates)
        {
            if (points == null || points.Count == 0) continue;

            // Use the shortest-term point as the spot rate
            var spotPoint = points.OrderBy(p => p[0]).First();
            if (spotPoint.Length < 2) continue;
            var rate = spotPoint[1];

            if (Enum.TryParse<MarketDataInstEnum>(instName, ignoreCase: true, out var inst))
                SetMarketDataRate(marketData, inst, rate);
        }

        return new ConstantRateProvider(marketData);
    }

    private static void SetMarketDataRate(MarketData md, MarketDataInstEnum inst, double rate)
    {
        switch (inst)
        {
            case MarketDataInstEnum.Libor1M: md.Libor1M = rate; break;
            case MarketDataInstEnum.Libor3M: md.Libor3M = rate; break;
            case MarketDataInstEnum.Libor6M: md.Libor6M = rate; break;
            case MarketDataInstEnum.Libor12M: md.Libor12M = rate; break;
            case MarketDataInstEnum.Sofr30Avg: md.Sofr30Avg = rate; break;
            case MarketDataInstEnum.Sofr90Avg: md.Sofr90Avg = rate; break;
            case MarketDataInstEnum.Sofr180Avg: md.Sofr180Avg = rate; break;
            case MarketDataInstEnum.SofrIndex: md.SofrIndex = rate; break;
            case MarketDataInstEnum.Swap2Y: md.Swap2Y = rate; break;
            case MarketDataInstEnum.Swap3Y: md.Swap3Y = rate; break;
            case MarketDataInstEnum.Swap5Y: md.Swap5Y = rate; break;
            case MarketDataInstEnum.Swap10Y: md.Swap10Y = rate; break;
            case MarketDataInstEnum.Swap30Y: md.Swap30Y = rate; break;
        }
    }

    private static WaterfallResponse ConvertToResponse(DealCashflows dealCashflows)
    {
        var response = new WaterfallResponse
        {
            TrancheCashflows = new Dictionary<string, List<TrancheCashflowDto>>(),
            TriggerResults = new List<TriggerResultDto>(),
            Summary = new WaterfallSummaryDto
            {
                TranchesSummary = new Dictionary<string, TrancheSummaryDto>()
            }
        };

        // Convert tranche cashflows (skip Certificate tranches - they're processed from ClassCashflows)
        foreach (var trancheCf in dealCashflows.TrancheCashflows)
        {
            // Certificate tranches track balance in ClassCashflows, not TrancheCashflows
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

            response.TrancheCashflows[trancheName] = cashflowList;

            // Calculate summary
            var lastCf = cashflowList.LastOrDefault();
            var origBal = cashflowList.FirstOrDefault()?.BeginBalance ?? 0;
            var totalWritedown = cashflowList.Sum(c => c.Writedown);
            response.Summary.TranchesSummary[trancheName] = new TrancheSummaryDto
            {
                TotalPrincipal = cashflowList.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal),
                TotalInterest = cashflowList.Sum(c => c.Interest),
                TotalExpense = cashflowList.Sum(c => c.Expense),
                TotalWritedown = totalWritedown,
                WritedownPct = origBal > 0 ? totalWritedown / origBal : 0,
                FinalBalance = lastCf?.Balance ?? 0,
                FinalFactor = lastCf?.Factor ?? 0
            };
        }

        response.Summary.TotalPeriods = response.TrancheCashflows.Values.FirstOrDefault()?.Count ?? 0;

        // Convert class cashflows for tranches that don't have DynamicTranches (expenses, certificates, reserves)
        foreach (var classCf in dealCashflows.ClassCashflows)
        {
            // Process expense, certificate, and reserve tranches (they only have DynamicClasses, not DynamicTranches)
            if (classCf.Key.CashflowTypeEnum != CashflowType.Expense &&
                classCf.Key.CashflowTypeEnum != CashflowType.Reserve &&
                classCf.Key.TrancheTypeEnum != TrancheTypeEnum.Certificate &&
                classCf.Key.TrancheTypeEnum != TrancheTypeEnum.CapFundsReserve)
                continue;

            var trancheName = classCf.Key.TrancheName;
            if (response.TrancheCashflows.ContainsKey(trancheName))
                continue; // Skip if already added

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

            // For Certificate tranches, always include even if no cashflows (to show balance tracking)
            // For Expense tranches, only include if they have cashflows
            if (cashflowList.Any() || classCf.Key.TrancheTypeEnum == TrancheTypeEnum.Certificate)
            {
                response.TrancheCashflows[trancheName] = cashflowList;

                // Calculate summary
                var lastCf = cashflowList.LastOrDefault();
                var origBal = cashflowList.FirstOrDefault()?.BeginBalance ?? 0;
                var totalWritedown = cashflowList.Sum(c => c.Writedown);
                response.Summary.TranchesSummary[trancheName] = new TrancheSummaryDto
                {
                    TotalPrincipal = cashflowList.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal),
                    TotalInterest = cashflowList.Sum(c => c.Interest),
                    TotalExpense = cashflowList.Sum(c => c.Expense),
                    TotalWritedown = totalWritedown,
                    WritedownPct = origBal > 0 ? totalWritedown / origBal : 0,
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
                response.TriggerResults.Add(new TriggerResultDto
                {
                    Period = period,
                    CashflowDate = tr.CashflowDate,
                    TriggerName = tr.TriggerName,
                    Triggered = tr.Passed,
                    Value = tr.ActualValue
                });
            }
        }

        // Get earliest termination date
        if (dealCashflows.EarliestTerminationDates.Any())
            response.TerminationDate = dealCashflows.EarliestTerminationDates.Values.Min();

        return response;
    }
}