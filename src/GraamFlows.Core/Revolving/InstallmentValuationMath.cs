namespace GraamFlows.Revolving;

/// <summary>
///     Present values of a 0% installment schedule. A rate is annual, compounded monthly as
///     <c>r / 12</c>, and every payment falls at a month end.
/// </summary>
public static class InstallmentValuationMath
{
    /// <summary>
    ///     <c>Σ pay_k / (1 + r/12)^k</c> for k from 1: the first payment falls due one month from now.
    ///     PV is linear in cash, so scaling a schedule by a share scales its value at every rate by it.
    /// </summary>
    public static double PresentValue(IReadOnlyList<double> payments, double annualRate)
    {
        return PresentValue(payments, 0, annualRate);
    }

    /// <param name="from">the first payment to count, falling due one month from now.</param>
    internal static double PresentValue(IReadOnlyList<double> payments, int from, double annualRate)
    {
        var discount = 1.0 / (1.0 + annualRate / 12.0);
        var factor = discount;
        var value = 0.0;
        for (var k = from; k < payments.Count; k++)
        {
            value += payments[k] * factor;
            factor *= discount;
        }

        return value;
    }

    /// <summary>
    ///     The ratio a printed discounted amount is taken on: face over present value, rounded to five
    ///     places, half away from zero. It rounds the decimal the ratio prints as, so a ratio that is
    ///     a half at the sixth place rounds up as it would on paper. A pool with no value has no ratio.
    /// </summary>
    public static double DiscountRatio(double faceBalance, double presentValue)
    {
        if (!double.IsFinite(faceBalance) || !double.IsFinite(presentValue) || presentValue <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(presentValue),
                $"A discount ratio needs a positive present value, and it is {presentValue}.");

        return (double)Math.Round((decimal)(faceBalance / presentValue), 5, MidpointRounding.AwayFromZero);
    }
}
