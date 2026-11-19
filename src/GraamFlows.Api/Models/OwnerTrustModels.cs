namespace GraamFlows.Api.Models;

// An amortizing owner trust over an installment pool, on POST /api/revolving: program.ownerTrust,
// pool.model INSTALLMENT and one series whose classes are the notes. Rates, shares and percents are
// percents, as everywhere on this API: a discountRate of 7.8 is 7.8%. Amounts are dollars.

/// <summary>The trust's rules, and where it stands. Its presence is what makes a run an owner trust.</summary>
public class OwnerTrustDto
{
    /// <summary>Annual percent the adjusted pool is the present value at. Required.</summary>
    public double? DiscountRate { get; set; }

    /// <summary>Senior first. Required: every class sits in exactly one.</summary>
    public List<OwnerTrustTierDto>? Tiers { get; set; }

    /// <summary>The waterfall, in the documents' order. Required; the last is PAY_CERTIFICATE.</summary>
    public List<OwnerTrustStepDto>? Steps { get; set; }

    /// <summary>The amortization period starts on the payment date in this month. Absent: no scheduled end.</summary>
    public DateTime? RevolvingEnd { get; set; }

    /// <summary>Annual percent of the adjusted pool at the month's opening. Absent is 0.</summary>
    public double? ServicingRate { get; set; }

    /// <summary>Trustee and reviewer fees per month, in dollars. Absent is 0.</summary>
    public double? MonthlyFees { get; set; }

    /// <summary>Required.</summary>
    public OwnerTrustOcDto? Oc { get; set; }

    /// <summary>Dollars. Absent is 0.</summary>
    public double? RequiredReserve { get; set; }

    /// <summary>The amortization events tested. Absent tests nothing.</summary>
    public OwnerTrustTestsDto? Tests { get; set; }

    /// <summary>The certificateholders' optional acquisition and clean-up redemption. Absent: none.</summary>
    public OwnerTrustCleanUpDto? CleanUp { get; set; }

    /// <summary>What the acquisition account buys in forecast months. Absent buys nothing, disclosed.</summary>
    public OwnerTrustAcquisitionDto? Acquisition { get; set; }

    /// <summary>By forecast month: the pool passes its floor composition tests. Absent passes, disclosed.</summary>
    public List<bool>? FloorTestsPass { get; set; }

    /// <summary>
    ///     By forecast month: percent of the pool before the month's purchases 91 days or more
    ///     delinquent at the close. Absent holds the last month known, disclosed.
    /// </summary>
    public List<double>? Delinquency { get; set; }

    /// <summary>Annual percent the reserve account earns. Absent is 0, disclosed.</summary>
    public double? ReserveEarningsRate { get; set; }

    /// <summary>
    ///     The business days payment dates roll on: SETTLEMENT (the card programs' calendar) or
    ///     FEDERAL_RESERVE (with Juneteenth from 2022). Absent is SETTLEMENT.
    /// </summary>
    public string? Calendar { get; set; }

    /// <summary>Where the trust stands at the statement date. Absent: revolving, with empty accounts.</summary>
    public OwnerTrustStateDto? State { get; set; }

    /// <summary>Printed trust figures, parallel to pool.actualPeriods.</summary>
    public List<OwnerTrustActualDto>? ActualPeriods { get; set; }
}

public class OwnerTrustTierDto
{
    public string? Name { get; set; }

    /// <summary>Classes that rank together, pro rata to what each is owed.</summary>
    public List<string>? Classes { get; set; }

    /// <summary>On and after this payment date, priority principal pays the tier in full.</summary>
    public DateTime? FinalMaturity { get; set; }
}

public class OwnerTrustStepDto
{
    /// <summary>
    ///     PAY_FEES, PAY_SERVICING, PAY_INTEREST, PRIORITY_PRINCIPAL, REGULAR_PRINCIPAL,
    ///     ACCELERATED_PRINCIPAL, FUND_RESERVE, DEPOSIT_ACQUISITION, FUND_NEGATIVE_CARRY or PAY_CERTIFICATE.
    /// </summary>
    public string? Verb { get; set; }

    /// <summary>The tier PAY_INTEREST and PRIORITY_PRINCIPAL act on.</summary>
    public string? Tier { get; set; }
}

/// <summary>
///     While revolving the target is notes / (1 − pct) − notes; after, pct × the adjusted pool. The
///     floor tests choose the passing or failing percent. All four percents are required.
/// </summary>
public class OwnerTrustOcDto
{
    public double? RevolvingPct { get; set; }
    public double? RevolvingFailingPct { get; set; }
    public double? AmortizingPct { get; set; }
    public double? AmortizingFailingPct { get; set; }

    /// <summary>Dollars. Absent is 0.</summary>
    public double? Floor { get; set; }
}

/// <summary>Each limit is a percent. A test with no limit is not tested.</summary>
public class OwnerTrustTestsDto
{
    /// <summary>Sum of the window's monthly write-offs over each month's opening pool, times the multiplier.</summary>
    public double? WriteOffLimit { get; set; }

    /// <summary>Months. Absent is 3.</summary>
    public int? WriteOffWindow { get; set; }

    /// <summary>Absent is 4.</summary>
    public double? WriteOffMultiplier { get; set; }

    /// <summary>Average of the window's monthly delinquency ratios.</summary>
    public double? DelinquencyLimit { get; set; }

    /// <summary>Months. Absent is 3.</summary>
    public int? DelinquencyWindow { get; set; }

    /// <summary>The adjusted pool below this percent of the note balance, after payments.</summary>
    public double? ParityLimit { get; set; }

    /// <summary>Overcollateralization below target after payments. Absent is false.</summary>
    public bool? OcShortfall { get; set; }

    /// <summary>Months a shortfall from a change of percent has before it counts. Absent is 3.</summary>
    public int? OcGraceMonths { get; set; }

    /// <summary>While revolving: interest unpaid, or the reserve or negative carry short. Absent is false.</summary>
    public bool? RevolvingShortfalls { get; set; }
}

public class OwnerTrustCleanUpDto
{
    /// <summary>Percent of the closing pool at or below which it may be exercised. Required.</summary>
    public double? PoolFactor { get; set; }

    /// <summary>Dollars. Required.</summary>
    public double? ClosingPoolBalance { get; set; }

    /// <summary>POOL_FACE or NOTE_BALANCE. Absent is POOL_FACE.</summary>
    public string? PriceBasis { get; set; }

    /// <summary>Percent of the basis. Absent is 100.</summary>
    public double? Price { get; set; }

    /// <summary>Forecast payment dates after the first eligible one before it is exercised. Absent is 0.</summary>
    public int? DelayMonths { get; set; }

    /// <summary>Forecast: exercised on the payment date in this month, or the first eligible one after.</summary>
    public DateTime? Date { get; set; }
}

public class OwnerTrustAcquisitionDto
{
    /// <summary>Required: percentOfFace, or pvRate (cash is the present value at that rate).</summary>
    public InstallmentPriceDto? Price { get; set; }

    /// <summary>Required: level contracts, weights percents of the face bought summing to 100.</summary>
    public List<InstallmentTermDto>? Terms { get; set; }
}

/// <summary>Where a trust stands. A run's closing state is sent back as the next run's.</summary>
public class OwnerTrustStateDto
{
    /// <summary>REVOLVING, AMORTIZING or EARLY_AMORTIZATION. Absent is REVOLVING.</summary>
    public string? Phase { get; set; }

    /// <summary>The amortization event that has occurred. Required with EARLY_AMORTIZATION.</summary>
    public string? Event { get; set; }

    public double? Reserve { get; set; }
    public double? AcquisitionAccount { get; set; }
    public double? NegativeCarry { get; set; }

    /// <summary>The adjusted pool at the statement date. Absent values the opening pool at the discount rate.</summary>
    public double? AdjustedPool { get; set; }

    /// <summary>Monthly write-offs over each month's opening pool, percents, oldest first.</summary>
    public List<double>? WriteOffHistory { get; set; }

    /// <summary>Monthly delinquency ratios, percents, oldest first.</summary>
    public List<double>? DelinquencyHistory { get; set; }

    /// <summary>The overcollateralization percents of the months before, oldest first.</summary>
    public List<double>? OcPctHistory { get; set; }

    /// <summary>Payment dates the clean-up has been eligible on.</summary>
    public int? CleanUpEligibleMonths { get; set; }
}

/// <summary>A printed month. Each field left out is computed as in a forecast month.</summary>
public class OwnerTrustActualDto
{
    public double? Fees { get; set; }
    public bool? FloorTestsPass { get; set; }

    /// <summary>The printed write-off test value, percent.</summary>
    public double? WriteOffRatio { get; set; }

    /// <summary>The printed delinquency test value, percent.</summary>
    public double? DelinquencyRatio { get; set; }

    /// <summary>The month's own delinquency ratio, percent.</summary>
    public double? Delinquency { get; set; }

    /// <summary>Other cash into available funds, as printed. Absent is 0.</summary>
    public double? OtherFunds { get; set; }

    /// <summary>An event the statement reports that the pool cannot show; it starts the amortization period.</summary>
    public string? Event { get; set; }

    /// <summary>The clean-up deposit, where the statement prints one.</summary>
    public double? CleanUpDeposit { get; set; }

    /// <summary>The printed adjusted pool at the close, after purchases: the waterfall runs on it.</summary>
    public double? AdjustedPool { get; set; }
}

public class OwnerTrustResultDto
{
    public List<OwnerTrustPeriodDto> Periods { get; set; } = new();
    public List<OwnerTrustPhaseMoveDto> PhasePath { get; set; } = new();

    /// <summary>CLEAN_UP or NOTES_PAID where the trust ended in the run.</summary>
    public string? Ended { get; set; }

    public OwnerTrustClosingDto Closing { get; set; } = new();
}

public class OwnerTrustPhaseMoveDto
{
    public string Phase { get; set; } = "";
    public int Period { get; set; }
    public DateTime DistributionDate { get; set; }

    /// <summary>The amortization event behind the move. Absent for the scheduled end of revolving.</summary>
    public string? Event { get; set; }
}

/// <summary>Send state as the next run's ownerTrust.state, classes onto the series' classes and pool as pool.cohorts.</summary>
public class OwnerTrustClosingDto
{
    /// <summary>Absent once the trust has ended.</summary>
    public OwnerTrustStateDto? State { get; set; }

    public Dictionary<string, RevolvingClassClosingDto> Classes { get; set; } = new();
    public List<InstallmentCohortDto> Pool { get; set; } = new();
}

public class OwnerTrustPeriodDto
{
    public int Period { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime DistributionDate { get; set; }
    public bool Replayed { get; set; }
    public string Phase { get; set; } = "";
    public string? Event { get; set; }
    public double BeginningPool { get; set; }
    public double PoolBeforeAdditions { get; set; }
    public double EndingPool { get; set; }
    public double BeginningAdjustedPool { get; set; }
    public double AdjustedPoolBeforeAdditions { get; set; }
    public double EndingAdjustedPool { get; set; }
    public double Ysoc { get; set; }

    /// <summary>The run's own adjusted pool, where a printed one was stated.</summary>
    public double? ComputedAdjustedPool { get; set; }

    public double Collections { get; set; }
    public double WriteOffs { get; set; }
    public double RepurchaseCash { get; set; }
    public double OtherFunds { get; set; }
    public double ReserveEarnings { get; set; }
    public double CleanUpDeposit { get; set; }
    public double AvailableFunds { get; set; }
    public double Fees { get; set; }
    public double ServicingFee { get; set; }
    public bool FloorTestsPass { get; set; }

    /// <summary>Percent.</summary>
    public double OcTargetPct { get; set; }

    public double OcTarget { get; set; }
    public double Oc { get; set; }
    public double PriorityPrincipal { get; set; }
    public double RegularPrincipal { get; set; }
    public double AcceleratedPrincipal { get; set; }
    public double PrincipalPaid { get; set; }
    public double InterestPaid { get; set; }
    public double ReserveRequired { get; set; }
    public double ReserveDraw { get; set; }
    public double ReserveRelease { get; set; }
    public double ReserveDeposit { get; set; }
    public double Reserve { get; set; }
    public double AcquisitionDeposit { get; set; }
    public double AcquisitionCash { get; set; }
    public double AcquisitionRelease { get; set; }
    public double AcquisitionAccount { get; set; }
    public double AdditionsFace { get; set; }
    public double AdditionsValue { get; set; }
    public double NegativeCarryRequired { get; set; }
    public double NegativeCarryDeposit { get; set; }
    public double NegativeCarryDraw { get; set; }
    public double NegativeCarryRelease { get; set; }
    public double NegativeCarry { get; set; }
    public double CertificatePaid { get; set; }
    public double NoteBalance { get; set; }
    public List<OwnerTrustTestDto> Tests { get; set; } = new();
    public List<OwnerTrustClassDto> Classes { get; set; } = new();
    public InstallmentPoolPeriodDto Pool { get; set; } = new();
}

public class OwnerTrustTestDto
{
    public string Name { get; set; } = "";

    /// <summary>Percent for a ratio; dollars for a shortfall.</summary>
    public double? Value { get; set; }

    public double? Limit { get; set; }

    /// <summary>PASS, FAIL or NOT_EVALUATED (no full window yet).</summary>
    public string Status { get; set; } = "";

    /// <summary>The value was the statement's.</summary>
    public bool Printed { get; set; }

    /// <summary>The run's own value beside a printed one.</summary>
    public double? Computed { get; set; }
}

public class OwnerTrustClassDto
{
    public string Key { get; set; } = "";
    public double BeginBalance { get; set; }
    public double Coupon { get; set; }
    public double? IndexValue { get; set; }
    public int AccrualDays { get; set; }
    public double InterestAccrued { get; set; }
    public double InterestDue { get; set; }
    public double InterestPaid { get; set; }
    public double InterestShortfall { get; set; }
    public double PrincipalPaid { get; set; }
    public double EndBalance { get; set; }
}
