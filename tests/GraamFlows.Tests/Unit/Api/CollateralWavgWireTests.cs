using FluentAssertions;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// graam-harmony#5221 — the Net WAC and Eff WAC columns were blank for every deal.
///
/// <c>PeriodCashflows</c> carries <c>NetWac</c> (computed next to <c>WAC</c> on every period)
/// and <c>EffectiveWac</c> (stamped by the structure during the run), but
/// <c>PeriodCashflowDto</c> declared neither, so <c>CollateralCashflowMapper</c> could not carry
/// them and no consumer ever saw them. A blank column is indistinguishable from a zero, which is
/// why this sat behind a WAC that rendered fine on the very same row.
///
/// The mapper's own contract — "one mapping for every endpoint that returns collateral, so a
/// field added to one cannot silently go missing from another" — is what this pins.
/// </summary>
public class CollateralWavgWireTests
{
    [Fact]
    public void The_wire_carries_every_wavg_the_period_computed()
    {
        var period = new PeriodCashflows
        {
            CashflowDate = new DateTime(2026, 2, 25),
            GroupNum = "1",
            BeginBalance = 100_000_000,
            Balance = 99_000_000,
            Interest = 500_000,
            NetInterest = 480_000,
            ServiceFee = 20_000,
            WAC = 6.0,
            NetWac = 5.76,
            EffectiveWac = 5.5,
            WAM = 340,
            WALA = 6
        };

        var dto = CollateralCashflowMapper.ToDtos(new[] { period }).Single();

        // The two that were dropped...
        dto.NetWac.Should().Be(5.76, "net WAC is computed on every period and has to reach the wire");
        dto.EffectiveWac.Should().Be(5.5, "the WAC the waterfall distributed on has to reach the wire");
        // ...next to the ones that always arrived, so a mapper that stopped copying anything fails here.
        dto.Wac.Should().Be(6.0);
        dto.Wam.Should().Be(340);
        dto.Wala.Should().Be(6);
    }
}
