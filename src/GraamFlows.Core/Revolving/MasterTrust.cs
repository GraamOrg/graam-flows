namespace GraamFlows.Revolving;

// A master trust of note series over one pool of fixed 0% installment contracts. Every series holds an
// undivided interest in the same pool, and each values that pool at its own discount rate: the
// discounted series invested amount is the series invested amount times the pool's face over its
// present value at the series' rate. Each month every series takes its allocation percentage of the
// trust's collections and runs its own waterfall. While the trust revolves, the collections left to
// the certificates buy new contracts. Rates and percents are fractions here; the API takes percents.

/// <summary>
///     The decimals each printed ratio is rounded to, half away from zero, before it is used. The
///     defaults are the precision a statement prints them at: the discount ratio to five places, the
///     series share to two places of a percent, the allocation percentage to five places of a percent.
/// </summary>
public sealed record MasterTrustRounding
{
    public int DiscountRatio { get; init; } = 5;
    public int SeriesShare { get; init; } = 4;
    public int Allocation { get; init; } = 7;
}

/// <summary>The amortization events a series tests. A test left null or false is not tested.</summary>
public sealed record MasterTrustSeriesTests
{
    /// <summary>Interest due on any class and not paid on a payment date.</summary>
    public bool InterestShortfall { get; init; }

    /// <summary>While revolving: the reserve account below its required amount after the payment date.</summary>
    public bool ReserveShortfall { get; init; }

    /// <summary>A pool balance deficit after the payment date's distributions and deposits.</summary>
    public bool PoolBalanceDeficit { get; init; }

    /// <summary>The allocated pool balance below this share of the notes less the principal funding account.</summary>
    public double? AllocatedPoolLimit { get; init; }

    /// <summary>The discounted invested amount above the pool less the series' ineligible and excess concentration amounts.</summary>
    public bool DiscountedInvested { get; init; }
}

/// <summary>One series of the trust: its rules and where it stands at the statement date.</summary>
public sealed record MasterTrustSeries
{
    public required string Name { get; init; }

    /// <summary>Annual, as a fraction: the rate this series values the whole pool at.</summary>
    public required double DiscountRate { get; init; }

    /// <summary>The required overcollateralization percentage p: target OC is p × notes / (1 − p).</summary>
    public required double OcPct { get; init; }

    /// <summary>
    ///     A series with no waterfall in the run: it takes its share of the pool and of the funds, and
    ///     its notes are stated rather than paid. A series the statements print only as a row.
    /// </summary>
    public bool Private { get; init; }

    /// <summary>On a private series: its notes at the statement date (or at its closing).</summary>
    public double PrivateNotes { get; init; }

    /// <summary>The notes, senior first. Empty on a private series.</summary>
    public IReadOnlyList<RevolvingClass> Classes { get; init; } = Array.Empty<RevolvingClass>();

    public IReadOnlyList<OwnerTrustTier> Tiers { get; init; } = Array.Empty<OwnerTrustTier>();
    public IReadOnlyList<OwnerTrustStep> Steps { get; init; } = Array.Empty<OwnerTrustStep>();

    /// <summary>Which excess concentration amount the series takes (series may test different limits).</summary>
    public string ConcentrationGroup { get; init; } = "";

    /// <summary>Dollars, while revolving. After revolving it is what the account held at its last payment date.</summary>
    public double RequiredReserve { get; init; }

    /// <summary>Trustee, collateral agent and reviewer fees per month, in dollars.</summary>
    public double MonthlyFees { get; init; }

    /// <summary>The principal funding account limit, as a share of the notes.</summary>
    public double PfaLimitPct { get; init; } = 0.5;

    /// <summary>
    ///     The day the series closed. It joins the trust in the month holding it: it has no opening
    ///     amounts, its allocation numerator is its closing adjusted invested amount, and its first
    ///     interest runs from this day. Null: outstanding at the statement date.
    /// </summary>
    public DateTime? ClosingDate { get; init; }

    /// <summary>
    ///     A Class A Certificateholder deposit redeems the notes on the payment date in this month: the
    ///     deposit and the reserve pay the notes whole, and the series leaves the trust. Null: none.
    /// </summary>
    public DateTime? RedemptionDate { get; init; }

    /// <summary>The deposit as a share of the note balance. 1 is par.</summary>
    public double RedemptionPrice { get; init; } = 1.0;

    /// <summary>Not redeemed by the payment date in this month is an amortization event. Null: not tested.</summary>
    public DateTime? AnticipatedRedemption { get; init; }

    public MasterTrustSeriesTests Tests { get; init; } = new();

    public MasterTrustSeriesState State { get; init; } = new();
}

/// <summary>Where a series stands at the statement date.</summary>
public sealed record MasterTrustSeriesState
{
    public SeriesPhase Phase { get; init; } = SeriesPhase.Revolving;
    public string? Event { get; init; }
    public double Reserve { get; init; }

    /// <summary>The principal funding account.</summary>
    public double Pfa { get; init; }

    /// <summary>Once amortizing: the notes on the last day of revolving, which the OC target is then set on.</summary>
    public double? FrozenNotes { get; init; }

    /// <summary>Once amortizing: the allocation numerator on the last day of revolving.</summary>
    public double? FrozenNumerator { get; init; }

    /// <summary>A PFA release owed into the next payment date's available funds.</summary>
    public double PfaReleaseDue { get; init; }
}

/// <summary>The cohorts a forecast month buys, and how it pays for them.</summary>
public sealed record MasterTrustAcquisition
{
    public required InstallmentPrice Price { get; init; }

    /// <summary>The terms bought; their weights are shares of the face bought and come to 1.</summary>
    public required IReadOnlyList<InstallmentTerm> Terms { get; init; }

    /// <summary>
    ///     False: the cash the certificates were left on the last payment date buys what it can. True: the
    ///     trust buys back to its opening face each month, paying cash up to that amount and taking the
    ///     rest through the certificates.
    /// </summary>
    public bool HoldPoolFace { get; init; }
}

/// <summary>The trust's rules: everything fixed by its documents.</summary>
public sealed record MasterTrustConfig
{
    public required IReadOnlyList<MasterTrustSeries> Series { get; init; }

    /// <summary>Annual, as a fraction of the pool at the month's opening, shared by allocation percentage.</summary>
    public double ServicingRate { get; init; }

    public MasterTrustRounding Rounding { get; init; } = new();

    /// <summary>The trust's pool tests (write-offs and delinquencies). Each trips every series.</summary>
    public OwnerTrustTests Tests { get; init; } = new();

    /// <summary>Null: forecast months buy nothing, and the pool runs off.</summary>
    public MasterTrustAcquisition? Acquisition { get; init; }

    /// <summary>
    ///     By forecast month and concentration group: ineligible plus excess concentration amounts.
    ///     A group with no path holds its last known amount, disclosed.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Concentration { get; init; } =
        new Dictionary<string, IReadOnlyList<double>>();

    /// <summary>By forecast month: 91+ delinquencies as a share of the pool at the month's close. Empty holds the last known.</summary>
    public IReadOnlyList<double> Delinquency { get; init; } = Array.Empty<double>();

    public OwnerTrustCalendar Calendar { get; init; } = OwnerTrustCalendar.Settlement;
}

/// <summary>Where the trust stands at the statement date.</summary>
public sealed record MasterTrustState
{
    /// <summary>Monthly write-offs over each month's opening pool, oldest first, as fractions.</summary>
    public IReadOnlyList<double> WriteOffHistory { get; init; } = Array.Empty<double>();

    /// <summary>Monthly delinquency ratios, oldest first, as fractions.</summary>
    public IReadOnlyList<double> DelinquencyHistory { get; init; } = Array.Empty<double>();

    /// <summary>Ineligible plus excess concentration amounts at the statement date, by group.</summary>
    public IReadOnlyDictionary<string, double> Concentration { get; init; } = new Dictionary<string, double>();

    /// <summary>Cash the certificates were left on the last payment date, which the first forecast month buys with.</summary>
    public double CertificateCash { get; init; }
}

/// <summary>
///     A financing adjustment date: a day the pool is revalued as additions join. Each holds the pool
///     from that day until the next one, so its weight is the days between them.
/// </summary>
public sealed record MasterTrustFad
{
    public required DateTime Date { get; init; }
    public required double PoolBalance { get; init; }

    /// <summary>Ineligible plus excess concentration amounts on the day, by group. A group not stated holds.</summary>
    public IReadOnlyDictionary<string, double> Concentration { get; init; } = new Dictionary<string, double>();
}

/// <summary>A replayed month's printed figures for one series. Each null is computed.</summary>
public sealed record MasterTrustSeriesActual
{
    /// <summary>The printed discount rate this month, as a fraction. It holds for later months.</summary>
    public double? DiscountRate { get; init; }

    /// <summary>Printed present value of the pool at the series' rate, as the month opens. Stated, the run uses it.</summary>
    public double? PresentValueStart { get; init; }

    /// <summary>Printed present value at the month's close. Stated, the run uses it and reports its own beside it.</summary>
    public double? PresentValueEnd { get; init; }

    /// <summary>The printed time-weighted adjusted invested amount the allocation is taken on.</summary>
    public double? AllocationNumerator { get; init; }

    /// <summary>A private series' printed notes at the month's close.</summary>
    public double? NoteBalance { get; init; }

    /// <summary>Printed fees.</summary>
    public double? Fees { get; init; }

    /// <summary>The printed Class A Certificateholder deposit: the series is redeemed on this payment date.</summary>
    public double? RedemptionDeposit { get; init; }

    /// <summary>A servicer termination or other event the statements report.</summary>
    public string? Event { get; init; }

    /// <summary>
    ///     Printed fixed coupons by class, in percent, for a class whose coupon was reset. Each holds for
    ///     later months.
    /// </summary>
    public IReadOnlyDictionary<string, double> Coupons { get; init; } = new Dictionary<string, double>();
}

/// <summary>A replayed month's printed trust figures, parallel to the pool's actual periods.</summary>
public sealed record MasterTrustActual
{
    /// <summary>The month's financing adjustment dates, the first the month's opening, the last its close.</summary>
    public IReadOnlyList<MasterTrustFad> Fads { get; init; } = Array.Empty<MasterTrustFad>();

    public double? WriteOffRatio { get; init; }
    public double? DelinquencyRatio { get; init; }
    public double? Delinquency { get; init; }

    /// <summary>The printed pool balance deficit the series' regular priority principal acts on.</summary>
    public double? PoolBalanceDeficit { get; init; }

    /// <summary>Other cash into the trust's funds, as printed.</summary>
    public double OtherFunds { get; init; }

    public IReadOnlyDictionary<string, MasterTrustSeriesActual> Series { get; init; } =
        new Dictionary<string, MasterTrustSeriesActual>();
}

/// <summary>Everything a master trust run needs.</summary>
public sealed record MasterTrustRun
{
    public string Name { get; init; } = "";

    /// <summary>The pool and the run's months. Its discount rates are the series' rates, added if missing.</summary>
    public required InstallmentPoolRun Pool { get; init; }

    public required MasterTrustConfig Trust { get; init; }
    public MasterTrustState State { get; init; } = new();
    public IReadOnlyList<MasterTrustActual> ActualPeriods { get; init; } = Array.Empty<MasterTrustActual>();
    public int DistributionDay { get; init; } = 20;
}

/// <summary>One series' month.</summary>
public sealed record MasterTrustSeriesPeriod
{
    public required string Name { get; init; }
    public SeriesPhase Phase { get; init; }
    public string? Event { get; init; }
    public bool Private { get; init; }
    public double DiscountRate { get; init; }

    public double NotesStart { get; init; }
    public double NotesEnd { get; init; }
    public double TargetOcStart { get; init; }
    public double TargetOcEnd { get; init; }
    public double InvestedStart { get; init; }
    public double InvestedEnd { get; init; }

    /// <summary>The present value the run used: printed where stated, else its own.</summary>
    public double PresentValueStart { get; init; }

    public double PresentValueEnd { get; init; }

    /// <summary>The run's own present value at the close, where a printed one was used; else null.</summary>
    public double? ComputedPresentValueEnd { get; init; }

    public double DiscountRatioStart { get; init; }
    public double DiscountRatioEnd { get; init; }
    public double DiscountedInvestedStart { get; init; }
    public double DiscountedInvestedEnd { get; init; }
    public double SeriesShareStart { get; init; }
    public double SeriesShareEnd { get; init; }
    public double ConcentrationStart { get; init; }
    public double ConcentrationEnd { get; init; }
    public double IncrementalStart { get; init; }
    public double IncrementalEnd { get; init; }
    public double AdjustedInvestedStart { get; init; }
    public double AdjustedInvestedEnd { get; init; }

    /// <summary>The time-weighted adjusted invested amount the allocation is taken on.</summary>
    public double AllocationNumerator { get; init; }

    /// <summary>The run's own numerator, where a printed one was used; else null.</summary>
    public double? ComputedAllocationNumerator { get; init; }

    public double AllocationPct { get; init; }
    public double AllocatedPoolBalance { get; init; }

    /// <summary>The series' allocation percentage of the trust's funds.</summary>
    public double Funds { get; init; }

    public double PfaRelease { get; init; }
    public double RedemptionDeposit { get; init; }
    public double ReserveDraw { get; init; }

    /// <summary>Funds, the PFA release, the redemption deposit and the reserve drawn.</summary>
    public double AvailableFunds { get; init; }

    public double Fees { get; init; }
    public double ServicingFee { get; init; }
    public double InterestPaid { get; init; }

    /// <summary>Priority principal by tier, senior first.</summary>
    public IReadOnlyList<double> PriorityPrincipal { get; init; } = Array.Empty<double>();

    public double RegularPrincipal { get; init; }

    /// <summary>Principal paid on the notes, PFA payouts included.</summary>
    public double PrincipalPaid { get; init; }

    public double PfaDeposit { get; init; }
    public double Pfa { get; init; }
    public double ReserveRequired { get; init; }
    public double ReserveDeposit { get; init; }
    public double ReserveRelease { get; init; }
    public double Reserve { get; init; }

    /// <summary>What the waterfall left for the Class R interest.</summary>
    public double ClassR { get; init; }

    public IReadOnlyList<OwnerTrustTestReading> Tests { get; init; } = Array.Empty<OwnerTrustTestReading>();
    public IReadOnlyList<OwnerTrustClassPeriod> Classes { get; init; } = Array.Empty<OwnerTrustClassPeriod>();
}

/// <summary>A financing adjustment date as the run weighted it.</summary>
public sealed record MasterTrustFadReading(DateTime Date, int Days, double PoolBalance);

/// <summary>One month of the trust.</summary>
public sealed record MasterTrustPeriod
{
    public int Period { get; init; }
    public DateTime PeriodEnd { get; init; }
    public DateTime DistributionDate { get; init; }
    public bool Replayed { get; init; }

    public double BeginningPool { get; init; }
    public double EndingPool { get; init; }
    public IReadOnlyList<MasterTrustFadReading> Fads { get; init; } = Array.Empty<MasterTrustFadReading>();
    public double TimeWeightedPool { get; init; }

    /// <summary>The pool's own present value at each series rate, as the month opens and at its close.</summary>
    public IReadOnlyList<InstallmentValuation> PresentValues { get; init; } = Array.Empty<InstallmentValuation>();

    /// <summary>Collections and repurchase cash, which the series share by allocation percentage.</summary>
    public double TotalFunds { get; init; }

    public double SumNumerators { get; init; }
    public double AllocationDenominator { get; init; }
    public double TransferorPct { get; init; }
    public double TransferorAllocation { get; init; }
    public double RequiredPoolBalance { get; init; }
    public double PoolBalanceDeficit { get; init; }

    /// <summary>The run's own deficit, where a printed one was used; else null.</summary>
    public double? ComputedPoolBalanceDeficit { get; init; }

    /// <summary>Face bought at the month's close, and what of it the trust paid cash for.</summary>
    public double AdditionsFace { get; init; }

    public double AdditionsCash { get; init; }

    /// <summary>Face the certificates contributed: bought with no trust cash.</summary>
    public double CertificateFundedFace { get; init; }

    /// <summary>Class R and the transferor allocation, less any of it spent on purchases next month.</summary>
    public double CertificateCash { get; init; }

    public IReadOnlyList<OwnerTrustTestReading> Tests { get; init; } = Array.Empty<OwnerTrustTestReading>();
    public required IReadOnlyList<MasterTrustSeriesPeriod> Series { get; init; }
    public required InstallmentPoolPeriod Pool { get; init; }
}

/// <summary>A series as the run closed it.</summary>
/// <param name="State">null once it has ended: there is nothing left to run.</param>
/// <param name="Ended">REDEEMED or NOTES_PAID, or null if it runs on.</param>
public sealed record MasterTrustSeriesClosing(
    string Name,
    MasterTrustSeriesState? State,
    IReadOnlyList<OwnerTrustClassClosing> Classes,
    double Notes,
    double DiscountRate,
    string? Ended);

public sealed record MasterTrustResult
{
    public required IReadOnlyList<MasterTrustPeriod> Periods { get; init; }

    /// <summary>Each series' phase moves, with the period each took effect in.</summary>
    public required IReadOnlyList<(string Series, SeriesPhase Phase, int Period, DateTime DistributionDate, string? Event)>
        PhasePath { get; init; }

    public required MasterTrustState Closing { get; init; }
    public required IReadOnlyList<MasterTrustSeriesClosing> ClosingSeries { get; init; }
    public required IReadOnlyList<InstallmentCohort> ClosingPool { get; init; }
    public required IReadOnlyList<string> Disclosures { get; init; }
}
