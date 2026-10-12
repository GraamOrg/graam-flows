using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A CLO's Principal Priority of Payments opens (clause (A)) by paying what the interest waterfall
/// left unpaid of its senior items — taxes, Administrative Expenses up to the cap, the Senior
/// Collateral Management Fee — out of principal proceeds, ahead of the notes' principal. Measured
/// against a reference model on a deal's last Payment Date, when interest ran out: the reference paid
/// both in full; paid only from interest, half the one and none of the other was paid and the
/// residual kept the difference.
/// </summary>
public class ExpensesFromPrincipalTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    /// <summary>A pool with little interest and steady principal: 1M a month repaid.</summary>
    private static CollateralCashflows Pool(double interest)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 1_000_000,
                unscheduledPrincipal: 0, interest: interest);
            bal -= 1_000_000;
        }

        return b.Build();
    }

    private static TestDealBuilder Deal(params string[] fromPrincipal) =>
        new TestDealBuilder()
            .WithTranche("A", 95_000_000, 0.0, subOrder: 0)
            .WithTranche("R", 5_000_000, 0.0, subOrder: 1, cashflowType: "PI", couponType: "Residual")
            .WithExpenseTranche("Admin", 0, formula: "100000")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SINGLE('A'))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('R')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('R')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('R')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('R'), SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))")
            .WithExpensesFromPrincipal(fromPrincipal);

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    [Fact]
    public void An_expense_interest_cannot_cover_is_topped_up_from_principal_ahead_of_the_notes()
    {
        var cf = Deal("Admin").BuildAndRun(Pool(interest: 40_000)).Cashflows;
        var admin = Rows(cf, "Admin");
        admin.Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.Expense - 100_000) < 0.01 && Math.Abs(r.ExpenseShortfall) < 0.01,
            "40k from interest and 60k from principal");
        Rows(cf, "A").Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.ScheduledPrincipal - 940_000) < 0.01,
            "the notes' principal is what the expense left");
    }

    [Fact]
    public void Without_it_the_shortfall_stays_unpaid_and_principal_untouched()
    {
        var cf = Deal().BuildAndRun(Pool(interest: 40_000)).Cashflows;
        Rows(cf, "Admin").Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.Expense - 40_000) < 0.01);
        Rows(cf, "A").Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.ScheduledPrincipal - 1_000_000) < 0.01);
    }

    [Fact]
    public void Principal_tops_up_only_what_interest_left_unpaid()
    {
        var cf = Deal("Admin").BuildAndRun(Pool(interest: 250_000)).Cashflows;
        Rows(cf, "Admin").Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.Expense - 100_000) < 0.01);
        Rows(cf, "A").Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.ScheduledPrincipal - 1_000_000) < 0.01,
            "interest covered it: no principal drawn");
    }

    [Fact]
    public void Names_are_validated_and_none_may_also_be_paid_after_interest()
    {
        var defined = new[] { new ExpenseDto { ExpenseName = "Admin", Formula = "1" }, new ExpenseDto { ExpenseName = "Sub", Formula = "1" } };
        ExpensesAfterInterestMapper.MapFromPrincipal(new List<string> { "Admin" }, defined, new[] { "Sub" }, "d")
            .Should().Equal("Admin");
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapFromPrincipal(new List<string> { "Nope" }, defined, Array.Empty<string>(), "d"))
            .Should().Throw<ArgumentException>().WithMessage("*expensesFromPrincipal names Nope*");
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapFromPrincipal(new List<string> { "Sub" }, defined, new[] { "Sub" }, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*Sub is both paid after interest*");
    }
}
