using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface IAsset
{
    string AssetName { get; set; }
    string AssetId { get; set; }
    InterestRateType InterestRateType { get; set; }

    /// <summary>
    ///     Principal-repayment style (amortizing / bullet / PIK). Defaults to
    ///     <see cref="AmortizationType.Amortizing" /> so existing assets and
    ///     payloads that never set it keep the pre-existing scheduled-amortization
    ///     behavior unchanged.
    /// </summary>
    AmortizationType AmortizationType { get; set; }

    /// <summary>
    ///     How the coupon accrues per period. Defaults to <see cref="AccrualBasis.Thirty360" /> so
    ///     existing assets and payloads accrue exactly as before.
    /// </summary>
    AccrualBasis AccrualBasis { get; set; }

    /// <summary>
    ///     Payments a year (12 monthly — the default — 4 quarterly, 2 semi-annual). A less-than-monthly
    ///     asset pays interest and prepays, defaults and recovers only on its payment dates
    ///     (<c>PaymentSchedule</c>).
    /// </summary>
    int PaymentFrequency { get; set; }

    /// <summary>The asset's next payment date, which places its payment months. Required below 12.</summary>
    DateTime? NextPaymentDate { get; set; }

    /// <summary>
    ///     The absolute period of the first payment row, when the caller places it directly (bought
    ///     collateral pays on its purchase calendar). Overrides <see cref="NextPaymentDate" />.
    /// </summary>
    int? FirstPaymentAbsT { get; set; }

    /// <summary>
    ///     The asset's stated maturity date, or null. When stated it is the asset's maturity: it repays
    ///     in the projection row that collects the date (the row's collection cutoff, as for a payment
    ///     date), and its last payment pays the interest accrued only up to the date.
    /// </summary>
    DateTime? MaturityDate { get; set; }

    /// <summary>
    ///     The date the asset's whole-month remaining term was counted from (its tape's as-of date), or
    ///     null. With a stated calendar and maturity, the maturity then carries the months on that
    ///     monthly grid since the asset's last payment, and a scheduled date in the grid month the
    ///     maturity ends is no payment (<c>PaymentSchedule.IsPayment</c>).
    /// </summary>
    DateTime? RemainingTermAsOfDate { get; set; }

    DateTime OriginalDate { get; set; }
    double OriginalBalance { get; set; }
    double OriginalInterestRate { get; set; }
    double CurrentInterestRate { get; set; }
    int OriginalAmortizationTerm { get; set; }
    double CurrentBalance { get; set; }
    double BalanceAtIssuance { get; set; }
    double OriginalLTV { get; set; }
    string GroupNum { get; set; }
    string LoanStatus { get; set; }
    double ServiceFee { get; set; }
    double DebtService { get; set; }
    int InitialAdjustmentPeriod { get; set; }
    int AdjustmentPeriod { get; set; }
    double InitialRate { get; set; }
    MarketDataInstEnum IndexName { get; set; }
    double IndexMargin { get; set; }
    double? LifeAdjustmentCap { get; set; }
    double? LifeAdjustmentFloor { get; set; }
    double? AdjustmentCap { get; set; }
    bool IsIO { get; set; }
    int? IOTerm { get; set; }

    /// <summary>
    ///     The amortization schedule, in months from the first amortizing payment, that a BALLOON
    ///     loan's level payment is computed over when it is longer than the loan's term to maturity
    ///     (a 10-year commercial mortgage on a 30-year schedule). Null or 0: the payment amortizes
    ///     over the term, as before.
    /// </summary>
    int? BalloonAmortizationTerm { get; set; }
    double? ForbearanceAmt { get; set; }
    string StepDatesList { get; set; }
    string StepRatesList { get; set; }

    /// <summary>
    ///     Weighted average loan age in months. Used for ABS-to-SMM conversion where
    ///     the pool's seasoning affects the prepayment rate formula.
    /// </summary>
    int Wala { get; set; }
}