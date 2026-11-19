using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>An owner trust as a caller meets it on /api/revolving: JSON text, through the whole app.</summary>
public class OwnerTrustHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public OwnerTrustHttpTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>A revolving owner trust, one replayed month and two forecast ones, in snake_case.</summary>
    private static JsonObject TrustBody() => JsonNode.Parse("""
        {
          "program": {
            "name": "An owner trust",
            "owner_trust": {
              "discount_rate": 6.0,
              "tiers": [
                { "name": "A", "classes": ["A-1a", "A-1b"] },
                { "name": "B", "classes": ["B"] }
              ],
              "steps": [
                { "verb": "PAY_FEES" }, { "verb": "PAY_SERVICING" },
                { "verb": "PAY_INTEREST", "tier": "A" }, { "verb": "PRIORITY_PRINCIPAL", "tier": "A" },
                { "verb": "PAY_INTEREST", "tier": "B" }, { "verb": "PRIORITY_PRINCIPAL", "tier": "B" },
                { "verb": "REGULAR_PRINCIPAL" }, { "verb": "ACCELERATED_PRINCIPAL" },
                { "verb": "FUND_RESERVE" }, { "verb": "DEPOSIT_ACQUISITION" }, { "verb": "FUND_NEGATIVE_CARRY" },
                { "verb": "PAY_CERTIFICATE" }
              ],
              "revolving_end": "2026-10-01",
              "servicing_rate": 0.75,
              "monthly_fees": 2000.0,
              "oc": { "revolving_pct": 10.5, "revolving_failing_pct": 13.5, "amortizing_pct": 14.5,
                      "amortizing_failing_pct": 17.5, "floor": 10000.0 },
              "required_reserve": 12000.0,
              "tests": { "write_off_limit": 10.0, "delinquency_limit": 2.0, "parity_limit": 50.0, "oc_shortfall": true },
              "clean_up": { "pool_factor": 10.0, "closing_pool_balance": 2000000.0 },
              "acquisition": { "price": { "pv_rate": 6.0 }, "terms": [{ "term": 20, "weight": 100.0, "age": 4 }] },
              "calendar": "FEDERAL_RESERVE",
              "state": { "phase": "REVOLVING", "reserve": 12000.0 },
              "actual_periods": [{ "fees": 2100.0, "floor_tests_pass": true, "write_off_ratio": 1.2,
                                   "delinquency_ratio": 0.4, "delinquency": 0.4 }]
            }
          },
          "statement_date": "2026-07-31",
          "distribution_day": 20,
          "periods": 3,
          "pool": {
            "model": "INSTALLMENT",
            "discount_rates": [6.0],
            "cohorts": [{ "name": "opening", "age": 6, "schedule": [50000.0, 50000.0, 50000.0, 50000.0, 50000.0,
              50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0,
              50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0] }],
            "actual_periods": [{ "scheduled_collections": 50000.0, "prepaid_collections": 10000.0, "write_offs": 1000.0,
              "additions": [{ "face": 60000.0, "term": 20, "cash": 56000.0 }] }],
            "drivers": { "prepayment_rate": { "annual": [10.0] }, "write_off_rate": 0.1 }
          },
          "series": [{
            "name": "notes",
            "classes": [
              { "key": "A-1a", "balance": 750000.0, "coupon": 1.2 },
              { "key": "A-1b", "balance": 150000.0, "index": "Libor1M", "margin": 0.5, "day_count": "Actual/360" },
              { "key": "B", "balance": 100000.0, "coupon": 3.0 }
            ]
          }],
          "market_rates": { "Libor1M": [[0, 1.0], [1, 1.25], [2, 1.5]] }
        }
        """)!.AsObject();

    private async Task<(HttpStatusCode Status, string Text)> Post(JsonObject body)
    {
        var response = await _client.PostAsync("/api/revolving",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnOwnerTrustRunsOnTheRevolvingRoute()
    {
        var (status, text) = await Post(TrustBody());

        status.Should().Be(HttpStatusCode.OK, text);
        var response = JsonNode.Parse(text)!.AsObject();
        var periods = response["ownerTrust"]!["periods"]!.AsArray();
        periods.Count.Should().Be(3);

        var first = periods[0]!;
        first["replayed"]!.GetValue<bool>().Should().BeTrue();
        first["phase"]!.GetValue<string>().Should().Be("REVOLVING");
        first["fees"]!.GetValue<double>().Should().Be(2100.0);
        // The account pays for the printed additions up to what the waterfall deposited in it.
        var deposit = first["acquisitionDeposit"]!.GetValue<double>();
        deposit.Should().BeGreaterThan(0.0);
        first["acquisitionCash"]!.GetValue<double>().Should().BeApproximately(Math.Min(56000.0, deposit), 1e-6);
        first["pool"]!["additionsFace"]!.GetValue<double>().Should().Be(60000.0);
        first["tests"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should()
            .Contain(new[] { "WRITE_OFF_RATIO", "DELINQUENCY_RATIO", "ADJUSTED_POOL_TO_NOTES", "OVERCOLLATERALIZATION" });
        first["tests"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "WRITE_OFF_RATIO")!["value"]!
            .GetValue<double>().Should().BeApproximately(1.2, 1e-12);

        // The floating class reads the curve's first month on its actual days (2026-08-20 to 2026-09-21).
        var a1b = first["classes"]!.AsArray().First(c => c!["key"]!.GetValue<string>() == "A-1b")!;
        a1b["coupon"]!.GetValue<double>().Should().Be(1.5);
        a1b["accrualDays"]!.GetValue<int>().Should().Be(32);

        // The scheduled end of revolving: the payment date in October amortizes.
        periods[2]!["phase"]!.GetValue<string>().Should().Be("AMORTIZING");
        response["ownerTrust"]!["phasePath"]!.AsArray().Should().ContainSingle();
        response["trancheCashflows"]!.AsObject().Select(p => p.Key).Should()
            .BeEquivalentTo(new[] { "notes/A-1a", "notes/A-1b", "notes/B" });
        response["ownerTrust"]!["closing"]!["state"]!["phase"]!.GetValue<string>().Should().Be("AMORTIZING");
        response["series"]!.AsObject().Should().BeEmpty();
    }

    [Fact]
    public async Task AnOwnerTrustRefusesACardProgramsFieldsByName()
    {
        var body = TrustBody();
        body["program"]!["recovery_bucket"] = "FINANCE";
        body["pool_balance"] = 1000.0;

        var (status, text) = await Post(body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("program.recoveryBucket").And.Contain("poolBalance");
    }

    [Theory]
    [InlineData("verb", "PAY_SOMETHING")]
    [InlineData("model", "CARD")]
    [InlineData("series", "two")]
    [InlineData("curve", "none")]
    public async Task AnOwnerTrustTheEngineCannotReadIsRefused(string what, string expected)
    {
        var body = TrustBody();
        switch (what)
        {
            case "verb":
                body["program"]!["owner_trust"]!["steps"]!.AsArray().Insert(0, new JsonObject { ["verb"] = "PAY_SOMETHING" });
                break;
            case "model":
                body["pool"]!["model"] = "CARD";
                break;
            case "series":
                body["series"]!.AsArray().Add(JsonNode.Parse("""{ "name": "two", "classes": [] }"""));
                break;
            case "curve":
                body.Remove("market_rates");
                break;
        }

        var (status, text) = await Post(body);

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain(what switch
        {
            "verb" => expected,
            "model" => "INSTALLMENT",
            "series" => "states 2",
            _ => "Libor1M"
        });
    }

    [Fact]
    public async Task ACardProgramsResponseCarriesNoOwnerTrust()
    {
        var card = JsonNode.Parse("""
            {
              "program": {
                "name": "Example card trust", "recovery_bucket": "FINANCE", "pool_measure": "PERIOD_START",
                "principal_freeze": "FROZEN_PERCENT",
                "finance_steps": [{ "verb": "PAY_INTEREST", "class": "A" }, { "verb": "RELEASE_TRANSFEROR" }],
                "principal_steps": [{ "verb": "SHARE_PRINCIPAL" }]
              },
              "statement_date": "2026-07-31", "periods": 2, "pool_balance": 1000000.0,
              "drivers": { "payment_rate": 30.0, "hold_pool": true, "yield": 1.5, "charge_off_rate": 0.2 },
              "funding_earnings_rate": [0.0],
              "series": [{ "name": "S", "classes": [{ "key": "A", "balance": 400000.0, "coupon": 5.0 }],
                           "writedown_order": ["A"] }]
            }
            """)!.AsObject();

        var (status, text) = await Post(card);

        status.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotContain("ownerTrust");
    }
}
