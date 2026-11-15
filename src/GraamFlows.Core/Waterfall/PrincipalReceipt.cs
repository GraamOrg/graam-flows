namespace GraamFlows.Waterfall;

/// <summary>
///     What a principal payment did, reported by the payable that made it. A step spends
///     <see cref="Paid" />: it does not work the payment out afterwards from balances, which read
///     a class paid past its balance as paid nothing and a payment to a class outside the measured
///     set as no payment at all.
/// </summary>
/// <param name="Paid">
///     The principal the response will show for the classes the payable reached: the cash that
///     left the step.
/// </param>
/// <param name="Reduced">
///     What those classes' balances fell by. Less than <see cref="Paid" /> when a class was paid
///     more than it held.
/// </param>
public readonly record struct PrincipalReceipt(double Paid, double Reduced)
{
    public static PrincipalReceipt None => default;

    /// <summary>Cash paid to classes beyond what their balances fell by.</summary>
    public double PastBalance => Math.Max(Paid - Reduced, 0);

    public static PrincipalReceipt operator +(PrincipalReceipt a, PrincipalReceipt b) =>
        new(a.Paid + b.Paid, a.Reduced + b.Reduced);
}
