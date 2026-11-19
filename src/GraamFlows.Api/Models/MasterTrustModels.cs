namespace GraamFlows.Api.Models;

// A master trust of note series over one installment pool, on POST /api/revolving: program.masterTrust
// and pool.model INSTALLMENT. Each series values the whole pool at its own discount rate. A public
// series' classes are request.series[] under the same name; a private one states only its notes.
// Rates, shares and percents are percents, as everywhere on this API. Amounts are dollars.

/// <summary>The trust's rules and its series. Its presence is what makes a run a master trust.</summary>
public class MasterTrustDto
{
    /// <summary>Required: at least one.</summary>
    public List<MasterTrustSeriesDto>? Series { get; set; }

    /// <summary>Annual percent of the pool at the month's opening, shared by allocation percentage. Absent is 0.</summary>
    public double? ServicingRate { get; set; }

    /// <summary>The decimals printed ratios are rounded to. Absent: 5, 4 and 7, as statements print them.</summary>
    public MasterTrustRoundingDto? Rounding { get; set; }

    /// <summary>The trust's pool tests: they trip every series. Absent tests nothing.</summary>
    public MasterTrustTestsDto? Tests { get; set; }

    /// <summary>What forecast months buy. Absent buys nothing, disclosed.</summary>
    public MasterTrustAcquisitionDto? Acquisition { get; set; }

    /// <summary>By group, by forecast month: ineligible plus excess concentration dollars. Absent holds, disclosed.</summary>
    public Dictionary<string, List<double>>? Concentration { get; set; }

    /// <summary>By forecast month: percent of the pool at the close 91+ days delinquent. Absent holds, disclosed.</summary>
    public List<double>? Delinquency { get; set; }

    /// <summary>SETTLEMENT or FEDERAL_RESERVE. Absent is SETTLEMENT.</summary>
    public string? Calendar { get; set; }

    public MasterTrustStateDto? State { get; set; }

    /// <summary>Printed trust figures, parallel to pool.actualPeriods.</summary>
    public List<MasterTrustActualDto>? ActualPeriods { get; set; }
}

public class MasterTrustRoundingDto
{
    /// <summary>Decimals of the discount ratio (pool / PV). Absent is 5.</summary>
    public int? DiscountRatio { get; set; }

    /// <summary>Decimals of the series share as a fraction. Absent is 4 (two places of a percent).</summary>
    public int? SeriesShare { get; set; }

    /// <summary>Decimals of the allocation percentage as a fraction. Absent is 7 (five places of a percent).</summary>
    public int? Allocation { get; set; }
}

public class MasterTrustTestsDto
{
    /// <summary>Percent. Sum of the window's monthly write-offs over each month's opening pool, times the multiplier.</summary>
    public double? WriteOffLimit { get; set; }

    /// <summary>Absent is 3.</summary>
    public int? WriteOffWindow { get; set; }

    /// <summary>Absent is 4.</summary>
    public double? WriteOffMultiplier { get; set; }

    /// <summary>Percent. Average of the window's monthly 91+ ratios.</summary>
    public double? DelinquencyLimit { get; set; }

    /// <summary>Absent is 3.</summary>
    public int? DelinquencyWindow { get; set; }
}

public class MasterTrustAcquisitionDto
{
    /// <summary>Required.</summary>
    public InstallmentPriceDto? Price { get; set; }

    /// <summary>Required; weights in percent of face, summing to 100.</summary>
    public List<InstallmentTermDto>? Terms { get; set; }

    /// <summary>
    ///     True: buy back to the month's opening face, cash up to what the certificates were left and
    ///     the rest through the certificates. Absent is false: the cash buys what it buys.
    /// </summary>
    public bool? HoldPoolFace { get; set; }
}

public class MasterTrustSeriesDto
{
    /// <summary>Required. A public series' classes are request.series[] under this name.</summary>
    public string? Name { get; set; }

    /// <summary>Annual percent the series values the whole pool at. Required.</summary>
    public double? DiscountRate { get; set; }

    /// <summary>Required overcollateralization percent: target OC = pct × notes / (100 − pct). Required.</summary>
    public double? OcPct { get; set; }

    /// <summary>True: no waterfall, notes stated in privateNotes. Absent is false.</summary>
    public bool? Private { get; set; }

    /// <summary>A private series' notes at the statement date or its closing. Required on one.</summary>
    public double? PrivateNotes { get; set; }

    /// <summary>Senior first. Required on a public series.</summary>
    public List<OwnerTrustTierDto>? Tiers { get; set; }

    /// <summary>
    ///     PAY_FEES, PAY_SERVICING, PAY_INTEREST, PRIORITY_PRINCIPAL, FUND_RESERVE, REGULAR_PRINCIPAL and
    ///     PAY_CERTIFICATE (last), in the documents' order. Required on a public series.
    /// </summary>
    public List<OwnerTrustStepDto>? Steps { get; set; }

    /// <summary>The concentration amount the series takes. Absent is "".</summary>
    public string? ConcentrationGroup { get; set; }

    /// <summary>Dollars. Absent is 0.</summary>
    public double? RequiredReserve { get; set; }

    /// <summary>Dollars per month. Absent is 0.</summary>
    public double? MonthlyFees { get; set; }

    /// <summary>Percent of the notes. Absent is 50.</summary>
    public double? PfaLimitPct { get; set; }

    /// <summary>The series joins the trust in the month holding this day. Absent: outstanding at the statement date.</summary>
    public DateTime? ClosingDate { get; set; }

    /// <summary>A Class A Certificateholder deposit redeems the notes on the payment date in this month. Absent: none.</summary>
    public DateTime? RedemptionDate { get; set; }

    /// <summary>Percent of the note balance deposited. Absent is 100.</summary>
    public double? RedemptionPrice { get; set; }

    /// <summary>Not redeemed by the payment date in this month is an amortization event. Absent: not tested.</summary>
    public DateTime? AnticipatedRedemption { get; set; }

    public MasterTrustSeriesTestsDto? Tests { get; set; }
    public MasterTrustSeriesStateDto? State { get; set; }
}

public class MasterTrustSeriesTestsDto
{
    public bool? InterestShortfall { get; set; }
    public bool? ReserveShortfall { get; set; }
    public bool? PoolBalanceDeficit { get; set; }

    /// <summary>Percent: the allocated pool balance below it of the notes less the PFA.</summary>
    public double? AllocatedPoolLimit { get; set; }

    public bool? DiscountedInvested { get; set; }
}

public class MasterTrustSeriesStateDto
{
    /// <summary>REVOLVING or EARLY_AMORTIZATION. Absent is REVOLVING.</summary>
    public string? Phase { get; set; }

    public string? Event { get; set; }
    public double? Reserve { get; set; }
    public double? Pfa { get; set; }
    public double? FrozenNotes { get; set; }
    public double? FrozenNumerator { get; set; }
    public double? PfaReleaseDue { get; set; }
}

public class MasterTrustStateDto
{
    /// <summary>Percents, oldest first.</summary>
    public List<double>? WriteOffHistory { get; set; }

    /// <summary>Percents, oldest first.</summary>
    public List<double>? DelinquencyHistory { get; set; }

    /// <summary>Ineligible plus excess concentration dollars at the statement date, by group.</summary>
    public Dictionary<string, double>? Concentration { get; set; }

    /// <summary>Cash the certificates were left on the last payment date. Absent is 0.</summary>
    public double? CertificateCash { get; set; }
}

public class MasterTrustFadDto
{
    public DateTime? Date { get; set; }
    public double? PoolBalance { get; set; }

    /// <summary>Ineligible plus excess concentration dollars on the day, by group.</summary>
    public Dictionary<string, double>? Concentration { get; set; }
}

public class MasterTrustSeriesActualDto
{
    /// <summary>Percent. It holds for later months.</summary>
    public double? DiscountRate { get; set; }

    public double? PresentValueStart { get; set; }
    public double? PresentValueEnd { get; set; }
    public double? AllocationNumerator { get; set; }
    public double? NoteBalance { get; set; }
    public double? Fees { get; set; }
    public double? RedemptionDeposit { get; set; }
    public string? Event { get; set; }

    /// <summary>Printed fixed coupons by class, in percent, for a class whose coupon was reset. Each holds.</summary>
    public Dictionary<string, double>? Coupons { get; set; }
}

public class MasterTrustActualDto
{
    /// <summary>At least two to be used: the month's first day and its last, and any between.</summary>
    public List<MasterTrustFadDto>? Fads { get; set; }

    /// <summary>Percent.</summary>
    public double? WriteOffRatio { get; set; }

    /// <summary>Percent.</summary>
    public double? DelinquencyRatio { get; set; }

    /// <summary>Percent.</summary>
    public double? Delinquency { get; set; }

    public double? PoolBalanceDeficit { get; set; }
    public double? OtherFunds { get; set; }
    public Dictionary<string, MasterTrustSeriesActualDto>? Series { get; set; }
}

public class MasterTrustResultDto
{
    public List<MasterTrustPeriodDto> Periods { get; set; } = new();
    public List<MasterTrustPhaseMoveDto> PhasePath { get; set; } = new();
    public MasterTrustClosingDto Closing { get; set; } = new();
}

public class MasterTrustPhaseMoveDto
{
    public string Series { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Period { get; set; }
    public DateTime DistributionDate { get; set; }
    public string? Event { get; set; }
}

public class MasterTrustClosingDto
{
    public MasterTrustStateDto State { get; set; } = new();
    public List<MasterTrustSeriesClosingDto> Series { get; set; } = new();
    public List<InstallmentCohortDto> Pool { get; set; } = new();
}

public class MasterTrustSeriesClosingDto
{
    public string Name { get; set; } = "";

    /// <summary>Null once the series has ended.</summary>
    public MasterTrustSeriesStateDto? State { get; set; }

    public Dictionary<string, RevolvingClassClosingDto> Classes { get; set; } = new();
    public double Notes { get; set; }
    public double DiscountRate { get; set; }
    public string? Ended { get; set; }
}

public class MasterTrustFadReadingDto
{
    public DateTime Date { get; set; }
    public int Days { get; set; }
    public double PoolBalance { get; set; }
}

public class MasterTrustValuationDto
{
    public double Rate { get; set; }
    public double Beginning { get; set; }
    public double Ending { get; set; }
}

public class MasterTrustPeriodDto
{
    public int Period { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime DistributionDate { get; set; }
    public bool Replayed { get; set; }
    public double BeginningPool { get; set; }
    public double EndingPool { get; set; }
    public List<MasterTrustFadReadingDto> Fads { get; set; } = new();
    public double TimeWeightedPool { get; set; }
    public List<MasterTrustValuationDto> PresentValues { get; set; } = new();
    public double TotalFunds { get; set; }
    public double SumNumerators { get; set; }
    public double AllocationDenominator { get; set; }
    public double TransferorPct { get; set; }
    public double TransferorAllocation { get; set; }
    public double RequiredPoolBalance { get; set; }
    public double PoolBalanceDeficit { get; set; }
    public double? ComputedPoolBalanceDeficit { get; set; }
    public double AdditionsFace { get; set; }
    public double AdditionsCash { get; set; }
    public double CertificateFundedFace { get; set; }
    public double CertificateCash { get; set; }
    public List<OwnerTrustTestDto> Tests { get; set; } = new();
    public List<MasterTrustSeriesPeriodDto> Series { get; set; } = new();
    public InstallmentPoolPeriodDto Pool { get; set; } = new();
}

public class MasterTrustSeriesPeriodDto
{
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "";
    public string? Event { get; set; }
    public bool Private { get; set; }
    public double DiscountRate { get; set; }
    public double NotesStart { get; set; }
    public double NotesEnd { get; set; }
    public double TargetOcStart { get; set; }
    public double TargetOcEnd { get; set; }
    public double InvestedStart { get; set; }
    public double InvestedEnd { get; set; }
    public double PresentValueStart { get; set; }
    public double PresentValueEnd { get; set; }
    public double? ComputedPresentValueEnd { get; set; }
    public double DiscountRatioStart { get; set; }
    public double DiscountRatioEnd { get; set; }
    public double DiscountedInvestedStart { get; set; }
    public double DiscountedInvestedEnd { get; set; }
    public double SeriesShareStart { get; set; }
    public double SeriesShareEnd { get; set; }
    public double ConcentrationStart { get; set; }
    public double ConcentrationEnd { get; set; }
    public double IncrementalStart { get; set; }
    public double IncrementalEnd { get; set; }
    public double AdjustedInvestedStart { get; set; }
    public double AdjustedInvestedEnd { get; set; }
    public double AllocationNumerator { get; set; }
    public double? ComputedAllocationNumerator { get; set; }
    public double AllocationPct { get; set; }
    public double AllocatedPoolBalance { get; set; }
    public double Funds { get; set; }
    public double PfaRelease { get; set; }
    public double RedemptionDeposit { get; set; }
    public double ReserveDraw { get; set; }
    public double AvailableFunds { get; set; }
    public double Fees { get; set; }
    public double ServicingFee { get; set; }
    public double InterestPaid { get; set; }
    public List<double> PriorityPrincipal { get; set; } = new();
    public double RegularPrincipal { get; set; }
    public double PrincipalPaid { get; set; }
    public double PfaDeposit { get; set; }
    public double Pfa { get; set; }
    public double ReserveRequired { get; set; }
    public double ReserveDeposit { get; set; }
    public double ReserveRelease { get; set; }
    public double Reserve { get; set; }
    public double ClassR { get; set; }
    public List<OwnerTrustTestDto> Tests { get; set; } = new();
    public List<OwnerTrustClassDto> Classes { get; set; } = new();
}
