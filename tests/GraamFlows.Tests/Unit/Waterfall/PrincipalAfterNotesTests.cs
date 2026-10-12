using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// After the Reinvestment Period a CLO's Principal Priority of Payments pays a Subordinated Collateral
/// Management Fee the interest proceeds left unpaid — after the notes, before the residual. Measured
/// against a reference model on a deal's last Payment Date, when interest ran out: it paid the fee in
/// full from principal; ours carried it unpaid and the residual kept it.
/// </summary>
public class PrincipalAfterNotesTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    /// <summary>No interest; 2M of principal a month against a 5M note: the note is repaid in month 3.</summary>
    private static CollateralCashflows Pool()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 12_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 2_000_000,
                unscheduledPrincipal: 0, interest: 0);
            bal -= 2_000_000;
        }

        return b.Build();
    }

    private static TestDealBuilder Deal(PrincipalAfterNotesConfig? afterNotes) =>
        new TestDealBuilder()
            .WithTranche("A", 5_000_000, 0.0, subOrder: 0)
            .WithTranche("R", 7_000_000, 0.0, subOrder: 1, cashflowType: "PI", couponType: "Residual")
            .WithExpenseTranche("SubFee", 0, formula: "10000")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SINGLE('A'))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('R')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('R')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('R')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('R'), SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))")
            .WithExpensesAfterInterest("SubFee")
            .Pipe(b => afterNotes == null ? b : b.WithPrincipalAfterNotes(afterNotes));

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static PrincipalAfterNotesConfig Cfg(DateTime? from = null) =>
        new() { Expenses = new[] { "SubFee" }, Notes = new[] { "A" }, FromDate = from };

    [Fact]
    public void A_carried_fee_is_paid_from_principal_once_the_notes_need_none_of_it()
    {
        var plain = Deal(null).BuildAndRun(Pool()).Cashflows;
        var cf = Deal(Cfg()).BuildAndRun(Pool()).Cashflows;
        var fee = Rows(cf, "SubFee");
        // Months 0-1: all principal goes to the 5M note (2M + 2M < 5M) — nothing for the fee.
        fee[0].Expense.Should().Be(0);
        fee[1].Expense.Should().Be(0);
        // Month 2: the note needs 1M of the 2M; the fee's carried 30k is paid from the surplus.
        fee[2].Expense.Should().BeApproximately(30_000, 0.01);
        fee[3].Expense.Should().BeApproximately(10_000, 0.01);
        Rows(cf, "A").Sum(r => r.ScheduledPrincipal).Should().BeApproximately(5_000_000, 0.01, "the note is repaid in full first");
        (Rows(plain, "R").Sum(r => r.ScheduledPrincipal) - Rows(cf, "R").Sum(r => r.ScheduledPrincipal))
            .Should().BeApproximately(fee.Sum(r => r.Expense), 0.01, "out of what the residual would have had");
    }

    [Fact]
    public void Not_before_its_start_date()
    {
        var cf = Deal(Cfg(from: FirstPay.AddMonths(4))).BuildAndRun(Pool()).Cashflows;
        var fee = Rows(cf, "SubFee");
        fee.Take(4).Should().OnlyContain(r => r.Expense == 0);
        fee[4].Expense.Should().BeApproximately(50_000, 0.01, "everything carried until then");
    }

    [Fact]
    public void It_pays_only_after_interest_expenses_and_names_its_notes()
    {
        var after = new[] { "SubFee" };
        var tranches = new[] { "A", "R" };
        ExpensesAfterInterestMapper.MapPrincipalAfterNotes(null, after, tranches, "d").Should().BeNull();
        ExpensesAfterInterestMapper.MapPrincipalAfterNotes(
            new PrincipalAfterNotesDto { Expenses = { "SubFee" }, Notes = { "A" } }, after, tranches, "d")!.Notes.Should().Equal("A");
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapPrincipalAfterNotes(
                new PrincipalAfterNotesDto { Expenses = { "Admin" }, Notes = { "A" } }, after, tranches, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*Admin, which is not paid after interest*");
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapPrincipalAfterNotes(
                new PrincipalAfterNotesDto { Expenses = { "SubFee" }, Notes = { "Z" } }, after, tranches, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*Z is not a tranche*");
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapPrincipalAfterNotes(
                new PrincipalAfterNotesDto { Expenses = { "SubFee" } }, after, tranches, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*must name the notes*");
    }
}
