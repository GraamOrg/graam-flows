using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The yield solver searches -100% to 1,000%. When no yield in that range hits the price,
///     it used to return -100 as if it were the answer: a residual on a 1% OC sliver, paying
///     about 37x its face, priced at 100 came back "yielding -100%". A yield the solver did not
///     find is now a refusal that says which side of the range it lies on.
/// </summary>
public class UnsolvedYieldIsRefusedTests
{
    private static readonly DateTime Settle = new(2026, 10, 29);

    private static ActionResult<PricingResponse> PriceAt(double price, double balance,
        IEnumerable<(int Month, double Interest, double Principal)> flows)
    {
        var remaining = balance;
        var cashflows = flows.Select(f =>
        {
            remaining -= f.Principal;
            return new CashflowEntryDto
            {
                Date = Settle.AddMonths(f.Month),
                Interest = f.Interest,
                Principal = f.Principal,
                Balance = remaining
            };
        }).ToList();
        var request = new PricingRequest
        {
            Cashflows = cashflows,
            Params = new PricingParamsDto
            {
                InputType = "price",
                InputValue = price,
                SettleDate = Settle,
                Balance = balance,
                DayCount = "30/360",
                Compounding = "SemiAnnual"
            }
        };
        return new PricingController(NullLogger<PricingController>.Instance).Stats(request);
    }

    private static string RefusalOf(ActionResult<PricingResponse> result)
        => result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;

    [Fact]
    public void AResidualWorthFarMoreThanItsFaceIsRefusedAsAboveTheRange()
    {
        // 4,000,000 of face collecting 2,000,000 a month for ten years, face back at the end.
        var flows = Enumerable.Range(1, 120)
            .Select(m => (m, 2_000_000.0, m == 120 ? 4_000_000.0 : 0.0));

        var refusal = RefusalOf(PriceAt(100, 4_000_000, flows));

        refusal.Should().Contain("yields more than 1,000%");
        refusal.Should().Contain("per 100 of the 4,000,000 balance");
    }

    [Fact]
    public void AStreamWorthAlmostNothingIsRefusedAsBelowTheRange()
    {
        // 1,000,000 of face that returns 1 dollar in three years.
        var refusal = RefusalOf(PriceAt(100, 1_000_000, new[] { (36, 0.0, 1.0) }));

        refusal.Should().Contain("yields less than -100%");
    }

    [Fact]
    public void AYieldInsideTheRangeIsStillSolved()
    {
        // A 6% monthly-pay bullet at par: 6.0755% quoted semiannual.
        var flows = Enumerable.Range(1, 60)
            .Select(m => (m, 1_000_000 * 0.06 / 12, m == 60 ? 1_000_000.0 : 0.0));

        var ok = PriceAt(100, 1_000_000, flows).Result.Should().BeOfType<OkObjectResult>().Subject;

        ok.Value.Should().BeOfType<PricingResponse>().Subject.Yield.Should().BeApproximately(6.0755, 0.001);
    }
}
