using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Revolving;

/// <summary>A class's running balances, and what happened to it this month.</summary>
internal sealed class ClassRun
{
    public ClassRun(RevolvingClass config)
    {
        Config = config;
        Outstanding = config.Balance;
        WrittenDown = config.WrittenDown;
        InterestDue = config.InterestShortfall;
    }

    public RevolvingClass Config { get; }
    public double Outstanding { get; set; }
    public double WrittenDown { get; set; }
    public double InterestDue { get; set; }
    public double DefaultDue { get; set; }

    public double BeginInvested { get; set; }
    public double RatePct { get; set; }
    public double? IndexValue { get; set; }
    public double InterestAccrued { get; set; }
    public double InterestPaid { get; set; }
    public double PrincipalPaid { get; set; }
    public double NetWritedown { get; set; }

    public double Invested => Outstanding - WrittenDown;

    public void OpenMonth()
    {
        BeginInvested = Invested;
        InterestAccrued = 0.0;
        InterestPaid = 0.0;
        PrincipalPaid = 0.0;
        NetWritedown = 0.0;
    }

    public void WriteDown(double amount)
    {
        WrittenDown += amount;
        NetWritedown += amount;
    }

    public void Reinstate(double amount)
    {
        WrittenDown -= amount;
        NetWritedown -= amount;
    }

    public ClassRun Copy()
    {
        return (ClassRun)MemberwiseClone();
    }
}

/// <summary>
///     A series' running state and the month's two collection buckets. Verbs move cash between the
///     buckets and everything else, and every verb is safe to run twice in a month because it pays
///     only what is still due.
/// </summary>
internal sealed class SeriesRun
{
    public const double Cent = 0.01;

    private readonly Dictionary<string, ClassRun> _byKey;
    private readonly Dictionary<string, double> _feeDue;

    public SeriesRun(RevolvingSeries config, int actionLag, IReadOnlyList<TriggerTest> tests)
    {
        Config = config;
        Machine = new SeriesPhaseMachine(config.Phase);
        Classes = config.Classes.Select(c => new ClassRun(c)).ToList();
        _byKey = Classes.ToDictionary(c => c.Config.Key);
        _feeDue = config.Fees.ToDictionary(f => f.Name, _ => 0.0);
        Funding = new FundingAccount(config.FundingAccount, config.FundingDeficit);
        Reserve = config.Reserve;
        FrozenPrincipalPct = config.FrozenPrincipalPct;
        FrozenNumerator = config.FrozenNumerator;
        AccumulationMonths = config.AccumulationMonthsRun;
        Latches = tests.Select(t => new TriggerLatch(t, actionLag)).ToList();
        History = config.History.ToDictionary(h => h.Key, h => h.Value.ToList());
    }

    private SeriesRun(SeriesRun other)
    {
        Config = other.Config;
        Machine = other.Machine;
        Classes = other.Classes.Select(c => c.Copy()).ToList();
        _byKey = Classes.ToDictionary(c => c.Config.Key);
        _feeDue = new Dictionary<string, double>(other._feeDue);
        Funding = other.Funding.Copy();
        Reserve = other.Reserve;
        ReserveRequired = other.ReserveRequired;
        ControlledThisMonth = other.ControlledThisMonth;
        Finance = other.Finance;
        Principal = other.Principal;
        Latches = other.Latches;
        History = other.History;
    }

    public RevolvingSeries Config { get; }
    public SeriesPhaseMachine Machine { get; }
    public List<ClassRun> Classes { get; }
    public FundingAccount Funding { get; }
    public double Reserve { get; set; }
    public double ReserveRequired { get; set; }
    public double CarriedFinance { get; set; }
    public double? FrozenPrincipalPct { get; set; }
    public double? FrozenNumerator { get; set; }
    public int AccumulationMonths { get; set; }
    public double ControlledThisMonth { get; set; }
    public List<TriggerLatch> Latches { get; }
    public Dictionary<TriggerMetric, List<double>> History { get; }

    public double Finance { get; set; }
    public double Principal { get; set; }

    /// <summary>The part of <see cref="Finance" /> that is investor collections, before reallocation.</summary>
    public double InvestorFinance { get; set; }

    /// <summary>What group reallocation gave the series. Null if the program does not reallocate.</summary>
    public double? ReallocatedFinance { get; set; }

    /// <summary>Cash a dry run spent, which is what the series still needs.</summary>
    public double Spent { get; set; }

    public double FeesAccrued { get; private set; }
    public double FeesPaid { get; private set; }

    /// <summary>Finance left after the waterfall, wherever it went.</summary>
    public double Surplus { get; set; }

    public double ExcessOut { get; set; }
    public double ExcessIn { get; set; }
    public double SharedPrincipalOut { get; set; }
    public double SharedPrincipalIn { get; set; }
    public double TransferorFinance { get; set; }
    public double TransferorPrincipal { get; set; }
    public double FundingRelease { get; set; }
    public double ReserveDeposit { get; set; }
    public double ReserveRelease { get; set; }

    public SeriesPhase Phase => Machine.Phase;

    public double TotalInvested => Classes.Sum(c => c.Invested);

    /// <summary>The allocator's numerator: invested amounts less what the account already holds.</summary>
    public double AdjustedInvested => Math.Max(0.0, TotalInvested - Funding.Balance);

    /// <summary>
    ///     Nothing is invested and the account is empty, so the series takes no allocation and stops
    ///     accruing. Reporting a rate on dust, or interest on a balance written off, is meaningless.
    /// </summary>
    public bool Exhausted => Classes.All(c => c.Invested < Cent) && Funding.Balance < Cent;

    /// <summary>Exhausted with nothing written off. A write-down is a loss, not a payment.</summary>
    public bool PaidInFull => Exhausted && Classes.All(c => c.WrittenDown < Cent);

    public double FundingRoom => Math.Max(0.0,
        Config.FundedClasses.Sum(key => ClassFor(key).Invested) - Funding.Balance);

    public ClassRun ClassFor(string key)
    {
        return _byKey[key];
    }

    public void AccrueFee(RevolvingFee fee, double basis)
    {
        var amount = basis * fee.RatePct / 100.0 / 12.0;
        _feeDue[fee.Name] += amount;
        FeesAccrued += amount;
    }

    public double FeesDue => _feeDue.Values.Sum();

    /// <summary>A fee the series does not state is 0.</summary>
    public double FeeDue(string name)
    {
        return _feeDue.GetValueOrDefault(name);
    }

    public void PayFee(string name, double amount)
    {
        _feeDue[name] -= amount;
        FeesPaid += amount;
    }

    public void OpenMonth()
    {
        Funding.OpenMonth();
        foreach (var c in Classes)
            c.OpenMonth();

        FeesAccrued = 0.0;
        FeesPaid = 0.0;
        InvestorFinance = 0.0;
        ReallocatedFinance = null;
        Surplus = 0.0;
        ExcessOut = 0.0;
        ExcessIn = 0.0;
        SharedPrincipalOut = 0.0;
        SharedPrincipalIn = 0.0;
        TransferorFinance = 0.0;
        TransferorPrincipal = 0.0;
        FundingRelease = 0.0;
        ReserveDeposit = 0.0;
        ReserveRelease = 0.0;
        Spent = 0.0;
    }

    /// <summary>The series as it stands, to find what it still needs without moving any cash.</summary>
    public SeriesRun DryRun()
    {
        return new SeriesRun(this);
    }
}
