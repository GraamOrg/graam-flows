namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     One step of an asset's prepayment penalty schedule (<see cref="IAsset.PrepaymentPenalties" />):
///     from the payment date in <see cref="From" />'s month on, a voluntary prepayment pays
///     <see cref="Pct" /> percent of the principal prepaid, until the next step. A schedule states
///     its end as a last step of 0.
/// </summary>
public class PrepaymentPenaltyStep
{
    public PrepaymentPenaltyStep()
    {
    }

    public PrepaymentPenaltyStep(DateTime from, double pct)
    {
        From = from;
        Pct = pct;
    }

    /// <summary>The first month the step applies in (its day is ignored).</summary>
    public DateTime From { get; set; }

    /// <summary>The penalty, in percent of the principal prepaid: 10 is 10%.</summary>
    public double Pct { get; set; }
}
