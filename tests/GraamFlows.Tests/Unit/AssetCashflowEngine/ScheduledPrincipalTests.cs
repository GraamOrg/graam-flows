using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// An asset that pays a stated amount of principal every month (<see cref="IAsset.ScheduledPrincipal" />):
/// a commercial mortgage that amortizes by a fixed dollar figure, interest on its balance, the rest at
/// maturity. Its trust's loan-level filings show the same principal every month, before and after a
/// curtailment; a level payment's principal grows each month instead. Synthetic numbers.
/// </summary>
public class ScheduledPrincipalTests
{
    private const double RatePct = 4.0;
    private const double Principal = 25_000.0;
    private const double Balance = 60_000_000.0;
    private const int Term = 120;
    private static readonly DateTime FirstProjDate = new(2026, 2, 1);

    private static Asset Loan(double? scheduledPrincipal = Principal, double debtService = 0.0) => new()
    {
        AssetName = "L4",
        AssetId = "L4",
        InterestRateType = InterestRateType.FRM,
        OriginalDate = FirstProjDate,
        OriginalBalance = Balance,
        CurrentBalance = Balance,
        BalanceAtIssuance = Balance,
        OriginalInterestRate = RatePct,
        CurrentInterestRate = RatePct,
        OriginalAmortizationTerm = Term,
        DebtService = debtService,
        GroupNum = "1",
        ScheduledPrincipal = scheduledPrincipal
    };

    private static List<PeriodCashflows> Run(IAsset asset, double cpr = 0.0)
    {
        var anchorAbsT = DateUtil.CalcAbsT(FirstProjDate);
        IAssetAssumptions Assump(IAsset _) => new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, cpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 0.0));
        var result = CfCore.GenerateAssetCashflows(new List<IAsset> { asset }, FirstProjDate, null, Assump, null!);
        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    [Fact]
    public void The_Loan_Pays_Its_Stated_Principal_Every_Month_And_Balloons_At_Maturity()
    {
        var periods = Run(Loan());

        periods.Should().HaveCount(Term);
        foreach (var p in periods.Take(Term - 1))
        {
            p.ScheduledPrincipal.Should().BeApproximately(Principal, 1e-6);
            p.Interest.Should().BeApproximately(RatePct / 1200.0 * p.BeginBalance, 1e-6);
        }

        var balloon = Balance - Principal * (Term - 1);
        periods[^1].ScheduledPrincipal.Should().BeApproximately(balloon, 1e-4);
        periods[^1].Balance.Should().BeApproximately(0.0, 1e-4);
    }

    [Fact]
    public void A_Stated_Principal_Wins_Over_A_Debt_Service_Sent_Beside_It()
    {
        // A caller sends a level payment too, for an engine that predates the field.
        var level = Run(Loan(scheduledPrincipal: null, debtService: 225_000.0));
        var both = Run(Loan(debtService: 225_000.0));

        level[0].ScheduledPrincipal.Should().BeApproximately(225_000.0 - RatePct / 1200.0 * Balance, 1e-6);
        level[12].ScheduledPrincipal.Should().BeGreaterThan(level[0].ScheduledPrincipal);
        both.Take(Term - 1).Should().OnlyContain(p => Math.Abs(p.ScheduledPrincipal - Principal) < 1e-6);
    }

    [Fact]
    public void A_Prepayment_Does_Not_Lower_The_Stated_Principal()
    {
        var periods = Run(Loan(), cpr: 6.0);

        // The balance falls faster than the schedule; the principal it pays is the same dollar figure.
        periods[0].UnscheduledPrincipal.Should().BeGreaterThan(0.0);
        periods.Take(60).Should().OnlyContain(p => Math.Abs(p.ScheduledPrincipal - Principal) < 1e-6);
        periods[59].BeginBalance.Should().BeLessThan(Balance - 59 * Principal);
    }

    [Fact]
    public void A_Stated_Principal_With_Scheduled_Payments_Is_Refused()
    {
        var loan = Loan();
        loan.ScheduledPayments = Enumerable.Repeat(250_000.0, Term).ToArray();

        var act = () => Run(loan);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*stated principal amount cannot be combined with scheduled payments*");
    }

    [Fact]
    public void The_Response_Names_The_Assets_That_Paid_A_Stated_Principal()
    {
        AssetDto Dto(string id, double? principal) => new()
        {
            AssetName = id,
            AssetId = id,
            InterestRateType = "FRM",
            OriginalDate = FirstProjDate,
            OriginalBalance = Balance,
            CurrentBalance = Balance,
            OriginalInterestRate = RatePct,
            CurrentInterestRate = RatePct,
            OriginalAmortizationTerm = Term,
            GroupNum = "1",
            ScheduledPrincipal = principal
        };
        var result = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(
            new CalcCollateralRequest
            {
                Assets = new List<AssetDto> { Dto("L4", Principal), Dto("L5", null) },
                ProjectionDate = FirstProjDate,
                Assumptions = new AssumptionsDto { Cpr = 0.0, Cdr = 0.0 }
            }).Result!;

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;
        response.ScheduledPrincipalAssetIds.Should().Equal("L4");
    }
}
