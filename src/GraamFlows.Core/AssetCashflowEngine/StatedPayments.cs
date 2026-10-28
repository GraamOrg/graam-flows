using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     An asset that states its own scheduled payments (<see cref="IAsset.ScheduledPayments" />).
///
///     Some equipment-loan pools state their balance as the receivables' scheduled payments
///     discounted monthly at a stated rate, and most of their obligors pay once a year: the pool's
///     printed schedule has a large payment in one month and little or nothing in the others. A level
///     payment cannot reproduce it, and a less-than-monthly asset only moves its interest to its
///     payment months. Stating the payments themselves, with the discount rate as the coupon and the
///     discounted value as the balance, pays that schedule exactly: each period's interest is the
///     coupon on the beginning balance, the rest of the payment is principal, and in a month whose
///     payment is below the interest the shortfall accretes into the balance (it is the discount
///     unwinding).
///
///     The schedule replaces the level payment, the payment calendar and the maturity, so the features
///     that set those are refused rather than half-honoured.
/// </summary>
public static class StatedPayments
{
    /// <summary>The asset's stated payments, or null when it states none (an empty schedule is none).</summary>
    public static double[]? Of(IAsset asset) =>
        asset.ScheduledPayments is { Length: > 0 } payments ? payments : null;

    /// <summary>
    ///     Throw an <see cref="ArgumentException" /> when the asset states a schedule together with a
    ///     feature the schedule replaces or that the engine does not apply to one.
    /// </summary>
    public static void Validate(IAsset asset)
    {
        if (asset.ScheduledPrincipal is > 0)
        {
            var with = Of(asset) != null ? "scheduled payments"
                : asset.AmortizationType == AmortizationType.Pik ? "a PIK coupon"
                : asset.AmortizationType == AmortizationType.Bullet ? "a bullet repayment"
                : null;
            if (with != null)
                throw new ArgumentException(
                    $"Asset {asset.AssetId}: a stated principal amount cannot be combined with {with}.",
                    nameof(IAsset.ScheduledPrincipal));
        }

        if (Of(asset) is not { } payments)
            return;

        string? conflict = null;
        if (PaymentSchedule.MonthsBetween(asset.PaymentFrequency) > 1)
            conflict = $"a payment frequency of {asset.PaymentFrequency} a year (the schedule states a payment, " +
                       "zero or not, for every monthly period)";
        else if (asset.NextPaymentDate != null || asset.FirstPaymentAbsT != null)
            conflict = "a stated payment date (the schedule places the payments)";
        else if (asset.MaturityDate != null)
            conflict = "a stated maturity date (the asset ends with its last scheduled payment)";
        else if (asset.IsIO || asset.IOTerm is > 0)
            conflict = "an interest-only period";
        else if (asset.InterestRateType == InterestRateType.ARM || asset.InitialAdjustmentPeriod > 0)
            conflict = "an adjustable rate";
        else if (asset.AmortizationType == AmortizationType.Pik)
            conflict = "a PIK coupon";
        else if (asset.AmortizationType == AmortizationType.Bullet)
            conflict = "a bullet repayment";
        else if (asset.BalloonAmortizationTerm is > 0)
            conflict = "a balloon amortization term";

        if (conflict != null)
            throw new ArgumentException(
                $"Asset {asset.AssetId}: scheduled payments cannot be combined with {conflict}.",
                nameof(IAsset.ScheduledPayments));

        for (var p = 0; p < payments.Length; p++)
            if (!double.IsFinite(payments[p]) || payments[p] < 0)
                throw new ArgumentException(
                    $"Asset {asset.AssetId}: scheduled payment {p + 1} is {payments[p]}; payments must be " +
                    "finite and not negative.",
                    nameof(IAsset.ScheduledPayments));
    }
}
