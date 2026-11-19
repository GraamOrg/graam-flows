namespace GraamFlows.Revolving;

// An amortizing owner trust over an installment pool: one series of sequential notes and a
// certificate, backed by fixed 0% installment contracts held at the present value of their remaining
// payments. Notes are sized on the adjusted pool (the pool less its yield supplement
// overcollateralization, which is the pool's present value at the trust's discount rate). While the
// trust revolves, collections buy new contracts through an acquisition account; once it amortizes,
// they pay the notes down to the adjusted pool less an overcollateralization target. Rates and
// percents are fractions here; the API takes percents.

/// <summary>The closed set of an owner trust's waterfall steps, in whatever order the trust states them.</summary>
public enum OwnerTrustVerb
{
    /// <summary>Trustee and reviewer fees: the stated monthly amount, or a replayed month's printed one.</summary>
    PayFees,

    /// <summary>The servicing fee: the annual rate / 12 × the adjusted pool at the month's opening.</summary>
    PayServicing,

    /// <summary>Interest due on one tier, pro rata across its classes when short.</summary>
    PayInterest,

    /// <summary>
    ///     While amortizing: the tier and every tier above it are paid down to the adjusted pool. On and
    ///     after a tier's final maturity date, the tier in full.
    /// </summary>
    PriorityPrincipal,

    /// <summary>
    ///     While amortizing: the notes, less the month's priority principal, are paid down to the adjusted
    ///     pool less the overcollateralization target, sequentially by tier.
    /// </summary>
    RegularPrincipal,

    /// <summary>Only once an amortization event has occurred: everything left, sequentially, until the notes are paid.</summary>
    AcceleratedPrincipal,

    /// <summary>Tops the reserve account up to its required amount.</summary>
    FundReserve,

    /// <summary>
    ///     While revolving: deposits into the acquisition account what brings the notes back to the
    ///     adjusted pool less the overcollateralization target, less what the account already holds.
    /// </summary>
    DepositAcquisition,

    /// <summary>While revolving: tops the negative carry account up to its required amount.</summary>
    FundNegativeCarry,

    /// <summary>Everything left, to the certificateholders.</summary>
    PayCertificate
}

public static class OwnerTrustVerbs
{
    private static readonly Dictionary<string, OwnerTrustVerb> Names = new()
    {
        ["PAY_FEES"] = OwnerTrustVerb.PayFees,
        ["PAY_SERVICING"] = OwnerTrustVerb.PayServicing,
        ["PAY_INTEREST"] = OwnerTrustVerb.PayInterest,
        ["PRIORITY_PRINCIPAL"] = OwnerTrustVerb.PriorityPrincipal,
        ["REGULAR_PRINCIPAL"] = OwnerTrustVerb.RegularPrincipal,
        ["ACCELERATED_PRINCIPAL"] = OwnerTrustVerb.AcceleratedPrincipal,
        ["FUND_RESERVE"] = OwnerTrustVerb.FundReserve,
        ["DEPOSIT_ACQUISITION"] = OwnerTrustVerb.DepositAcquisition,
        ["FUND_NEGATIVE_CARRY"] = OwnerTrustVerb.FundNegativeCarry,
        ["PAY_CERTIFICATE"] = OwnerTrustVerb.PayCertificate
    };

    public static OwnerTrustVerb Parse(string? verb)
    {
        if (verb != null && Names.TryGetValue(verb.Trim().ToUpperInvariant(), out var parsed))
            return parsed;

        throw new RevolvingConfigException(
            $"Unknown owner trust step '{verb}'. Supported: {string.Join(", ", Names.Keys)}.");
    }

    public static string ToWire(OwnerTrustVerb verb)
    {
        return Names.First(n => n.Value == verb).Key;
    }

    /// <summary>The verbs that name a tier.</summary>
    public static bool NamesTier(OwnerTrustVerb verb)
    {
        return verb is OwnerTrustVerb.PayInterest or OwnerTrustVerb.PriorityPrincipal;
    }

    /// <summary>
    ///     The steps a reserve draw covers, as the trust's documents put it: senior fees, interest and
    ///     priority principal.
    /// </summary>
    public static bool ReserveCovers(OwnerTrustVerb verb)
    {
        return verb is OwnerTrustVerb.PayFees or OwnerTrustVerb.PayServicing or OwnerTrustVerb.PayInterest
            or OwnerTrustVerb.PriorityPrincipal;
    }

    /// <summary>The steps a negative carry draw covers while revolving.</summary>
    public static bool NegativeCarryCovers(OwnerTrustVerb verb)
    {
        return verb is OwnerTrustVerb.PayFees or OwnerTrustVerb.PayServicing or OwnerTrustVerb.PayInterest
            or OwnerTrustVerb.FundReserve or OwnerTrustVerb.DepositAcquisition;
    }
}

/// <param name="Verb">what the step does.</param>
/// <param name="Tier">the tier PAY_INTEREST and PRIORITY_PRINCIPAL act on.</param>
public sealed record OwnerTrustStep(OwnerTrustVerb Verb, string? Tier = null);

/// <summary>
///     Classes that rank together: they take interest and principal pro rata to what each is owed,
///     as a Class A of fixed and floating notes does.
/// </summary>
/// <param name="FinalMaturity">on and after this payment date, priority principal pays the tier in full.</param>
public sealed record OwnerTrustTier(string Name, IReadOnlyList<string> Classes, DateTime? FinalMaturity = null);

/// <summary>
///     The overcollateralization target. While revolving it is set on the notes:
///     <c>notes / (1 − p) − notes</c>. While amortizing it is a share of the adjusted pool at the month's
///     close. Either way it is at least <see cref="Floor" />. The floor tests choose the passing or the
///     failing percent each month.
/// </summary>
public sealed record OwnerTrustOc
{
    public required double RevolvingPct { get; init; }
    public required double RevolvingFailingPct { get; init; }
    public required double AmortizingPct { get; init; }
    public required double AmortizingFailingPct { get; init; }

    /// <summary>Dollars: the target is never below it.</summary>
    public double Floor { get; init; }
}

/// <summary>
///     The amortization events the statements report. A test left null is not tested. Each latches:
///     the trust amortizes from then on, and pays accelerated principal.
/// </summary>
public sealed record OwnerTrustTests
{
    /// <summary>
    ///     The sum of the last <see cref="WriteOffWindow" /> months' write-offs over each month's opening
    ///     pool, times <see cref="WriteOffMultiplier" />, above this.
    /// </summary>
    public double? WriteOffLimit { get; init; }

    public int WriteOffWindow { get; init; } = 3;
    public double WriteOffMultiplier { get; init; } = 4.0;

    /// <summary>The average of the last <see cref="DelinquencyWindow" /> months' delinquency ratios, above this.</summary>
    public double? DelinquencyLimit { get; init; }

    public int DelinquencyWindow { get; init; } = 3;

    /// <summary>The adjusted pool below this share of the note balance.</summary>
    public double? ParityLimit { get; init; }

    /// <summary>
    ///     Overcollateralization below its target after the month's payments and acquisitions. A
    ///     shortfall that comes only from a change of the target's percent is not an event unless it
    ///     lasts past <see cref="OcGraceMonths" /> months.
    /// </summary>
    public bool OcShortfall { get; init; }

    public int OcGraceMonths { get; init; } = 3;

    /// <summary>
    ///     While revolving: interest due not paid, or the reserve or negative carry account below its
    ///     required amount, after the month's payments.
    /// </summary>
    public bool RevolvingShortfalls { get; init; }
}

/// <summary>What the clean-up purchase pays per dollar it is measured on.</summary>
public enum CleanUpPriceBasis
{
    /// <summary>The pool's face at the month's close.</summary>
    PoolFace,

    /// <summary>The notes' balance as the month opens.</summary>
    NoteBalance
}

/// <summary>
///     The certificateholders' optional acquisition of the receivables, with a clean-up redemption of
///     the notes: on any payment date when the pool at the month's close is at most
///     <see cref="PoolFactor" /> of <see cref="ClosingPoolBalance" />, they pay a price into the trust
///     and take the receivables. The notes are paid in full with interest, the accounts are released,
///     and the rest goes to the certificate. It is refused, and the trust runs on, where the price and
///     the trust's cash cannot pay the notes and the trust's other obligations in full.
/// </summary>
public sealed record OwnerTrustCleanUp
{
    public required double PoolFactor { get; init; }
    public required double ClosingPoolBalance { get; init; }
    public CleanUpPriceBasis PriceBasis { get; init; } = CleanUpPriceBasis.PoolFace;

    /// <summary>The price as a share of <see cref="PriceBasis" />. 1 is par.</summary>
    public double Price { get; init; } = 1.0;

    /// <summary>In a forecast: payment dates after the first eligible one before it is exercised.</summary>
    public int DelayMonths { get; init; }

    /// <summary>In a forecast: the payment date's month it is exercised in, if eligible then. Null uses eligibility.</summary>
    public DateTime? Date { get; init; }
}

/// <summary>The business days a payment date rolls on.</summary>
public enum OwnerTrustCalendar
{
    /// <summary>The settlement calendar card programs use. It has no Juneteenth.</summary>
    Settlement,

    /// <summary>The Federal Reserve's: with Juneteenth from 2022.</summary>
    FederalReserve
}

/// <summary>An owner trust's rules: everything fixed by its documents.</summary>
public sealed record OwnerTrustConfig
{
    /// <summary>
    ///     Annual, as a fraction: the rate the adjusted pool is the present value at. The pool is valued
    ///     at it whether or not the pool lists it.
    /// </summary>
    public required double DiscountRate { get; init; }

    /// <summary>Senior first.</summary>
    public required IReadOnlyList<OwnerTrustTier> Tiers { get; init; }

    public required IReadOnlyList<OwnerTrustStep> Steps { get; init; }

    /// <summary>
    ///     The amortization period starts on the payment date in this month, if no event starts it
    ///     sooner. Null: the trust has no scheduled end to revolving.
    /// </summary>
    public DateTime? RevolvingEnd { get; init; }

    /// <summary>Annual, as a fraction of the adjusted pool at the month's opening.</summary>
    public double ServicingRate { get; init; }

    /// <summary>Trustee and reviewer fees per month, in dollars.</summary>
    public double MonthlyFees { get; init; }

    public required OwnerTrustOc Oc { get; init; }
    public double RequiredReserve { get; init; }
    public OwnerTrustTests Tests { get; init; } = new();
    public OwnerTrustCleanUp? CleanUp { get; init; }

    /// <summary>
    ///     In forecast months while revolving: what the acquisition account buys, at this price.
    ///     Null buys nothing, and the account keeps its cash.
    /// </summary>
    public InstallmentPrice? AcquisitionPrice { get; init; }

    /// <summary>The terms the account buys, whose weights are shares of the face bought and come to 1.</summary>
    public IReadOnlyList<InstallmentTerm> AcquisitionTerms { get; init; } = Array.Empty<InstallmentTerm>();

    /// <summary>
    ///     By forecast month: whether the pool passes its floor composition tests. A month past the end
    ///     takes the last value; empty passes every month, and the run discloses it.
    /// </summary>
    public IReadOnlyList<bool> FloorTestsPass { get; init; } = Array.Empty<bool>();

    /// <summary>
    ///     By forecast month: receivables 91 days or more delinquent at the month's close, as a share of
    ///     the pool before the month's purchases. A month past the end takes the last value; empty
    ///     holds the last month the run knows, and the run discloses it.
    /// </summary>
    public IReadOnlyList<double> Delinquency { get; init; } = Array.Empty<double>();

    /// <summary>Annual, as a fraction: what the reserve account earns, into available funds. 0 if not stated.</summary>
    public double ReserveEarningsRate { get; init; }

    /// <summary>The business days payment dates roll on.</summary>
    public OwnerTrustCalendar Calendar { get; init; } = OwnerTrustCalendar.Settlement;
}

/// <summary>Where the trust stands at the statement date.</summary>
public sealed record OwnerTrustState
{
    public SeriesPhase Phase { get; init; } = SeriesPhase.Revolving;

    /// <summary>The amortization event that has occurred, if any: accelerated principal is then paid.</summary>
    public string? Event { get; init; }

    public double Reserve { get; init; }
    public double AcquisitionAccount { get; init; }
    public double NegativeCarry { get; init; }

    /// <summary>The adjusted pool at the statement date. Null values the opening pool at the discount rate.</summary>
    public double? AdjustedPool { get; init; }

    /// <summary>Monthly write-offs over each month's opening pool, oldest first, as fractions.</summary>
    public IReadOnlyList<double> WriteOffHistory { get; init; } = Array.Empty<double>();

    /// <summary>Monthly delinquency ratios, oldest first, as fractions.</summary>
    public IReadOnlyList<double> DelinquencyHistory { get; init; } = Array.Empty<double>();

    /// <summary>The overcollateralization percent of the months before, oldest first, for the shortfall's grace.</summary>
    public IReadOnlyList<double> OcPctHistory { get; init; } = Array.Empty<double>();

    /// <summary>Payment dates the clean-up has been eligible on, for its delay.</summary>
    public int CleanUpEligibleMonths { get; init; }
}

/// <summary>A printed month's trust figures, for replay. Each null is computed as in a forecast month.</summary>
public sealed record OwnerTrustActual
{
    /// <summary>Printed trustee and reviewer fees.</summary>
    public double? Fees { get; init; }

    public bool? FloorTestsPass { get; init; }

    /// <summary>The printed write-off test value (the window's sum times its multiplier), as a fraction.</summary>
    public double? WriteOffRatio { get; init; }

    /// <summary>The printed delinquency test value (the window's average), as a fraction.</summary>
    public double? DelinquencyRatio { get; init; }

    /// <summary>The month's own printed delinquency ratio, as a fraction, for the window.</summary>
    public double? Delinquency { get; init; }

    /// <summary>Other cash into available funds: account earnings and cap receipts, as printed.</summary>
    public double OtherFunds { get; init; }

    /// <summary>A servicer termination or other event the statements report, which the pool cannot show.</summary>
    public string? Event { get; init; }

    /// <summary>The certificateholders exercised the clean-up this month, depositing this cash.</summary>
    public double? CleanUpDeposit { get; init; }

    /// <summary>
    ///     The printed adjusted pool at the month's close, after the month's purchases. Stated, the
    ///     waterfall runs on it, and the run reports its own present value beside it.
    /// </summary>
    public double? AdjustedPool { get; init; }
}

/// <summary>Everything an owner trust run needs.</summary>
public sealed record OwnerTrustRun
{
    public string Name { get; init; } = "";

    /// <summary>The pool and the run's months: <see cref="InstallmentPoolRun.StatementDate" /> and periods.</summary>
    public required InstallmentPoolRun Pool { get; init; }

    public required OwnerTrustConfig Trust { get; init; }

    /// <summary>The notes. Each must sit in exactly one tier.</summary>
    public required IReadOnlyList<RevolvingClass> Classes { get; init; }

    public OwnerTrustState State { get; init; } = new();

    /// <summary>Parallel to the pool's actual periods; shorter is padded with nothing printed.</summary>
    public IReadOnlyList<OwnerTrustActual> ActualPeriods { get; init; } = Array.Empty<OwnerTrustActual>();

    /// <summary>Day of the following month a period pays on, before business-day roll.</summary>
    public int DistributionDay { get; init; } = 20;
}

/// <summary>One class's month.</summary>
public sealed record OwnerTrustClassPeriod
{
    public required string Key { get; init; }
    public double BeginBalance { get; init; }
    public double CouponPct { get; init; }
    public double? IndexValue { get; init; }
    public int AccrualDays { get; init; }
    public double InterestAccrued { get; init; }
    public double InterestDue { get; init; }
    public double InterestPaid { get; init; }

    /// <summary>Interest due and unpaid after the month, carried with interest on it.</summary>
    public double InterestShortfall { get; init; }

    public double PrincipalPaid { get; init; }
    public double EndBalance { get; init; }
}

/// <summary>One amortization test's reading on a payment date.</summary>
/// <param name="Value">the test's value, as a fraction; for a shortfall test, the dollars short.</param>
/// <param name="Limit">what the value is tested against.</param>
/// <param name="Printed">the value was taken from the statement, not computed.</param>
/// <param name="Computed">the run's own value, where the printed one was used.</param>
public sealed record OwnerTrustTestReading(
    string Name,
    double? Value,
    double? Limit,
    bool Breached,
    bool Printed,
    double? Computed = null);

/// <summary>One month of an owner trust.</summary>
public sealed record OwnerTrustPeriod
{
    public int Period { get; init; }
    public DateTime PeriodEnd { get; init; }
    public DateTime DistributionDate { get; init; }
    public bool Replayed { get; init; }

    /// <summary>The phase the payment date paid in.</summary>
    public SeriesPhase Phase { get; init; }

    /// <summary>The event that has occurred, if any, as the payment date paid.</summary>
    public string? Event { get; init; }

    public double BeginningPool { get; init; }

    /// <summary>The pool at the month's close, before the month's purchases.</summary>
    public double PoolBeforeAdditions { get; init; }

    public double EndingPool { get; init; }
    public double BeginningAdjustedPool { get; init; }
    public double AdjustedPoolBeforeAdditions { get; init; }
    public double EndingAdjustedPool { get; init; }

    /// <summary>Pool less adjusted pool at the close.</summary>
    public double Ysoc { get; init; }

    /// <summary>The run's own present value, where a printed adjusted pool was stated; else null.</summary>
    public double? ComputedAdjustedPool { get; init; }

    public double Collections { get; init; }
    public double WriteOffs { get; init; }
    public double RepurchaseCash { get; init; }
    public double OtherFunds { get; init; }
    public double ReserveEarnings { get; init; }
    public double CleanUpDeposit { get; init; }

    /// <summary>Collections, repurchase cash, other funds, earnings and the clean-up deposit.</summary>
    public double AvailableFunds { get; init; }

    public double Fees { get; init; }
    public double ServicingFee { get; init; }
    public bool FloorTestsPass { get; init; }
    public double OcTargetPct { get; init; }
    public double OcTarget { get; init; }

    /// <summary>Ending adjusted pool plus the acquisition account, less the notes, after the payment date.</summary>
    public double Oc { get; init; }

    public double PriorityPrincipal { get; init; }
    public double RegularPrincipal { get; init; }
    public double AcceleratedPrincipal { get; init; }

    /// <summary>Principal paid on the notes, all steps together.</summary>
    public double PrincipalPaid { get; init; }

    public double InterestPaid { get; init; }
    public double ReserveRequired { get; init; }
    public double ReserveDraw { get; init; }
    public double ReserveRelease { get; init; }
    public double ReserveDeposit { get; init; }
    public double Reserve { get; init; }
    public double AcquisitionDeposit { get; init; }

    /// <summary>Cash the acquisition account paid for the month's purchases.</summary>
    public double AcquisitionCash { get; init; }

    public double AcquisitionRelease { get; init; }
    public double AcquisitionAccount { get; init; }
    public double AdditionsFace { get; init; }

    /// <summary>The month's purchases at the discount rate.</summary>
    public double AdditionsValue { get; init; }

    public double NegativeCarryRequired { get; init; }
    public double NegativeCarryDeposit { get; init; }
    public double NegativeCarryDraw { get; init; }
    public double NegativeCarryRelease { get; init; }
    public double NegativeCarry { get; init; }

    /// <summary>
    ///     What the waterfall left for the certificateholders. Accounts released when the trust ends are on
    ///     their own release lines.
    /// </summary>
    public double CertificatePaid { get; init; }

    public double NoteBalance { get; init; }
    public IReadOnlyList<OwnerTrustTestReading> Tests { get; init; } = Array.Empty<OwnerTrustTestReading>();
    public required IReadOnlyList<OwnerTrustClassPeriod> Classes { get; init; }
    public required InstallmentPoolPeriod Pool { get; init; }
}

/// <summary>A class as the run closed it.</summary>
public sealed record OwnerTrustClassClosing(string Key, double Balance, double InterestShortfall);

public sealed record OwnerTrustResult
{
    public required IReadOnlyList<OwnerTrustPeriod> Periods { get; init; }

    /// <summary>The phase moves, with the period each took effect in.</summary>
    public required IReadOnlyList<(SeriesPhase Phase, int Period, DateTime DistributionDate, string? Event)> PhasePath
    {
        get;
        init;
    }

    /// <summary>The trust as the run closed it. Null after a clean-up or once the notes are paid: there is no trust left to run.</summary>
    public OwnerTrustState? Closing { get; init; }

    public required IReadOnlyList<OwnerTrustClassClosing> ClosingClasses { get; init; }
    public required IReadOnlyList<InstallmentCohort> ClosingPool { get; init; }

    /// <summary>How the trust ended in the run: CLEAN_UP, NOTES_PAID or null if it runs on.</summary>
    public string? Ended { get; init; }

    public required IReadOnlyList<string> Disclosures { get; init; }
}
