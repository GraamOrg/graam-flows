using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraamFlows.Api.Models;

// Rates on this contract are percents, as everywhere on this API: paymentRate 50.9 is 50.9%.
// Verbs, tests and phases are strings so that an unknown one is refused by name, not by the
// JSON binder. A figure the run needs is nullable here so that one left out is refused by name
// instead of binding to 0.

public class RevolvingRequest
{
    public RevolvingProgramDto Program { get; set; } = new();

    /// <summary>Record date of the last closed monthly period. It must be a month end.</summary>
    public DateTime StatementDate { get; set; }

    /// <summary>Day of the following month a period distributes on. 15 if not stated.</summary>
    public int? DistributionDay { get; set; }

    /// <summary>Months to run. Defaults to the length of <see cref="ActualPeriods" />.</summary>
    public int? Periods { get; set; }

    /// <summary>Principal receivables at the statement date. Required.</summary>
    public double? PoolBalance { get; set; }

    public RevolvingDriversDto? Drivers { get; set; }

    /// <summary>Printed monthly pool figures. Replay runs these, then any forecast months.</summary>
    public List<ActualPeriodDto>? ActualPeriods { get; set; }

    public List<RevolvingSeriesDto> Series { get; set; } = new();

    /// <summary>
    ///     Annual percent the funding accounts earn: a number or a vector. Accounts earn in replayed
    ///     months too, so a vector is read by run month, unlike the forecast drivers.
    /// </summary>
    public double[]? FundingEarningsRate { get; set; }

    /// <summary>
    ///     Curves by index, as <c>[monthOffset, rate]</c>. Offset 0 is the run's first month, replayed
    ///     or forecast, which is the month after <see cref="StatementDate" />.
    /// </summary>
    public Dictionary<string, List<double[]>>? MarketRates { get; set; }
}

public class RevolvingProgramDto
{
    public string? Name { get; set; }

    /// <summary>FINANCE or PRINCIPAL_UP_TO_NEW_DEFAULTS. Required.</summary>
    public string? RecoveryBucket { get; set; }

    /// <summary>PERIOD_START, AVERAGE or PERIOD_END. Required.</summary>
    public string? PoolMeasure { get; set; }

    /// <summary>Months between a test tripping and its action. Required if there are triggers.</summary>
    public int? ActionLag { get; set; }

    /// <summary>FROZEN_PERCENT or FROZEN_NUMERATOR. Required, with no default.</summary>
    public string? PrincipalFreeze { get; set; }

    /// <summary>Steps that run across a group each month. Absent means there are none.</summary>
    public List<RevolvingStepDto>? GroupSteps { get; set; }

    public List<RevolvingStepDto> FinanceSteps { get; set; } = new();
    public List<RevolvingStepDto> PrincipalSteps { get; set; } = new();
    public List<RevolvingTriggerDto>? Triggers { get; set; }
    public double? SeriesRequiredTransferorPct { get; set; }
    public double? RequiredTransferorPct { get; set; }

    /// <summary>Set on a de-linked note series: each series is then one tranche of one class.</summary>
    public RevolvingDelinkedDto? Delinked { get; set; }
}

public class RevolvingDelinkedDto
{
    /// <summary>The classes, senior first.</summary>
    public List<string>? ClassOrder { get; set; }

    public bool SegregatedFinance { get; set; }
}

public class RevolvingStepDto
{
    public string? Verb { get; set; }
    public string? Class { get; set; }
    public string? Fee { get; set; }
    public List<string>? From { get; set; }
    public List<string>? WhenPaid { get; set; }
    public List<string>? Phases { get; set; }

    /// <summary>On a note series, what REALLOCATE_PRINCIPAL pays: INTEREST or FEE.</summary>
    public string? Covers { get; set; }
}

public class RevolvingTriggerDto
{
    public string? Name { get; set; }
    public string? Metric { get; set; }

    /// <summary>Months in the rolling average.</summary>
    public int Window { get; set; }

    /// <summary>BELOW or ABOVE.</summary>
    public string? Comparison { get; set; }

    public double? Level { get; set; }
    public string? AgainstMetric { get; set; }

    /// <summary>EARLY_AMORTIZATION or FUND_RESERVE.</summary>
    public string? Action { get; set; }

    public int? CurePeriods { get; set; }
    public double? ReserveAmount { get; set; }
}

public class RevolvingSeriesDto
{
    public string? Name { get; set; }
    public string? Group { get; set; }
    public bool PrincipalSharing { get; set; }
    public bool ExcessSharing { get; set; }
    public List<RevolvingClassDto> Classes { get; set; } = new();
    public List<RevolvingFeeDto>? Fees { get; set; }
    public List<string>? WritedownOrder { get; set; }
    public List<string>? FundedClasses { get; set; }
    public DateTime? AccumulationStart { get; set; }
    public DateTime? ExpectedDate { get; set; }

    /// <summary>Controlled amount: a number, or one per accumulation month.</summary>
    public double[]? ControlledAmount { get; set; }

    public DateTime? FirstPayDate { get; set; }

    /// <summary>The day the series closed: the first distribution's interest runs from here.</summary>
    public DateTime? ClosingDate { get; set; }

    public double? RequiredReserve { get; set; }

    /// <summary>On a tranche: percent of its adjusted amount it requires of each junior class.</summary>
    public Dictionary<string, double>? RequiredSubordination { get; set; }

    /// <summary>Where the series stands at the statement date.</summary>
    public RevolvingSeriesStateDto? State { get; set; }
}

public class RevolvingSeriesStateDto
{
    public string? Phase { get; set; }

    /// <summary>Percent. Required once a series has left revolving, under FROZEN_PERCENT.</summary>
    public double? FrozenPrincipalPct { get; set; }

    /// <summary>Dollars. Required once a series has left revolving, under FROZEN_NUMERATOR.</summary>
    public double? FrozenNumerator { get; set; }

    public double? FundingAccount { get; set; }
    public double? FundingDeficit { get; set; }
    public double? Reserve { get; set; }
    public int? AccumulationMonthsRun { get; set; }

    /// <summary>On a tranche: its nominal liquidation amount at the close of the month before.</summary>
    public double? PriorNumerator { get; set; }

    /// <summary>On a tranche: its funding account at the close of the month before.</summary>
    public double? PriorFunding { get; set; }

    /// <summary>On a tranche: what it has used of each junior class.</summary>
    public Dictionary<string, double>? Usage { get; set; }

    /// <summary>Finance waiting for a series' first waterfall.</summary>
    public double? CarriedFinance { get; set; }

    /// <summary>Each fee's accrued and unpaid amount, by fee name.</summary>
    public Dictionary<string, double>? FeesDue { get; set; }

    /// <summary>The tests already tripped, by test name.</summary>
    public Dictionary<string, RevolvingTriggerStateDto>? TriggerState { get; set; }

    public RevolvingHistoryDto? History { get; set; }
}

/// <summary>A tripped test as one run closes it and the next opens it.</summary>
public class RevolvingTriggerStateDto
{
    public bool Latched { get; set; }

    /// <summary>Months after the run's first before it acts. 0 acts in the first month.</summary>
    public int MonthsToAct { get; set; }

    /// <summary>Good months so far towards a FUND_RESERVE test's cure.</summary>
    public int GoodMonths { get; set; }
}

/// <summary>A class as a run closed it: what the next request's class must state.</summary>
public class RevolvingClassClosingDto
{
    public double Balance { get; set; }
    public double OriginalBalance { get; set; }
    public double WrittenDown { get; set; }
    public double InterestShortfall { get; set; }
    public double DefaultDue { get; set; }
}

/// <summary>
///     A series as the run closed it. To cut a run, send <see cref="State" /> as the next request's
///     <c>state</c> and each of <see cref="Classes" /> onto its class. Nothing else about a series
///     may carry. What belongs to the run is the caller's to move on: <c>statementDate</c>, the pool,
///     and <c>marketRates</c>, <c>drivers</c> and <c>fundingEarningsRate</c>, whose first value is the
///     new run's first month. Unmoved, they replay the old run's first months.
/// </summary>
public class RevolvingClosingDto
{
    public RevolvingSeriesStateDto State { get; set; } = new();
    public Dictionary<string, RevolvingClassClosingDto> Classes { get; set; } = new();

    /// <summary>
    ///     Nothing owed and nothing held: no interest, fee, default or write-down, and no reserve,
    ///     funding account or carried finance. A next run can leave it out and nothing changes.
    /// </summary>
    public bool Retired { get; set; }

    /// <summary>
    ///     Still owed, and nothing in this run can pay it: no series it can be paid from has an amount
    ///     that takes an allocation, or holds cash. Trust collections belong to the transferor from
    ///     here. A loop that runs until the series are done must stop on retired or stranded, or it
    ///     waits forever. Send a stranded series on if a later series joins, since that can pay it.
    /// </summary>
    public bool Stranded { get; set; }
}

/// <summary>Monthly percents from the statements, oldest first, for the trigger tests.</summary>
public class RevolvingHistoryDto
{
    public double[]? PortfolioYield { get; set; }
    public double[]? BaseRate { get; set; }
    public double[]? ExcessSpread { get; set; }
}

public class RevolvingClassDto
{
    public string? Key { get; set; }

    /// <summary>Required.</summary>
    public double? Balance { get; set; }

    public double? OriginalBalance { get; set; }

    /// <summary>Required on a fixed class. A floating class ignores it.</summary>
    public double? Coupon { get; set; }

    /// <summary>The index of a floating class, which then pays index + margin.</summary>
    public string? Index { get; set; }

    /// <summary>Required on a floating class.</summary>
    public double? Margin { get; set; }

    /// <summary>
    ///     30/360 or Actual/360. Absent means 30/360 on a fixed class. A floating class must state it:
    ///     it moves every month's interest, and floaters are rarely 30/360.
    /// </summary>
    public string? DayCount { get; set; }

    public double? WrittenDown { get; set; }
    public double? InterestShortfall { get; set; }

    /// <summary>A default waiting for a series' first waterfall.</summary>
    public double? DefaultDue { get; set; }
}

public class RevolvingFeeDto
{
    public string? Name { get; set; }

    /// <summary>Annual percent of the adjusted invested amount. Required.</summary>
    public double? Rate { get; set; }
}

/// <summary>
///     Forecast drivers. Each is a number or a monthly vector, as a percent of the month's beginning
///     principal receivables, or <c>{"annual": ...}</c> for an annual percent that converts as
///     <c>annual × days / 365</c>. A vector is read by forecast month: its first value is the first
///     month after any <see cref="RevolvingRequest.ActualPeriods" />, and a short one holds its last.
///     A month's value must come to between 0% and 100% of beginning principal receivables.
/// </summary>
public class RevolvingDriversDto
{
    public RevolvingDriverDto? PaymentRate { get; set; }
    public RevolvingDriverDto? NewReceivablesRate { get; set; }
    public bool HoldPool { get; set; }
    public RevolvingDriverDto? Yield { get; set; }
    public RevolvingDriverDto? ChargeOffRate { get; set; }
    public RevolvingDriverDto? RecoveryRate { get; set; }
    public RevolvingDriverDto? DilutionRate { get; set; }
}

[JsonConverter(typeof(RevolvingDriverConverter))]
public class RevolvingDriverDto
{
    public double[] Values { get; set; } = Array.Empty<double>();
    public bool Annual { get; set; }
}

/// <summary>Reads a driver from a number, an array, or an object with one annual or monthly key.</summary>
public class RevolvingDriverConverter : JsonConverter<RevolvingDriverDto>
{
    public override RevolvingDriverDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return new RevolvingDriverDto { Values = ReadValues(ref reader) };

        var driver = new RevolvingDriverDto();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString();
            reader.Read();
            driver.Annual = key?.ToLowerInvariant() switch
            {
                "annual" => true,
                "monthly" => false,
                _ => throw new JsonException($"Unknown driver key '{key}'. Use 'annual' or 'monthly'.")
            };
            driver.Values = ReadValues(ref reader);
        }

        return driver;
    }

    public override void Write(Utf8JsonWriter writer, RevolvingDriverDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(value.Annual ? "annual" : "monthly");
        JsonSerializer.Serialize(writer, value.Values, options);
        writer.WriteEndObject();
    }

    private static double[] ReadValues(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return new[] { reader.GetDouble() };

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A driver is a number, an array of numbers, or an object with one key.");

        var values = new List<double>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            values.Add(reader.GetDouble());
        return values.ToArray();
    }
}

/// <summary>A printed month. The two collections, new receivables and charge-offs are required.</summary>
public class ActualPeriodDto
{
    public double? BeginningBalance { get; set; }
    public double? NewReceivables { get; set; }
    public double? PrincipalCollections { get; set; }

    /// <summary>Finance charge collections, excluding recoveries.</summary>
    public double? FinanceCollections { get; set; }

    public double Recoveries { get; set; }
    public double? ChargeOffs { get; set; }
    public double Dilution { get; set; }
    public double? EndingBalance { get; set; }
    public int? Days { get; set; }

    /// <summary>The pool the allocator divides by, where the trust prints one.</summary>
    public double? AllocationBalance { get; set; }

    public double? InterestAccountEarnings { get; set; }
    public double? PrincipalAccountEarnings { get; set; }
}

public class RevolvingResponse
{
    public List<RevolvingProgramPeriodDto> Periods { get; set; } = new();
    public Dictionary<string, RevolvingSeriesResultDto> Series { get; set; } = new();

    /// <summary>Class rows in the tranche output shape, keyed "series/class".</summary>
    public Dictionary<string, List<TrancheCashflowDto>> TrancheCashflows { get; set; } = new();

    /// <summary>What the run took as 0 because the request did not state it.</summary>
    public List<string> Disclosures { get; set; } = new();

    public MarketRateResolutionDto? MarketRateResolution { get; set; }
    public long ElapsedMs { get; set; }
}

public class RevolvingProgramPeriodDto
{
    public int Period { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime DistributionDate { get; set; }
    public int Days { get; set; }
    public int AccrualDays { get; set; }
    public double BeginningBalance { get; set; }
    public double NewReceivables { get; set; }
    public double PrincipalCollections { get; set; }
    public double FinanceCollections { get; set; }
    public double Recoveries { get; set; }
    public double RecoveriesToFinance { get; set; }
    public double RecoveriesToPrincipal { get; set; }
    public double ChargeOffs { get; set; }
    public double Dilution { get; set; }
    public double EndingBalance { get; set; }
    public double? LedgerBreak { get; set; }
    public double? RollBreak { get; set; }
    public double TransferorAmount { get; set; }
    public double TransferorFinance { get; set; }
    public double TransferorPrincipal { get; set; }
    public double CashIn { get; set; }
    public double CashOut { get; set; }
    public List<RevolvingReportingTestDto> ReportingTests { get; set; } = new();
    public RevolvingNotesDto? Notes { get; set; }
}

/// <summary>A de-linked note series' month as a whole.</summary>
public class RevolvingNotesDto
{
    public double Numerator { get; set; }
    public double PrincipalNumerator { get; set; }
    public double FinanceAllocation { get; set; }
    public double PrincipalAllocation { get; set; }
    public double InvestorDefault { get; set; }
    public double FeesPaid { get; set; }
    public double InterestPaid { get; set; }
    public double AccountEarnings { get; set; }
    public double SegregatedFinance { get; set; }
    public double ExcessDollars { get; set; }
    public double UncoveredDefault { get; set; }
    public double UnchargedDefault { get; set; }
    public double PrincipalReallocated { get; set; }
    public double FinanceYieldPct { get; set; }
    public double NetLossPct { get; set; }
    public double PortfolioYieldPct { get; set; }
    public double CouponPct { get; set; }
    public double BaseRatePct { get; set; }
    public double ExcessSpreadPct { get; set; }
}

/// <summary>What a de-linked tranche did in the month.</summary>
public class RevolvingTrancheDto
{
    public double Numerator { get; set; }
    public double OpeningNominalLiquidation { get; set; }
    public double NextNumerator { get; set; }
    public double NextPriorFunding { get; set; }
    public Dictionary<string, double> RequiredSubordination { get; set; } = new();
    public Dictionary<string, double> UsageThisMonth { get; set; } = new();
    public Dictionary<string, double> Usage { get; set; } = new();
}

public class RevolvingReportingTestDto
{
    public string Name { get; set; } = "";
    public double Required { get; set; }
    public double Actual { get; set; }
    public double Headroom { get; set; }
}

public class RevolvingSeriesResultDto
{
    public List<RevolvingSeriesPeriodDto> Periods { get; set; } = new();
    public List<RevolvingPhaseMoveDto> PhasePath { get; set; } = new();
    public RevolvingFirstTripDto? FirstTrip { get; set; }

    /// <summary>The series as the run closed it. A next run opens on this.</summary>
    public RevolvingClosingDto Closing { get; set; } = new();
}

public class RevolvingPhaseMoveDto
{
    public string Phase { get; set; } = "";
    public int Period { get; set; }
    public DateTime DistributionDate { get; set; }
}

public class RevolvingFirstTripDto
{
    public string Test { get; set; } = "";
    public int Period { get; set; }
    public DateTime DistributionDate { get; set; }
}

public class RevolvingSeriesPeriodDto
{
    public int Period { get; set; }
    public DateTime DistributionDate { get; set; }
    public string Phase { get; set; } = "";
    public double FloatingPct { get; set; }
    public double PrincipalPct { get; set; }
    public double FinanceAllocation { get; set; }

    /// <summary>What group reallocation gave the series. Null if the program does not reallocate.</summary>
    public double? ReallocatedFinance { get; set; }

    public double PrincipalAllocation { get; set; }
    public double InvestorDefault { get; set; }
    public double InvestedAmount { get; set; }
    public double AdjustedInvested { get; set; }
    public double FundingAccount { get; set; }
    public double FundingEarnings { get; set; }
    public double FundingDeposit { get; set; }
    public double FundingDeficit { get; set; }
    public double FundingRelease { get; set; }
    public double Reserve { get; set; }
    public double ReserveRequired { get; set; }
    public double ReserveDeposit { get; set; }
    public double ReserveRelease { get; set; }
    public double CarriedFinance { get; set; }
    public double FeesAccrued { get; set; }
    public double FeesPaid { get; set; }
    public double SharedPrincipalIn { get; set; }
    public double SharedPrincipalOut { get; set; }
    public double SharedExcessIn { get; set; }
    public double SharedExcessOut { get; set; }
    public double PortfolioYieldPct { get; set; }
    public double BaseRatePct { get; set; }
    public double ExcessSpreadPct { get; set; }
    public double ExcessDollars { get; set; }
    public List<RevolvingTriggerReadingDto> Triggers { get; set; } = new();
    public double? FrozenPrincipalPct { get; set; }
    public double? FrozenNumerator { get; set; }
    public RevolvingTrancheDto? Tranche { get; set; }
}

public class RevolvingTriggerReadingDto
{
    public string Name { get; set; } = "";

    /// <summary>NOT_EVALUATED until the test has a full window, then PASS or FAIL.</summary>
    public string Status { get; set; } = "";

    public double? Value { get; set; }
    public double? Reference { get; set; }
    public double? Headroom { get; set; }
    public bool Latched { get; set; }
    public bool Acting { get; set; }
}
