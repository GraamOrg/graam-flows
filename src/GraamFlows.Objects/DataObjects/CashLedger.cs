namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     A check on the waterfall's own cash, asked for per run (<see cref="IDeal.ReportCashLedger" />).
///     The waterfall carries the period's interest and principal in running totals, and each step
///     takes what it reckons it paid off them. Nothing compared that with the cash the classes
///     were actually paid, so a step could take funds and pay no one, or pay a class and take
///     nothing, and the run came back looking complete. This records both sides for every step
///     of every period, and what was left at the end with no class to take it. It only reads:
///     a run reports the same cashflows with it on or off.
/// </summary>
public sealed class CashLedger
{
    /// <summary>One step's totals over the run.</summary>
    public sealed class StepTotals
    {
        /// <summary>Periods the step ran in.</summary>
        public int Periods { get; internal set; }

        /// <summary>What the step took off the running totals.</summary>
        public double Taken { get; internal set; }

        /// <summary>The cash recorded on the classes (and held in the funds account) while it ran.</summary>
        public double Paid { get; internal set; }

        /// <summary>Taken with no payment recorded, summed over the periods where that happened.</summary>
        public double TakenNotPaid { get; internal set; }

        /// <summary>Paid with nothing taken, summed over the periods where that happened.</summary>
        public double PaidNotTaken { get; internal set; }

        /// <summary>Periods in which the two sides differed by more than a cent.</summary>
        public int PeriodsOff { get; internal set; }

        public DateTime? FirstPeriodOff { get; internal set; }

        /// <summary>The largest single-period difference, signed: positive is taken and not paid.</summary>
        public double LargestGap { get; internal set; }
    }

    private const double Cent = 0.01;
    private readonly object _lock = new();
    private readonly Dictionary<string, StepTotals> _steps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    /// <summary>Interest and principal that entered the waterfall, plus the servicing fee paid to a strip.</summary>
    public double Collected { get; private set; }

    /// <summary>Interest left after the last step with no residual class to take it.</summary>
    public double LeftInterest { get; private set; }

    /// <summary>Principal left after the last step with no residual class and no certificate.</summary>
    public double LeftPrincipal { get; private set; }

    /// <summary>
    ///     Principal left after the last step on a deal with a certificate. The certificate's
    ///     balance is the pool less the notes, so it carries this without being paid it.
    /// </summary>
    public double LeftPrincipalBesideCertificate { get; private set; }

    /// <summary>Periods that ended with cash no class took.</summary>
    public int PeriodsLeft { get; private set; }

    /// <summary>The steps in the order they first ran.</summary>
    public IReadOnlyList<KeyValuePair<string, StepTotals>> Steps
    {
        get
        {
            lock (_lock) return _order.Select(n => new KeyValuePair<string, StepTotals>(n, _steps[n])).ToList();
        }
    }

    public void Collect(double amount)
    {
        lock (_lock) Collected += amount;
    }

    public void Record(string step, DateTime date, double taken, double paid)
    {
        lock (_lock)
        {
            if (!_steps.TryGetValue(step, out var s))
            {
                _steps[step] = s = new StepTotals();
                _order.Add(step);
            }

            s.Periods++;
            s.Taken += taken;
            s.Paid += paid;
            var gap = taken - paid;
            if (Math.Abs(gap) <= Cent)
                return;
            s.PeriodsOff++;
            s.FirstPeriodOff ??= date;
            if (gap > 0) s.TakenNotPaid += gap;
            else s.PaidNotTaken -= gap;
            if (Math.Abs(gap) > Math.Abs(s.LargestGap)) s.LargestGap = gap;
        }
    }

    public void Leftover(double interest, double principal, bool hasCertificate)
    {
        interest = Math.Max(0, interest);
        principal = Math.Max(0, principal);
        if (interest + principal <= Cent)
            return;
        lock (_lock)
        {
            PeriodsLeft++;
            LeftInterest += interest;
            if (hasCertificate) LeftPrincipalBesideCertificate += principal;
            else LeftPrincipal += principal;
        }
    }
}
