namespace GraamFlows.Revolving;

/// <summary>
///     One month of a de-linked note series. A tranche is an engine series of one class, so it keeps
///     its own phase, funding account and tests, but it owns no cash: every tranche's share of the
///     pool goes into the one finance bucket and the one principal bucket here, and the program's
///     steps run across tranches in class order.
/// </summary>
internal sealed class NoteMonth
{
    public NoteMonth(DelinkedNotes notes, IReadOnlyList<SeriesRun> tranches)
    {
        Notes = notes;
        Tranches = tranches;
    }

    public DelinkedNotes Notes { get; }
    public IReadOnlyList<SeriesRun> Tranches { get; }

    public double Finance { get; set; }
    public double Principal { get; set; }

    /// <summary>What finance covered of the series' default, which goes on to principal.</summary>
    public double DefaultCovered { get; set; }

    /// <summary>Finance left after the steps: the excess paid to the transferor.</summary>
    public double Excess { get; set; }

    public double TransferorPrincipal { get; set; }

    /// <summary>The default finance did not cover, charged off against the tranches.</summary>
    public double UncoveredDefault { get; set; }

    /// <summary>Principal moved to pay senior interest and fees, which junior tranches bear.</summary>
    public double PrincipalReallocated { get; set; }

    /// <summary>The ceiling on that: the principal allocable to the junior classes this month.</summary>
    public double JuniorPrincipalLeft { get; set; }

    public IEnumerable<SeriesRun> OfClass(string key)
    {
        return Tranches.Where(t => t.ClassKey == key);
    }

    public string SeniorClass => Notes.ClassOrder[0];

    public IEnumerable<string> JuniorClasses => Notes.ClassOrder.Skip(1);

    public double NominalLiquidation(string key)
    {
        return OfClass(key).Sum(t => t.AdjustedInvested);
    }
}

/// <summary>
///     The finance and principal steps of a de-linked note series. Within a class, cash goes pro
///     rata to what each tranche is owed, so a class is short together.
/// </summary>
internal static class NoteWaterfall
{
    public static void RunFinance(NoteMonth month, IReadOnlyList<WaterfallStep> steps)
    {
        foreach (var step in steps)
            switch (step.Verb)
            {
                case WaterfallVerb.PayInterest:
                    PayInterest(month, step.Class!);
                    break;
                case WaterfallVerb.PayFee:
                    PayFee(month, step.Fee!);
                    break;
                case WaterfallVerb.CoverDefaults:
                    CoverDefaults(month);
                    break;
                case WaterfallVerb.ReimburseChargeoff:
                    Reimburse(month, step.Class!);
                    break;
                case WaterfallVerb.FundReserve:
                    FundReserve(month);
                    break;
                case WaterfallVerb.ShareExcess:
                case WaterfallVerb.ReleaseTransferor:
                    month.Excess += month.Finance;
                    month.Finance = 0.0;
                    break;
                default:
                    throw new InvalidOperationException($"{step.Verb} does not run on a note series' finance.");
            }
    }

    public static void RunPrincipal(NoteMonth month, IReadOnlyList<WaterfallStep> steps, DateTime distributionDate)
    {
        foreach (var step in steps)
            switch (step.Verb)
            {
                case WaterfallVerb.ReallocatePrincipal:
                    Reallocate(month, step);
                    break;
                case WaterfallVerb.DepositPfa:
                    DepositAll(month, distributionDate);
                    break;
                case WaterfallVerb.SharePrincipal:
                case WaterfallVerb.ReleaseTransferor:
                    month.TransferorPrincipal += month.Principal;
                    month.Principal = 0.0;
                    break;
                default:
                    throw new InvalidOperationException($"{step.Verb} does not run on a note series' principal.");
            }
    }

    private static void PayInterest(NoteMonth month, string key)
    {
        var classes = month.OfClass(key).Select(t => t.Classes[0]).ToList();
        var due = classes.Sum(c => c.InterestDue);
        if (due <= 0)
            return;

        var pay = Math.Min(month.Finance, due);
        month.Finance -= pay;
        foreach (var c in classes)
        {
            var share = pay * c.InterestDue / due;
            c.InterestDue -= share;
            c.InterestPaid += share;
        }
    }

    private static void PayFee(NoteMonth month, string fee)
    {
        var due = month.Tranches.Sum(t => t.FeeDue(fee));
        if (due <= 0)
            return;

        var pay = Math.Min(month.Finance, due);
        month.Finance -= pay;
        foreach (var t in month.Tranches.ToList())
        {
            var share = pay * t.FeeDue(fee) / due;
            if (share > 0)
                t.PayFee(fee, share);
        }
    }

    /// <summary>
    ///     The series' default is covered as one amount, whichever class names the step, so only the
    ///     first such step moves cash. What it covers becomes principal.
    /// </summary>
    private static void CoverDefaults(NoteMonth month)
    {
        var classes = month.Tranches.Select(t => t.Classes[0]).ToList();
        var due = classes.Sum(c => c.DefaultDue);
        if (due <= 0)
            return;

        var pay = Math.Min(month.Finance, due);
        month.Finance -= pay;
        month.Principal += pay;
        month.DefaultCovered += pay;
        foreach (var c in classes)
            c.DefaultDue -= pay * c.DefaultDue / due;
    }

    private static void Reimburse(NoteMonth month, string key)
    {
        var tranches = month.OfClass(key).ToList();
        var deficit = tranches.Sum(t => t.Classes[0].WrittenDown);
        if (deficit <= 0)
            return;

        var pay = Math.Min(month.Finance, deficit);
        month.Finance -= pay;
        month.Principal += pay;
        foreach (var t in tranches)
            t.Classes[0].Reinstate(pay * t.Classes[0].WrittenDown / deficit);
        Subordination.ReleaseUsage(month, key, pay, deficit);
    }

    private static void FundReserve(NoteMonth month)
    {
        foreach (var t in month.Tranches)
        {
            var pay = Math.Min(month.Finance, Math.Max(0.0, t.ReserveRequired - t.Reserve));
            month.Finance -= pay;
            t.Reserve += pay;
            t.ReserveDeposit += pay;
        }
    }

    /// <summary>
    ///     Principal pays a senior tranche's unpaid interest or fee, up to what its juniors still
    ///     owe it and what the junior classes were allocated this month. They bear it as a reduction.
    /// </summary>
    private static void Reallocate(NoteMonth month, WaterfallStep step)
    {
        var interest = step.Covers != ReallocationCovers.Fee;
        var wants = month.OfClass(step.Class!)
            .Select(t => (Tranche: t, Want: Math.Min(Deficiency(t, interest), Subordination.Unused(t, step.From!))))
            .Where(w => w.Want > 0)
            .ToList();
        var total = wants.Sum(w => w.Want);
        if (total <= 0)
            return;

        var pay = Math.Min(total, Math.Min(month.Principal, month.JuniorPrincipalLeft));
        month.Principal -= pay;
        month.JuniorPrincipalLeft -= pay;
        month.PrincipalReallocated += pay;
        foreach (var (tranche, want) in wants)
        {
            var share = pay * want / total;
            Settle(tranche, share, interest);
            Subordination.Bear(month, tranche, share, step.From!);
        }
    }

    private static double Deficiency(SeriesRun t, bool interest)
    {
        return interest ? t.Classes[0].InterestDue : t.FeesDue;
    }

    private static void Settle(SeriesRun t, double amount, bool interest)
    {
        if (interest)
        {
            t.Classes[0].InterestDue -= amount;
            t.Classes[0].InterestPaid += amount;
            return;
        }

        var due = t.FeesDue;
        foreach (var fee in t.Config.Fees)
        {
            var share = amount * t.FeeDue(fee.Name) / due;
            if (share > 0)
                t.PayFee(fee.Name, share);
        }
    }

    /// <summary>
    ///     Targeted deposits, senior class first and pro rata to target within a class. A junior
    ///     tranche takes only what leaves the senior tranches' required amounts standing. What it
    ///     cannot take carries as a deposit shortfall. Any account then due pays, whether or not it
    ///     took a deposit this month: one filled early has nothing left to take on its date.
    /// </summary>
    private static void DepositAll(NoteMonth month, DateTime distributionDate)
    {
        foreach (var key in month.Notes.ClassOrder)
        {
            var tranches = month.OfClass(key).ToList();
            var targets = tranches
                .Select(t => (Tranche: t, Target: Target(t)))
                .Where(x => x.Target > 0)
                .ToList();
            var total = targets.Sum(x => x.Target);
            if (total > 0)
            {
                var room = key == month.SeniorClass ? double.PositiveInfinity : Subordination.DepositRoom(month, key);
                var pay = Math.Min(month.Principal, Math.Min(total, room));
                month.Principal -= pay;
                foreach (var (tranche, target) in targets)
                    tranche.Funding.Deposit(pay * target / total);
            }

            foreach (var tranche in tranches.Where(t => t.Funding.Balance > 0 && Pays(t, distributionDate)))
            {
                RevolvingWaterfall.ReleaseFunding(tranche);
                month.Principal += tranche.Principal;
                tranche.Principal = 0.0;
            }
        }
    }

    private static double Target(SeriesRun t)
    {
        return t.Phase switch
        {
            SeriesPhase.EarlyAmortization or SeriesPhase.Amortizing => t.AdjustedInvested,
            SeriesPhase.Accumulating => t.Funding.Demand(t.ControlledThisMonth, t.FundingRoom),
            _ => 0.0
        };
    }

    private static bool Pays(SeriesRun t, DateTime distributionDate)
    {
        if (t.Phase is SeriesPhase.EarlyAmortization or SeriesPhase.Amortizing)
            return true;

        return t.Config.ExpectedDate is { } expected && RevolvingWaterfall.OnOrAfterMonth(distributionDate, expected);
    }
}
