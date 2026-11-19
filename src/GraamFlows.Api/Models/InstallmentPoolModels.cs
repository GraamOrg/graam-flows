namespace GraamFlows.Api.Models;

// An installment pool: cohorts of fixed 0% installment contracts, each a schedule of remaining
// payments, valued at the present value of those payments at a discount rate. Rates, shares and
// prices are percents, as everywhere on this API: a pvRate of 9 is 9%, a percentOfFace of 86.5 pays
// 86.5 cents per dollar of face. Amounts are dollars.

/// <summary>The body of <c>POST /api/revolving/pool</c>: a pool run with no series.</summary>
public class RevolvingPoolRequest
{
    /// <summary>Record date of the last closed monthly period. It must be a month end.</summary>
    public DateTime StatementDate { get; set; }

    /// <summary>Months to run. Defaults to the length of the pool's actual periods.</summary>
    public int? Periods { get; set; }

    /// <summary>Required, with model INSTALLMENT.</summary>
    public RevolvingPoolDto? Pool { get; set; }
}

public class RevolvingPoolDto
{
    /// <summary>CARD or INSTALLMENT. Absent is CARD, the pool every existing program has.</summary>
    public string? Model { get; set; }

    /// <summary>Annual percents the pool is valued at, compounded monthly as r / 12.</summary>
    public List<double>? DiscountRates { get; set; }

    /// <summary>The pool at the statement date: one or more cohorts, each with its schedule.</summary>
    public List<InstallmentCohortDto>? Cohorts { get; set; }

    /// <summary>Forecast drivers, for the months after the actual periods.</summary>
    public InstallmentDriversDto? Drivers { get; set; }

    /// <summary>Printed monthly figures. Replay runs these, then any forecast months.</summary>
    public List<InstallmentActualDto>? ActualPeriods { get; set; }

    /// <summary>Purchases in forecast months, each naming its period.</summary>
    public List<InstallmentPurchaseDto>? Purchases { get; set; }
}

/// <summary>Contracts that pay the same schedule.</summary>
public class InstallmentCohortDto
{
    public string? Name { get; set; }

    /// <summary>
    ///     Months since the contracts were bought, as the run's first month opens; it reads the
    ///     forecast curves. Required on an opening cohort; a purchase is 0 if it states none.
    /// </summary>
    public int? Age { get; set; }

    /// <summary>
    ///     Remaining scheduled payments by month, in dollars: the first falls due at the close of the
    ///     next month. Its sum is the face. Required on an opening cohort.
    /// </summary>
    public double[]? Schedule { get; set; }

    /// <summary>On a purchase, in place of a schedule: face paid in <see cref="Term" /> level payments.</summary>
    public double? Face { get; set; }

    /// <summary>Months, with <see cref="Face" />.</summary>
    public int? Term { get; set; }
}

/// <summary>Cohorts bought at the close of a month.</summary>
public class InstallmentPurchaseDto : InstallmentCohortDto
{
    /// <summary>On a forecast purchase: the run month it closes in, 0 being the month after the statement date.</summary>
    public int? Period { get; set; }

    /// <summary>One of this or <see cref="Cash" />.</summary>
    public InstallmentPriceDto? Price { get; set; }

    /// <summary>The cash paid, as printed. One of this or <see cref="Price" />.</summary>
    public double? Cash { get; set; }
}

/// <summary>One of percentOfFace or pvRate.</summary>
public class InstallmentPriceDto
{
    /// <summary>Percent of face: 86.5 pays 86.5 cents per dollar.</summary>
    public double? PercentOfFace { get; set; }

    /// <summary>Annual percent: the cash is the present value of the payments bought at this rate.</summary>
    public double? PvRate { get; set; }
}

/// <summary>
///     Forecast drivers. A rate is a number or a vector by cohort age, as a monthly percent of the
///     schedule, or <c>{"annual": ...}</c> for an annual percent that converts geometrically, as CPR
///     to SMM: <c>1 − (1 − annual)^(1/12)</c>. Age 0 is a cohort's first month; a short vector holds its last.
/// </summary>
public class InstallmentDriversDto
{
    /// <summary>Prepayments in full, a share of the schedule left after the month's payment. Required.</summary>
    public RevolvingDriverDto? PrepaymentRate { get; set; }

    /// <summary>Upgrades, a share of the same part, on their own cash line. Absent is 0, disclosed.</summary>
    public RevolvingDriverDto? UpgradeRate { get; set; }

    /// <summary>Gross write-offs, a share of the whole schedule as the month opens. Required. No recoveries.</summary>
    public RevolvingDriverDto? WriteOffRate { get; set; }

    /// <summary>Face repurchased in each forecast month, the first value the first forecast month. Past the end is none.</summary>
    public double[]? RepurchaseAmounts { get; set; }

    /// <summary>Required with repurchaseAmounts.</summary>
    public InstallmentPriceDto? RepurchasePrice { get; set; }

    /// <summary>Spend a share of each month's collections on fresh level contracts. Absent buys none, disclosed.</summary>
    public InstallmentReinvestDto? Reinvest { get; set; }
}

public class InstallmentReinvestDto
{
    /// <summary>Percent of the month's collections spent. Required.</summary>
    public double? Share { get; set; }

    /// <summary>Required.</summary>
    public InstallmentPriceDto? Price { get; set; }

    /// <summary>The terms bought, whose weights are percents of the face bought and come to 100. Required.</summary>
    public List<InstallmentTermDto>? Terms { get; set; }
}

public class InstallmentTermDto
{
    /// <summary>Months.</summary>
    public int? Term { get; set; }

    /// <summary>Percent of the face bought.</summary>
    public double? Weight { get; set; }

    /// <summary>Months since origination as bought, for the forecast curves. Absent is 0: new contracts.</summary>
    public int? Age { get; set; }
}

/// <summary>A printed month. Scheduled and prepaid collections and write-offs are required.</summary>
public class InstallmentActualDto
{
    /// <summary>Printed beginning face; reported as rollBreak, never reset to.</summary>
    public double? BeginningFace { get; set; }

    public double? ScheduledCollections { get; set; }
    public double? PrepaidCollections { get; set; }
    public double? UpgradeCollections { get; set; }
    public double? WriteOffs { get; set; }

    /// <summary>Face repurchased.</summary>
    public double? Repurchases { get; set; }

    /// <summary>One of this or repurchasePrice, where there are repurchases.</summary>
    public double? RepurchaseCash { get; set; }

    public InstallmentPriceDto? RepurchasePrice { get; set; }

    /// <summary>Cohorts bought at the month's close, each with its price or cash.</summary>
    public List<InstallmentPurchaseDto>? Additions { get; set; }

    /// <summary>Printed ending face; reported as ledgerBreak.</summary>
    public double? EndingFace { get; set; }
}

public class RevolvingPoolResponse
{
    public List<InstallmentPoolPeriodDto> Periods { get; set; } = new();

    /// <summary>The pool as the run closed it, ages advanced: send it as the next run's cohorts.</summary>
    public List<InstallmentCohortDto> Closing { get; set; } = new();

    /// <summary>What the run took as 0 or none because the request did not state it.</summary>
    public List<string> Disclosures { get; set; } = new();

    public long ElapsedMs { get; set; }
}

public class InstallmentPoolPeriodDto
{
    public int Period { get; set; }
    public DateTime PeriodEnd { get; set; }
    public bool Replayed { get; set; }
    public double BeginningFace { get; set; }

    /// <summary>What the schedule had falling due, after write-offs and repurchases.</summary>
    public double ScheduledDue { get; set; }

    /// <summary>Printed on a replayed month; what fell due on a forecast one.</summary>
    public double ScheduledCollections { get; set; }

    public double PrepaidCollections { get; set; }
    public double UpgradeCollections { get; set; }

    /// <summary>The one collections bucket: scheduled + prepaid + upgrades.</summary>
    public double Collections { get; set; }

    public double WriteOffs { get; set; }

    /// <summary>Face repurchased.</summary>
    public double Repurchases { get; set; }

    public double RepurchaseCash { get; set; }
    public double AdditionsFace { get; set; }
    public double AdditionsCash { get; set; }
    public double EndingFace { get; set; }
    public int Cohorts { get; set; }

    /// <summary>Printed beginning face less the ledger's. Absent unless printed.</summary>
    public double? RollBreak { get; set; }

    /// <summary>Ledger ending face less the printed one. Absent unless printed.</summary>
    public double? LedgerBreak { get; set; }

    public List<InstallmentValuationDto> Valuations { get; set; } = new();
}

/// <summary>
///     The pool's present value at one rate, Σ pay_k / (1 + r/12)^k with payments at month end, and the
///     value each line took at the month's close:
///     <c>ending = beginning × (1 + r/12) − scheduledDue − writeOffs − repurchases − prepaidCollections
///     − upgradeCollections − scheduledVariance + additions</c>.
/// </summary>
public class InstallmentValuationDto
{
    /// <summary>Annual percent.</summary>
    public double Rate { get; set; }

    public double Beginning { get; set; }
    public double WriteOffs { get; set; }
    public double Repurchases { get; set; }
    public double PrepaidCollections { get; set; }
    public double UpgradeCollections { get; set; }

    /// <summary>Replayed months: the value of printed scheduled collections less what fell due.</summary>
    public double ScheduledVariance { get; set; }

    public double Additions { get; set; }
    public double Ending { get; set; }
}
