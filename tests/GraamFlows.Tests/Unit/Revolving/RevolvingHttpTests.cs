using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>What another route's body looks like once the app's key rewriting has been at it.</summary>
[ApiController]
[Route("api/echo")]
public class EchoController : ControllerBase
{
    [HttpPost]
    public async Task<ContentResult> Post()
    {
        using var reader = new StreamReader(Request.Body);
        return Content(await reader.ReadToEndAsync(), "application/json");
    }
}

/// <summary>
///     The route as a caller meets it: through the whole app, with its snake_case rewriting of request
///     keys, in JSON text, and a response sent straight back as the next request.
/// </summary>
public class RevolvingHttpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Test = "YIELD_BELOW_BASE_RATE";
    private const string Reserve = "RESERVE_ON_THIN_SPREAD";
    private const string Fee = "servicing_fee";

    private readonly HttpClient _client;

    public RevolvingHttpTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(host => host.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(EchoController).Assembly))).CreateClient();
    }

    private static JsonObject Body(int periods) => JsonNode.Parse($$"""
        {
          "program": {
            "name": "Example card trust",
            "recovery_bucket": "FINANCE",
            "pool_measure": "PERIOD_START",
            "action_lag": 1,
            "principal_freeze": "FROZEN_PERCENT",
            "finance_steps": [
              { "verb": "PAY_INTEREST", "class": "A" },
              { "verb": "PAY_FEE", "fee": "{{Fee}}" },
              { "verb": "COVER_DEFAULTS", "class": "A" },
              { "verb": "FUND_RESERVE" },
              { "verb": "RELEASE_TRANSFEROR" }
            ],
            "principal_steps": [
              { "verb": "DEPOSIT_PFA", "phases": ["ACCUMULATING"] },
              { "verb": "PAY_CLASS", "class": "A", "phases": ["AMORTIZING", "EARLY_AMORTIZATION"] },
              { "verb": "SHARE_PRINCIPAL" }
            ],
            "triggers": [
              {
                "name": "{{Test}}", "metric": "PORTFOLIO_YIELD", "window": 1, "comparison": "BELOW",
                "against_metric": "BASE_RATE", "action": "EARLY_AMORTIZATION"
              },
              {
                "name": "{{Reserve}}", "metric": "EXCESS_SPREAD", "window": 1, "comparison": "BELOW", "level": 2.0,
                "action": "FUND_RESERVE", "cure_periods": 2, "reserve_amount": 5.0
              }
            ]
          },
          "statement_date": "2026-07-31",
          "periods": {{periods}},
          "pool_balance": 1000000.0,
          "drivers": {
            "payment_rate": 30.0, "hold_pool": true, "yield": 0.01, "charge_off_rate": 0.2
          },
          "funding_earnings_rate": [0.0],
          "series": [
            {
              "name": "S", "group": "1",
              "classes": [{ "key": "A", "balance": 400000.0, "coupon": 5.0 }],
              "fees": [{ "name": "{{Fee}}", "rate": 2.0 }],
              "writedown_order": ["A"], "funded_classes": ["A"]
            }
          ]
        }
        """)!.AsObject();

    private async Task<JsonObject> Post(JsonObject body)
    {
        var response = await _client.PostAsync("/api/revolving",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static JsonObject Next(JsonObject body, JsonObject response, DateTime statement)
    {
        var periods = response["periods"]!.AsArray();
        var closing = response["series"]!["S"]!["closing"]!;
        var next = body.DeepClone().AsObject();
        next["statement_date"] = statement.ToString("yyyy-MM-dd");
        next["periods"] = 1;
        next["pool_balance"] = periods[periods.Count - 1]!["endingBalance"]!.DeepClone();
        var series = next["series"]!.AsArray()[0]!.AsObject();
        series["state"] = closing["state"]!.DeepClone();
        var cls = series["classes"]!.AsArray()[0]!.AsObject();
        foreach (var (key, value) in closing["classes"]!["A"]!.AsObject())
            cls[key] = value!.DeepClone();
        return next;
    }

    [Fact]
    public async Task ARunCutOverTheWireMatchesOneLongRun()
    {
        var whole = await Post(Body(6));

        var body = Body(1);
        var month = await Post(body);
        var feesSeen = false;
        var testsSeen = false;
        var statement = new DateTime(2026, 8, 31);
        for (var i = 1; i < 6; i++)
        {
            var state = month["series"]!["S"]!["closing"]!["state"]!;
            feesSeen |= state["feesDue"]![Fee]!.GetValue<double>() > 0.0;
            testsSeen |= state["triggerState"]!.AsObject().ContainsKey(Test);

            month = await Post(Next(body, month, statement));

            var want = whole["series"]!["S"]!["periods"]![i]!;
            var got = month["series"]!["S"]!["periods"]![0]!;
            got["phase"]!.GetValue<string>().Should().Be(want["phase"]!.GetValue<string>(), $"month {i}");
            got["feesPaid"]!.GetValue<double>().Should().BeApproximately(want["feesPaid"]!.GetValue<double>(), 1e-6, $"month {i}");
            got["reserve"]!.GetValue<double>().Should().BeApproximately(want["reserve"]!.GetValue<double>(), 1e-6, $"month {i}");
            got["investedAmount"]!.GetValue<double>().Should()
                .BeApproximately(want["investedAmount"]!.GetValue<double>(), 1e-6, $"month {i}");
            statement = new DateTime(statement.Year, statement.Month, 1).AddMonths(2).AddDays(-1);
        }

        feesSeen.Should().BeTrue();
        testsSeen.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnknownNameInAStateIsRefusedAsSent()
    {
        var body = Body(1);
        body["series"]!.AsArray()[0]!["state"] = JsonNode.Parse("""{ "fees_due": { "no_such_fee": 1.0 } }""");

        var response = await _client.PostAsync("/api/revolving",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no_such_fee");
    }

    [Fact]
    public async Task ASnakeCaseFieldInsideANamedKeyIsStillRead()
    {
        var body = Body(1);
        body["series"]!.AsArray()[0]!["state"] = JsonNode.Parse($$"""
            { "trigger_state": { "{{Test}}": { "latched": true, "months_to_act": 3, "good_months": 0 } } }
            """);

        var month = await Post(body);

        month["series"]!["S"]!["closing"]!["state"]!["triggerState"]![Test]!["monthsToAct"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task ATestStatedWithNoValueIsANamed400()
    {
        var body = Body(1);
        body["series"]!.AsArray()[0]!["state"] = JsonNode.Parse($$"""{ "triggerState": { "{{Test}}": null } }""");

        var response = await _client.PostAsync("/api/revolving",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(Test).And.Contain("no value");
    }

    [Fact]
    public async Task OtherRoutesKeepTheirKeyRewriting()
    {
        var response = await _client.PostAsync("/api/echo", new StringContent(
            """{ "fees_due": { "some_name": 1 }, "marked_up": "a_b" }""", Encoding.UTF8, "application/json"));

        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("someName").And.Contain("feesDue").And.Contain("markedUp").And.Contain("a_b");
    }
}
