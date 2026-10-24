using FluentAssertions;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
///     CPY: a CPR each asset pays only from the month its call protection ends.
///
///     Before the convention existed, a caller ran it by sending each loan a per-asset CPR vector
///     that is 0 until the loan's open month and the speed after. The load-bearing test here is that
///     the convention reproduces exactly those cashflows from the asset's own dates, so a caller can
///     drop the vectors without moving a number.
/// </summary>
public class CpyPrepaymentTests
{
    private static readonly DateTime ProjectionDate = new(2024, 6, 1);
    private const double Speed = 25.0;

    private static Asset Loan(string id, DateTime? lockout = null, DateTime? defeasance = null,
        DateTime? yieldMaintenance = null, DateTime? premium = null) => new()
    {
        AssetName = id,
        AssetId = id,
        InterestRateType = InterestRateType.FRM,
        OriginalDate = new DateTime(2024, 1, 1),
        OriginalBalance = 10_000_000,
        CurrentBalance = 10_000_000,
        BalanceAtIssuance = 10_000_000,
        OriginalInterestRate = 5.0,
        CurrentInterestRate = 5.0,
        OriginalAmortizationTerm = 120,
        BalloonAmortizationTerm = 360,
        ServiceFee = 0.0,
        GroupNum = "1",
        LockoutEndDate = lockout,
        DefeasanceEndDate = defeasance,
        YieldMaintenanceEndDate = yieldMaintenance,
        PrepaymentPremiumEndDate = premium,
    };

    private static IAssetAssumptions Assumps(PrepaymentTypeEnum type, IAnchorableVector speed) =>
        new AssetAssumptions(type, speed, DefaultTypeEnum.CDR,
            new ConstVector(DateUtil.CalcAbsT(ProjectionDate), 0.0),
            new ConstVector(DateUtil.CalcAbsT(ProjectionDate), 0.0));

    private static List<PeriodCashflows> Run(IList<IAsset> assets, Func<IAsset, IAssetAssumptions> assumps) =>
        CfCore.GenerateAssetCashflows(assets, ProjectionDate, null, assumps, null!)
            .PeriodCashflows.OrderBy(cf => cf.CashflowDate).ToList();

    private static List<PeriodCashflows> RunAll(PrepaymentTypeEnum type, params IAsset[] assets)
    {
        var aa = Assumps(type, new ConstVector(DateUtil.CalcAbsT(ProjectionDate), Speed));
        return Run(assets, _ => aa);
    }

    /// <summary>The per-asset vector a caller built before CPY: 0 before the open month, the speed after.</summary>
    private static ArrayVector HeldVector(DateTime open)
    {
        var anchor = DateUtil.CalcAbsT(ProjectionDate);
        var k0 = DateUtil.CalcAbsT(open) - anchor;
        return new ArrayVector(anchor, Enumerable.Range(0, 720).Select(k => k < k0 ? 0.0 : Speed).ToArray());
    }

    [Fact]
    public void Cpy_Reproduces_The_Per_Asset_Held_Vectors_Exactly()
    {
        // Three loans, three shapes of call protection: lockout then defeasance; a hard lockout then
        // yield maintenance; and a loan already open at the projection date.
        var a = Loan("A", lockout: new DateTime(2025, 1, 10), defeasance: new DateTime(2033, 9, 10));
        var b = Loan("B", lockout: new DateTime(2026, 1, 10), yieldMaintenance: new DateTime(2033, 6, 10),
            premium: new DateTime(2033, 6, 10));
        var c = Loan("C", lockout: new DateTime(2024, 3, 10));

        var cpy = RunAll(PrepaymentTypeEnum.CPY, a, b, c);

        var vectors = new Dictionary<string, IAssetAssumptions>
        {
            ["A"] = Assumps(PrepaymentTypeEnum.CPR, HeldVector(new DateTime(2033, 9, 10))),
            ["B"] = Assumps(PrepaymentTypeEnum.CPR, HeldVector(new DateTime(2033, 6, 10))),
            ["C"] = Assumps(PrepaymentTypeEnum.CPR, new ConstVector(DateUtil.CalcAbsT(ProjectionDate), Speed)),
        };
        var held = Run(new List<IAsset> { Loan("A"), Loan("B"), Loan("C") }, x => vectors[x.AssetId]);

        cpy.Should().HaveCount(held.Count);
        for (var t = 0; t < held.Count; t++)
        {
            cpy[t].UnscheduledPrincipal.Should().Be(held[t].UnscheduledPrincipal, $"period {t}");
            cpy[t].ScheduledPrincipal.Should().Be(held[t].ScheduledPrincipal, $"period {t}");
            cpy[t].Interest.Should().Be(held[t].Interest, $"period {t}");
            cpy[t].Balance.Should().Be(held[t].Balance, $"period {t}");
        }
    }

    [Fact]
    public void A_Loan_Prepays_Nothing_Before_Its_Open_Month_And_At_Speed_From_It()
    {
        var open = new DateTime(2027, 3, 10);
        var flows = RunAll(PrepaymentTypeEnum.CPY, Loan("A", lockout: new DateTime(2025, 1, 10), defeasance: open));

        var openRow = DateUtil.CalcAbsT(open) - DateUtil.CalcAbsT(ProjectionDate);
        flows.Take(openRow).Should().OnlyContain(cf => cf.UnscheduledPrincipal == 0.0);
        flows[openRow].UnscheduledPrincipal.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void The_Latest_End_Date_Opens_The_Loan_Whichever_Field_States_It()
    {
        var late = new DateTime(2030, 7, 10);
        var early = new DateTime(2025, 1, 10);
        var expected = RunAll(PrepaymentTypeEnum.CPY, Loan("A", lockout: late));

        RunAll(PrepaymentTypeEnum.CPY, Loan("A", lockout: early, defeasance: late))
            .Select(cf => cf.UnscheduledPrincipal).Should().Equal(expected.Select(cf => cf.UnscheduledPrincipal));
        RunAll(PrepaymentTypeEnum.CPY, Loan("A", lockout: early, yieldMaintenance: late))
            .Select(cf => cf.UnscheduledPrincipal).Should().Equal(expected.Select(cf => cf.UnscheduledPrincipal));
        RunAll(PrepaymentTypeEnum.CPY, Loan("A", lockout: early, premium: late))
            .Select(cf => cf.UnscheduledPrincipal).Should().Equal(expected.Select(cf => cf.UnscheduledPrincipal));
    }

    [Fact]
    public void A_Loan_With_No_Dates_Prepays_From_The_First_Period_Under_Cpy()
    {
        RunAll(PrepaymentTypeEnum.CPY, Loan("A")).Select(cf => cf.UnscheduledPrincipal)
            .Should().Equal(RunAll(PrepaymentTypeEnum.CPR, Loan("A")).Select(cf => cf.UnscheduledPrincipal));
    }

    [Fact]
    public void Cpr_Ignores_Call_Protection_Dates()
    {
        var dated = Loan("A", lockout: new DateTime(2025, 1, 10), defeasance: new DateTime(2033, 9, 10));
        RunAll(PrepaymentTypeEnum.CPR, dated).Select(cf => cf.UnscheduledPrincipal)
            .Should().Equal(RunAll(PrepaymentTypeEnum.CPR, Loan("A")).Select(cf => cf.UnscheduledPrincipal));
    }

    [Fact]
    public void A_Per_Asset_Speed_Override_Is_Held_Too()
    {
        // The convention is the run's; a per-asset speed is that asset's speed under it.
        var open = new DateTime(2028, 1, 10);
        var anchor = DateUtil.CalcAbsT(ProjectionDate);
        var fast = Assumps(PrepaymentTypeEnum.CPY, new ConstVector(anchor, 60.0));
        var flows = Run(new List<IAsset> { Loan("A", lockout: open) }, _ => fast);

        var openRow = DateUtil.CalcAbsT(open) - anchor;
        flows.Take(openRow).Should().OnlyContain(cf => cf.UnscheduledPrincipal == 0.0);
        flows[openRow].UnscheduledPrincipal.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void OpenDate_Is_The_Latest_Stated_End_Or_Null()
    {
        CallProtection.OpenDate(Loan("A")).Should().BeNull();
        CallProtection.OpenDate(Loan("A", lockout: new DateTime(2025, 1, 10), yieldMaintenance: new DateTime(2031, 2, 10),
            premium: new DateTime(2029, 1, 10))).Should().Be(new DateTime(2031, 2, 10));
    }
}
