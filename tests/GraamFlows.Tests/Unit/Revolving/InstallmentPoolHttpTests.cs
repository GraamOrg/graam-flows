using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>The installment pool as a caller meets it: JSON text, through the whole app.</summary>
public class InstallmentPoolHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public InstallmentPoolHttpTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    private static JsonObject PoolBody() => JsonNode.Parse("""
        {
          "statement_date": "2026-07-31",
          "periods": 4,
          "pool": {
            "model": "INSTALLMENT",
            "discount_rates": [9.0, 12.5],
            "cohorts": [
              { "name": "opening", "age": 10, "schedule": [1000.0, 1000.0, 1000.0, 990.0, 980.0] }
            ],
            "actual_periods": [
              {
                "beginning_face": 4970.0,
                "scheduled_collections": 1000.0, "prepaid_collections": 120.0, "write_offs": 30.0,
                "additions": [{ "face": 3600.0, "term": 36, "price": { "pv_rate": 9.0 } }]
              }
            ],
            "drivers": {
              "prepayment_rate": { "annual": [12.0] },
              "upgrade_rate": [0.0, 0.5],
              "write_off_rate": 0.1,
              "reinvest": { "share": 100.0, "price": { "percent_of_face": 86.5 }, "terms": [{ "term": 36, "weight": 100.0 }] }
            }
          }
        }
        """)!.AsObject();

    private static JsonObject CardBody() => JsonNode.Parse("""
        {
          "program": {
            "name": "Example card trust",
            "recovery_bucket": "FINANCE",
            "pool_measure": "PERIOD_START",
            "principal_freeze": "FROZEN_PERCENT",
            "finance_steps": [
              { "verb": "PAY_INTEREST", "class": "A" },
              { "verb": "COVER_DEFAULTS", "class": "A" },
              { "verb": "RELEASE_TRANSFEROR" }
            ],
            "principal_steps": [
              { "verb": "PAY_CLASS", "class": "A", "phases": ["AMORTIZING", "EARLY_AMORTIZATION"] },
              { "verb": "SHARE_PRINCIPAL" }
            ]
          },
          "statement_date": "2026-07-31",
          "periods": 3,
          "pool_balance": 1000000.0,
          "drivers": { "payment_rate": 30.0, "hold_pool": true, "yield": 1.5, "charge_off_rate": 0.2 },
          "funding_earnings_rate": [0.0],
          "series": [
            {
              "name": "S",
              "classes": [{ "key": "A", "balance": 400000.0, "coupon": 5.0 }],
              "writedown_order": ["A"]
            }
          ]
        }
        """)!.AsObject();

    private async Task<(HttpStatusCode Status, string Text)> Post(string route, JsonObject body)
    {
        var response = await _client.PostAsync(route,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APoolRunReplaysThenForecastsAndValuesEachMonthAtEachRate()
    {
        var (status, text) = await Post("/api/revolving/pool", PoolBody());

        status.Should().Be(HttpStatusCode.OK, text);
        var response = JsonNode.Parse(text)!.AsObject();
        var periods = response["periods"]!.AsArray();
        periods.Count.Should().Be(4);

        var first = periods[0]!;
        first["replayed"]!.GetValue<bool>().Should().BeTrue();
        first["endingFace"]!.GetValue<double>().Should().BeApproximately(4970.0 - 1000.0 - 120.0 - 30.0 + 3600.0, 0.005);
        first["additionsCash"]!.GetValue<double>().Should()
            .BeApproximately(first["valuations"]![0]!["additions"]!.GetValue<double>(), 1e-9);
        first["valuations"]!.AsArray().Select(v => v!["rate"]!.GetValue<double>()).Should().Equal(9.0, 12.5);
        first.AsObject().ContainsKey("ledgerBreak").Should().BeFalse("nothing printed an ending face");

        var forecast = periods[1]!;
        forecast["replayed"]!.GetValue<bool>().Should().BeFalse();
        forecast["additionsCash"]!.GetValue<double>().Should()
            .BeApproximately(forecast["collections"]!.GetValue<double>(), 1e-9);
        forecast["additionsFace"]!.GetValue<double>().Should()
            .BeApproximately(forecast["collections"]!.GetValue<double>() / 0.865, 1e-6);

        response["closing"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnInstallmentPoolOnTheRevolvingRouteIsRefusedByName()
    {
        var body = CardBody();
        body["pool"] = new JsonObject { ["model"] = "INSTALLMENT" };

        var (status, text) = await Post("/api/revolving", body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("/api/revolving/pool");
    }

    [Fact]
    public async Task AnUnknownPoolModelIsRefusedOnBothRoutes()
    {
        var card = CardBody();
        card["pool"] = new JsonObject { ["model"] = "LEASE" };
        var pool = PoolBody();
        pool["pool"]!["model"] = "LEASE";

        foreach (var (route, body) in new[] { ("/api/revolving", card), ("/api/revolving/pool", pool) })
        {
            var (status, text) = await Post(route, body);
            status.Should().Be(HttpStatusCode.BadRequest, route);
            text.Should().Contain("LEASE");
        }
    }

    [Fact]
    public async Task ACardPoolStatedOrNotRunsTheSameCardLedger()
    {
        var stated = CardBody();
        stated["pool"] = new JsonObject { ["model"] = "CARD" };

        var (plainStatus, plain) = await Post("/api/revolving", CardBody());
        var (statedStatus, withModel) = await Post("/api/revolving", stated);

        plainStatus.Should().Be(HttpStatusCode.OK, plain);
        statedStatus.Should().Be(HttpStatusCode.OK, withModel);
        WithoutElapsed(withModel).Should().Be(WithoutElapsed(plain));
        plain.Should().NotContain("valuations").And.NotContain("cohorts");
    }

    [Fact]
    public async Task APoolRouteRequestThatStatesNoModelIsACardPoolAndRefused()
    {
        var body = PoolBody();
        body["pool"]!.AsObject().Remove("model");

        var (status, text) = await Post("/api/revolving/pool", body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("INSTALLMENT");
    }

    [Fact]
    public async Task AnOpeningCohortStatesItsScheduleNotATerm()
    {
        var body = PoolBody();
        body["pool"]!["cohorts"] = JsonNode.Parse("""[{ "age": 0, "face": 3600.0, "term": 36 }]""");

        var (status, text) = await Post("/api/revolving/pool", body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("states its schedule");
    }

    [Fact]
    public async Task ANegativeScheduledPaymentIsRefused()
    {
        var body = PoolBody();
        body["pool"]!["cohorts"]![0]!["schedule"] = JsonNode.Parse("[100.0, -5.0]");

        var (status, text) = await Post("/api/revolving/pool", body);

        status.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("schedule[1]");
    }

    private static string WithoutElapsed(string text)
    {
        var node = JsonNode.Parse(text)!.AsObject();
        node.Remove("elapsedMs");
        return node.ToJsonString();
    }
}
