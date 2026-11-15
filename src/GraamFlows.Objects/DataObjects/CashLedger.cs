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

        /// <summary>
        ///     Principal paid to classes beyond what their balances fell by. The cash is taken and
        ///     paid, so the two sides agree; the class that got it did not hold that much.
        /// </summary>
        public double PaidPastBalance { get; internal set; }

        /// <summary>
        ///     What the step took the running totals below zero by: cash it paid out beyond what it
        ///     held. Its taken and paid agree on that cash, so this is where it shows.
        /// </summary>
        public double Overdrew { get; internal set; }

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

    /// <summary>
    ///     Principal left after the last step that no class took. On a deal with a certificate that
    ///     is principal the structures could not place while a note still had a balance (once the
    ///     notes are gone the certificate is paid it); on a deal without one, principal with no
    ///     residual class to take it. Counted total by total, so an overdraft on one principal
    ///     total does not hide principal left on another.
    /// </summary>
    public double LeftPrincipal { get; private set; }

    /// <summary>
    ///     What the running totals stood below zero by at the end of each period, summed: cash paid
    ///     out beyond what was collected. Collected equals the steps' paid, plus taken-not-paid less
    ///     paid-not-taken, plus what was left, less this.
    /// </summary>
    public double Overdrawn { get; private set; }

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

    public void Record(string step, DateTime date, double taken, double paid, double paidPastBalance = 0,
        double overdrew = 0)
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
            if (paidPastBalance > Cent) s.PaidPastBalance += paidPastBalance;
            if (Math.Abs(overdrew) > Cent) s.Overdrew += overdrew;
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

    /// <summary>
    ///     What the period's running totals ended at, each given on its own: the interest total and
    ///     each principal total. A positive total is left; a negative one is overdrawn. They are
    ///     not netted, so an overdraft on one total does not hide cash left on another.
    /// </summary>
    public void Leftover(double interest, params double[] principal)
    {
        lock (_lock)
        {
            var overdrawn = Math.Max(-interest, 0) + principal.Sum(p => Math.Max(-p, 0));
            if (overdrawn > Cent) Overdrawn += overdrawn;
            var leftPrincipal = principal.Sum(p => Math.Max(p, 0));
            if (Math.Max(interest, 0) + leftPrincipal <= Cent)
                return;
            PeriodsLeft++;
            LeftInterest += Math.Max(interest, 0);
            LeftPrincipal += leftPrincipal;
        }
    }
}
