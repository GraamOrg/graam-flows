namespace GraamFlows.Revolving;

/// <summary>Surplus waiting to be shared this month, per group.</summary>
internal sealed class SharedCash
{
    public Dictionary<string, double> Excess { get; } = new();
    public Dictionary<string, double> Principal { get; } = new();

    public static void Add(Dictionary<string, double> pool, string group, double amount)
    {
        pool[group] = pool.GetValueOrDefault(group) + amount;
    }
}

/// <summary>
///     Runs a program's steps on one series. Steps are data, chosen at run time by the series' phase.
///     A step that does not apply to the phase is skipped, and an unknown step cannot get here.
/// </summary>
internal static class RevolvingWaterfall
{
    /// <param name="shared">
    ///     where surplus goes. Null is a dry run: the step that would release surplus does nothing,
    ///     so what the run spends is what the series still needs.
    /// </param>
    /// <param name="reallocate">
    ///     false on the first pass: excess finance from other series comes before principal.
    /// </param>
    public static void RunFinance(SeriesRun s, IReadOnlyList<WaterfallStep> steps, SharedCash? shared, bool reallocate)
    {
        foreach (var step in steps.Where(step => step.RunsIn(s.Phase)))
            switch (step.Verb)
            {
                case WaterfallVerb.PayInterest:
                    PayInterest(s, s.ClassFor(step.Class!));
                    break;
                case WaterfallVerb.PayFee:
                    PayFee(s, step.Fee!);
                    break;
                case WaterfallVerb.CoverDefaults:
                    foreach (var c in step.Class == null ? s.Classes : new List<ClassRun> { s.ClassFor(step.Class) })
                        CoverDefaults(s, c);
                    break;
                case WaterfallVerb.ReimburseChargeoff:
                    Reimburse(s, s.ClassFor(step.Class!));
                    break;
                case WaterfallVerb.ReallocatePrincipal:
                    if (reallocate)
                        ReallocatePrincipal(s, s.ClassFor(step.Class!), step.From!);
                    break;
                case WaterfallVerb.FundReserve:
                    FundReserve(s);
                    break;
                case WaterfallVerb.ShareExcess:
                    if (shared != null)
                        ShareExcess(s, shared);
                    break;
                case WaterfallVerb.ReleaseTransferor:
                    if (shared != null)
                        ReleaseFinance(s);
                    break;
                default:
                    throw new InvalidOperationException($"{step.Verb} does not run on finance collections.");
            }
    }

    public static void RunPrincipal(SeriesRun s, IReadOnlyList<WaterfallStep> steps, SharedCash? shared,
        DateTime distributionDate)
    {
        foreach (var step in steps.Where(step => step.RunsIn(s.Phase)))
            switch (step.Verb)
            {
                case WaterfallVerb.DepositPfa:
                    DepositFunding(s, distributionDate);
                    break;
                case WaterfallVerb.PayClass:
                    PayClass(s, s.ClassFor(step.Class!), step.WhenPaid);
                    break;
                case WaterfallVerb.SharePrincipal:
                    if (shared != null)
                        SharePrincipal(s, shared);
                    break;
                case WaterfallVerb.ReleaseTransferor:
                    if (shared != null)
                        ReleasePrincipal(s);
                    break;
                default:
                    throw new InvalidOperationException($"{step.Verb} does not run on principal collections.");
            }
    }

    /// <summary>
    ///     Pays the funded classes from the account, in order. Anything left once they are paid
    ///     rejoins principal, which can only happen if a class was written down after the deposit.
    /// </summary>
    public static void ReleaseFunding(SeriesRun s)
    {
        foreach (var c in s.Config.FundedClasses.Select(s.ClassFor))
        {
            var pay = Math.Min(s.Funding.Balance, Math.Max(0.0, c.Invested));
            s.Funding.Withdraw(pay);
            c.Outstanding -= pay;
            c.PrincipalPaid += pay;
            s.FundingRelease += pay;
        }

        s.Principal += s.Funding.Balance;
        s.FundingRelease += s.Funding.Balance;
        s.Funding.Withdraw(s.Funding.Balance);
    }

    /// <summary>
    ///     A note tranche with nothing left draws its own reserve, which "will ... be available only to
    ///     the holders of that tranche": its interest, then what it is still owed of its principal.
    /// </summary>
    /// <returns>what is left, once the tranche is owed nothing.</returns>
    public static double DrawReserve(SeriesRun s, double reserve)
    {
        var c = s.Classes[0];
        var interest = Math.Min(reserve, c.InterestDue);
        c.InterestDue -= interest;
        c.InterestPaid += interest;

        var principal = Math.Min(reserve - interest, c.Outstanding);
        c.Reinstate(Math.Min(principal, c.WrittenDown));
        c.Outstanding -= principal;
        c.PrincipalPaid += principal;
        return reserve - interest - principal;
    }

    /// <summary>Uncovered defaults write the series down in its stated order.</summary>
    public static void WriteDownUncovered(SeriesRun s)
    {
        var uncovered = s.Classes.Sum(c => c.DefaultDue);
        foreach (var c in s.Classes)
            c.DefaultDue = 0.0;

        foreach (var c in s.Config.WritedownOrder.Select(s.ClassFor))
        {
            var take = Math.Min(uncovered, Math.Max(0.0, c.Invested));
            c.WriteDown(take);
            uncovered -= take;
        }
    }

    private static double TakeFinance(SeriesRun s, double due)
    {
        var pay = Math.Min(s.Finance, due);
        s.Finance -= pay;
        s.Spent += pay;
        return pay;
    }

    private static double TakePrincipal(SeriesRun s, double due)
    {
        var pay = Math.Min(s.Principal, due);
        s.Principal -= pay;
        s.Spent += pay;
        return pay;
    }

    private static void PayInterest(SeriesRun s, ClassRun c)
    {
        var pay = TakeFinance(s, c.InterestDue);
        c.InterestDue -= pay;
        c.InterestPaid += pay;
    }

    private static void PayFee(SeriesRun s, string fee)
    {
        var pay = TakeFinance(s, s.FeeDue(fee));
        if (pay > 0)
            s.PayFee(fee, pay);
    }

    /// <summary>Finance that covers a default becomes principal.</summary>
    private static void CoverDefaults(SeriesRun s, ClassRun c)
    {
        var pay = TakeFinance(s, c.DefaultDue);
        c.DefaultDue -= pay;
        s.Principal += pay;
    }

    private static void Reimburse(SeriesRun s, ClassRun c)
    {
        var pay = TakeFinance(s, c.WrittenDown);
        c.Reinstate(pay);
        s.Principal += pay;
    }

    /// <summary>
    ///     Principal pays the senior class's unpaid interest and default, up to what the junior
    ///     classes have left. They bear it as a write-down. Covering a default sends the cash
    ///     straight back to principal, so that part only moves the loss.
    /// </summary>
    private static void ReallocatePrincipal(SeriesRun s, ClassRun senior, IReadOnlyList<string> from)
    {
        var capacity = from.Sum(key => Math.Max(0.0, s.ClassFor(key).Invested));

        var interest = Math.Min(Math.Min(senior.InterestDue, capacity), s.Principal);
        s.Principal -= interest;
        senior.InterestDue -= interest;
        senior.InterestPaid += interest;

        var loss = Math.Min(Math.Min(senior.DefaultDue, capacity - interest), s.Principal);
        senior.DefaultDue -= loss;

        var bear = interest + loss;
        foreach (var c in from.Select(s.ClassFor))
        {
            var take = Math.Min(bear, Math.Max(0.0, c.Invested));
            c.WriteDown(take);
            bear -= take;
        }
    }

    private static void FundReserve(SeriesRun s)
    {
        var pay = TakeFinance(s, Math.Max(0.0, s.ReserveRequired - s.Reserve));
        s.Reserve += pay;
        s.ReserveDeposit += pay;
    }

    private static void ShareExcess(SeriesRun s, SharedCash shared)
    {
        var surplus = s.Finance;
        s.Finance = 0.0;
        s.Surplus += surplus;
        if (s.Config.ExcessSharing)
        {
            s.ExcessOut += surplus;
            SharedCash.Add(shared.Excess, s.Config.Group, surplus);
        }
        else
        {
            s.TransferorFinance += surplus;
        }
    }

    private static void ReleaseFinance(SeriesRun s)
    {
        s.TransferorFinance += s.Finance;
        s.Surplus += s.Finance;
        s.Finance = 0.0;
    }

    private static void DepositFunding(SeriesRun s, DateTime distributionDate)
    {
        var deposit = TakePrincipal(s, s.Funding.Demand(s.ControlledThisMonth, s.FundingRoom));
        s.Funding.Deposit(deposit);

        if (s.Config.ExpectedDate is { } expected && OnOrAfterMonth(distributionDate, expected))
            ReleaseFunding(s);
    }

    private static void PayClass(SeriesRun s, ClassRun c, IReadOnlyList<string>? whenPaid)
    {
        if (whenPaid != null && whenPaid.Any(key => s.ClassFor(key).Invested >= SeriesRun.Cent))
            return;

        var pay = TakePrincipal(s, Math.Max(0.0, c.Invested));
        c.Outstanding -= pay;
        c.PrincipalPaid += pay;
    }

    private static void SharePrincipal(SeriesRun s, SharedCash shared)
    {
        var surplus = s.Principal;
        s.Principal = 0.0;
        if (s.Config.PrincipalSharing)
        {
            s.SharedPrincipalOut += surplus;
            SharedCash.Add(shared.Principal, s.Config.Group, surplus);
        }
        else
        {
            s.TransferorPrincipal += surplus;
        }
    }

    private static void ReleasePrincipal(SeriesRun s)
    {
        s.TransferorPrincipal += s.Principal;
        s.Principal = 0.0;
    }

    public static bool OnOrAfterMonth(DateTime date, DateTime target)
    {
        return (date.Year, date.Month).CompareTo((target.Year, target.Month)) >= 0;
    }
}
