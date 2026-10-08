using FluentAssertions;
using GraamFlows;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.Tests.Fixtures;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// A floating reinvestment template's floors (<see cref="ReinvestTemplate.IndexFloor" />,
/// <see cref="ReinvestTemplate.LifeFloor" />).
///
/// The template had no floor field, so a stated floor was carried by the caller and dropped. With
/// the index below a loan's floor that understates the bought collateral's coupon by the whole gap:
/// on a reference tie-out (a 4.30% index floor against a ~4.20% index) the unapplied floor was
/// 40-60% of the remaining collateral-interest difference. Bought collateral is fixed at its
/// purchase-date coupon (the documented v1 approximation), so the floors bind there:
/// <c>max(max(index, IndexFloor) + margin, LifeFloor)</c>. No floor stated is byte-identical.
/// </summary>
public class ReinvestTemplateFloorTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Start = 1_000_000.0;
    private const double Amort = 10_000.0;
    private const double Margin = 3.30;

    private static List<PeriodCashflows> BasePool()
    {
        var pool = new List<PeriodCashflows>();
        for (var p = 0; p < 36; p++)
        {
            var begin = Start - Amort * p;
            pool.Add(new PeriodCashflows
            {
                CashflowDate = Proj.AddMonths(p), GroupNum = "1", BeginBalance = begin,
                Balance = begin - Amort, ScheduledPrincipal = Amort, Interest = begin * 0.05 / 12.0
            });
        }

        return pool;
    }

    private static IAssetAssumptions ZeroAssumps()
    {
        var anchor = DateUtil.CalcAbsT(Proj);
        return new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchor, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0), new ConstVector(anchor, 0.0));
    }

    /// <summary>Interest the bought collateral earns over the run, per dollar of face bought.</summary>
    private static double CouponOfBoughtCollateral(double index, double? indexFloor, double? lifeFloor)
    {
        var cfg = new ReinvestmentConfig
        {
            ReinvestStartDate = Proj,
            ReinvestEndDate = Proj.AddMonths(12),
            ReinvestAllEligibleProceeds = true,
            Templates = new[]
            {
                new ReinvestTemplate
                {
                    AllocationPct = 100, Price = 100.0, AmortizationType = AmortizationType.Bullet,
                    IndexName = MarketDataInstEnum.Sofr30Avg, IndexMargin = Margin, TermMonths = 60,
                    IndexFloor = indexFloor, LifeFloor = lifeFloor
                }
            }
        };
        var r = CfCore.BuildReinvestment(BasePool(), cfg, Proj, ZeroAssumps(), new ConstantTestRateProvider(index));
        // Every cohort is a bullet held to the horizon at one fixed coupon, so interest / balance
        // over the periods it is outstanding is that coupon, annualized.
        var held = r.Cashflows.Where(c => c.BeginBalance > 1).ToList();
        return held.Sum(c => c.Interest) / held.Sum(c => c.BeginBalance) * 1200.0;
    }

    [Fact]
    public void An_index_below_its_floor_accrues_at_the_floor()
    {
        CouponOfBoughtCollateral(4.203, 4.30, null).Should().BeApproximately(4.30 + Margin, 1e-6);
    }

    [Fact]
    public void An_index_above_its_floor_is_unchanged()
    {
        CouponOfBoughtCollateral(5.00, 4.30, null).Should().BeApproximately(5.00 + Margin, 1e-6);
    }

    [Fact]
    public void No_floor_stated_is_the_index_plus_margin()
    {
        CouponOfBoughtCollateral(4.203, null, null).Should().BeApproximately(4.203 + Margin, 1e-6);
    }

    [Fact]
    public void A_life_floor_floors_the_all_in_coupon_and_the_binding_floor_wins()
    {
        CouponOfBoughtCollateral(4.203, null, 8.0).Should().BeApproximately(8.0, 1e-6);
        CouponOfBoughtCollateral(4.203, 4.30, 7.0).Should().BeApproximately(4.30 + Margin, 1e-6);
    }

    [Fact]
    public void The_wire_carries_both_floors()
    {
        var dto = new ReinvestmentDto
        {
            ReinvestEndDate = Proj.AddMonths(12),
            ReinvestAllEligibleProceeds = true,
            Templates = new List<ReinvestTemplateDto>
            {
                new() { AllocationPct = 100, TermMonths = 60, IndexFloor = 4.3, LifeFloor = 6.0 },
                new() { AllocationPct = 0, TermMonths = 60 }
            }
        };
        var t = ReinvestmentConfigMapper.Map(dto, "D")!.Templates;
        t[0].IndexFloor.Should().Be(4.3);
        t[0].LifeFloor.Should().Be(6.0);
        t[1].IndexFloor.Should().BeNull();
        t[1].LifeFloor.Should().BeNull();
    }
}
