using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>A master trust as a caller meets it on /api/revolving: JSON text, through the whole app.</summary>
public class MasterTrustHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public MasterTrustHttpTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>A public and a private series over one pool: one replayed month and two forecast ones, in snake_case.</summary>
    private static JsonObject TrustBody() => JsonNode.Parse("""
        {
          "program": {
            "name": "A master trust",
            "master_trust": {
              "servicing_rate": 0.75,
              "tests": { "write_off_limit": 10.0, "delinquency_limit": 2.0 },
              "acquisition": { "price": { "percent_of_face": 92.8 }, "terms": [{ "term": 24, "weight": 100.0 }],
                               "hold_pool_face": true },
              "state": { "concentration": { "TERM": 10000.0 }, "certificate_cash": 50000.0 },
              "series": [
                { "name": "public", "discount_rate": 6.0, "oc_pct": 8.25, "concentration_group": "TERM",
                  "tiers": [{ "name": "A", "classes": ["A-1a", "A-1b"] }, { "name": "B", "classes": ["B"] }],
                  "steps": [
                    { "verb": "PAY_FEES" }, { "verb": "PAY_SERVICING" },
                    { "verb": "PAY_INTEREST", "tier": "A" }, { "verb": "PRIORITY_PRINCIPAL", "tier": "A" },
                    { "verb": "PAY_INTEREST", "tier": "B" }, { "verb": "PRIORITY_PRINCIPAL", "tier": "B" },
                    { "verb": "FUND_RESERVE" }, { "verb": "REGULAR_PRINCIPAL" }, { "verb": "PAY_CERTIFICATE" }
                  ],
                  "required_reserve": 5000.0, "monthly_fees": 100.0,
                  "tests": { "interest_shortfall": true, "allocated_pool_limit": 50.0 },
                  "state": { "reserve": 5000.0 } },
                { "name": "private", "discount_rate": 9.0, "oc_pct": 8.75, "private": true, "private_notes": 300000.0,
                  "concentration_group": "TERM" }
              ],
              "actual_periods": [{
                "fads": [
                  { "date": "2026-08-01", "pool_balance": 1200000.0 },
                  { "date": "2026-08-20", "pool_balance": 1230000.0, "concentration": { "TERM": 12000.0 } },
                  { "date": "2026-08-31", "pool_balance": 1210000.0 }
                ],
                "write_off_ratio": 1.2, "delinquency_ratio": 0.4, "delinquency": 0.4,
                "series": { "public": { "fees": 120.0, "coupons": { "B": 3.5 } }, "private": { "note_balance": 320000.0 } }
              }]
            }
          },
          "statement_date": "2026-07-31",
          "distribution_day": 20,
          "periods": 3,
          "pool": {
            "model": "INSTALLMENT",
            "cohorts": [{ "name": "opening", "age": 6, "schedule": [50000.0, 50000.0, 50000.0, 50000.0, 50000.0,
              50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0,
              50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0, 50000.0] }],
            "actual_periods": [{ "scheduled_collections": 50000.0, "prepaid_collections": 10000.0, "write_offs": 1000.0,
              "additions": [{ "face": 61000.0, "term": 24, "price": { "percent_of_face": 92.8 } }] }],
            "drivers": { "prepayment_rate": { "monthly": [0.5] }, "write_off_rate": 0.1 }
          },
          "series": [{
            "name": "public",
            "classes": [
              { "key": "A-1a", "balance": 650000.0, "coupon": 4.5 },
              { "key": "A-1b", "balance": 100000.0, "index": "Sofr30Avg", "margin": 0.5, "day_count": "Actual/360" },
              { "key": "B", "balance": 50000.0, "coupon": 5.0 }
            ]
          }],
          "market_rates": { "Sofr30Avg": [[0, 4.0], [1, 4.1], [2, 4.2]] }
        }
        """)!.AsObject();

    private async Task<(HttpStatusCode Status, string Text)> Post(JsonObject body)
    {
        var response = await _client.PostAsync("/api/revolving",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMasterTrustRunsOnTheRevolvingRoute()
    {
        var (status, text) = await Post(TrustBody());

        status.Should().Be(HttpStatusCode.OK, text);
        var response = JsonNode.Parse(text)!.AsObject();
        response.ContainsKey("ownerTrust").Should().BeFalse();
        var periods = response["masterTrust"]!["periods"]!.AsArray();
        periods.Count.Should().Be(3);

        var first = periods[0]!;
        first["replayed"]!.GetValue<bool>().Should().BeTrue();
        first["fads"]!.AsArray().Select(f => f!["days"]!.GetValue<int>()).Should().Equal(19, 11, 1);
        first["timeWeightedPool"]!.GetValue<double>().Should()
            .BeApproximately(Math.Round((19 * 1200000.0 + 11 * 1230000.0 + 1210000.0) / 31.0, 2), 1e-6);
        var rows = first["series"]!.AsArray();
        rows.Select(r => r!["name"]!.GetValue<string>()).Should().Equal("public", "private");
        var pub = rows[0]!;
        pub["fees"]!.GetValue<double>().Should().Be(120.0);
        pub["phase"]!.GetValue<string>().Should().Be("REVOLVING");
        pub["classes"]!.AsArray().First(c => c!["key"]!.GetValue<string>() == "B")!["coupon"]!
            .GetValue<double>().Should().Be(3.5);
        var allocation = rows.Sum(r => r!["allocationPct"]!.GetValue<double>());
        (allocation + first["transferorPct"]!.GetValue<double>()).Should().BeApproximately(100.0, 1e-9);
        rows[1]!["private"]!.GetValue<bool>().Should().BeTrue();
        rows[1]!["notesEnd"]!.GetValue<double>().Should().Be(320000.0);

        // Forecast months buy back to the opening face.
        periods[1]!["additionsFace"]!.GetValue<double>().Should().BeGreaterThan(0.0);
        response["trancheCashflows"]!.AsObject().Select(p => p.Key).Should()
            .BeEquivalentTo(new[] { "public/A-1a", "public/A-1b", "public/B" });
        response["masterTrust"]!["closing"]!["series"]!.AsArray().Count.Should().Be(2);
        response["series"]!.AsObject().Should().BeEmpty();
    }

    [Fact]
    public async Task AProgramIsAnOwnerTrustOrAMasterTrustNotBoth()
    {
        var body = TrustBody();
        body["program"]!["owner_trust"] = new JsonObject { ["discount_rate"] = 6.0 };

        var (status, text) = await Post(body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("not both");
    }

    [Fact]
    public async Task AMasterTrustRefusesACardProgramsFieldsByName()
    {
        var body = TrustBody();
        body["program"]!["recovery_bucket"] = "FINANCE";
        body["pool_balance"] = 1000.0;
        body["series"]![0]!["required_reserve"] = 10.0;

        var (status, text) = await Post(body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("program.recoveryBucket").And.Contain("poolBalance").And.Contain("requiredReserve");
    }

    [Theory]
    [InlineData("noClasses", "request.series states its classes")]
    [InlineData("strayClasses", "masterTrust.series does not")]
    [InlineData("cardPool", "installment pool")]
    [InlineData("noCurve", "no curve")]
    public async Task RefusesByName(string what, string message)
    {
        var body = TrustBody();
        switch (what)
        {
            case "noClasses":
                body["series"] = new JsonArray();
                break;
            case "strayClasses":
                body["series"]!.AsArray().Add(new JsonObject { ["name"] = "stray", ["classes"] = new JsonArray() });
                break;
            case "cardPool":
                body["pool"]!["model"] = "CARD";
                break;
            case "noCurve":
                body.Remove("market_rates");
                break;
        }

        var (status, text) = await Post(body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain(message);
    }
}
