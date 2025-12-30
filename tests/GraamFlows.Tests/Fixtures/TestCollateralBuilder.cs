using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Helpers;

namespace GraamFlows.Tests.Fixtures;

/// <summary>
/// Fluent builder for creating CollateralCashflows test data.
/// </summary>
public class TestCollateralBuilder
{
    private readonly List<PeriodCashflows> _periods = new();
    private string _groupNum = "0";

    public TestCollateralBuilder WithGroupNum(string groupNum)
    {
        _groupNum = groupNum;
        return this;
    }

    /// <summary>
    /// Add a single period cashflow with specified values.
    /// </summary>
    public TestCollateralBuilder WithPeriod(
        DateTime date,
        double beginBalance,
        double scheduledPrincipal,
        double unscheduledPrincipal,
        double interest,
        double defaultedPrincipal = 0,
        double recoveryPrincipal = 0,
        double serviceFee = 0)
    {
        var period = new PeriodCashflows
        {
            CashflowDate = date,
            GroupNum = _groupNum,
            BeginBalance = beginBalance,
            Balance = beginBalance - scheduledPrincipal - unscheduledPrincipal - defaultedPrincipal,
            ScheduledPrincipal = scheduledPrincipal,
            UnscheduledPrincipal = unscheduledPrincipal,
            Interest = interest,
            NetInterest = interest - serviceFee,
            ServiceFee = serviceFee,
            DefaultedPrincipal = defaultedPrincipal,
            RecoveryPrincipal = recoveryPrincipal,
            DelinqBalance = 0,
            WAC = interest / beginBalance * 1200,
            NetWac = (interest - serviceFee) / beginBalance * 1200
        };

        _periods.Add(period);
        return this;
    }

    /// <summary>
    /// Generate constant cashflows for multiple periods (simplified amortization).
    /// </summary>
    public TestCollateralBuilder WithConstantCashflows(
        DateTime startDate,
        int numPeriods,
        double startingBalance,
        double cpr = TestConstants.DefaultCpr,
        double cdr = TestConstants.DefaultCdr,
        double wac = TestConstants.DefaultWac,
        double serviceFee = 0.25)
    {
        var balance = startingBalance;
        var smm = 1 - Math.Pow(1 - cpr / 100, 1.0 / 12);
        var mdr = cdr / 100 / 12;

        for (var i = 0; i < numPeriods; i++)
        {
            var date = startDate.AddMonths(i);
            var interest = balance * wac / 100 / 12;
            var scheduled = balance * 0.01; // ~1% scheduled principal
            var unscheduled = balance * smm;
            var defaults = balance * mdr;
            var recovery = defaults * 0.6; // 60% recovery
            var fee = balance * serviceFee / 100 / 12;

            WithPeriod(
                date: date,
                beginBalance: balance,
                scheduledPrincipal: scheduled,
                unscheduledPrincipal: unscheduled,
                interest: interest,
                defaultedPrincipal: defaults,
                recoveryPrincipal: recovery,
                serviceFee: fee);

            balance -= scheduled + unscheduled + defaults;
            if (balance < 1000) break;
        }

        return this;
    }

    /// <summary>
    /// Create a simple single-period cashflow for basic tests.
    /// </summary>
    public TestCollateralBuilder WithSinglePeriod(
        double balance = TestConstants.DefaultTrancheBalance,
        double wac = TestConstants.DefaultWac)
    {
        var interest = balance * wac / 100 / 12;
        var scheduled = balance * 0.01;
        var unscheduled = balance * 0.005;

        return WithPeriod(
            date: TestConstants.DefaultFirstPayDate,
            beginBalance: balance,
            scheduledPrincipal: scheduled,
            unscheduledPrincipal: unscheduled,
            interest: interest);
    }

    public CollateralCashflows Build()
    {
        return new CollateralCashflows(_periods);
    }
}
