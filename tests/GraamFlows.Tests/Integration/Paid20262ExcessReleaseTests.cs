using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace GraamFlows.Tests.Integration;

/// <summary>
/// Repro for graam-flows#32 (harmony PAID 2026-2). A certificate/OC deal — rated debt &lt;
/// collateral, the CERTIFICATE holds the OC and is the EXCESS_RELEASE recipient — must release
/// the residual excess interest to the certificate. On this deal it comes back $0 and ~$211M of
/// net interest is undistributed. Drives the REAL harmony-assembled deal_json + collateral (dumped
/// under Data/paid/) straight through WaterfallController.Execute — the exact API path.
/// </summary>
public class Paid20262ExcessReleaseTests
{
    private readonly ITestOutputHelper _out;

    public Paid20262ExcessReleaseTests(ITestOutputHelper output) => _out = output;

    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string DataDir =>
        Path.Combine(AppContext.BaseDirectory, "Integration", "Data", "paid");

    [Fact]
    public void ExcessRelease_ToCertificate_LandsOnCertificateInterest()
    {
        var deal = JsonSerializer.Deserialize<DealDto>(
            File.ReadAllText(Path.Combine(DataDir, "deal.json")), ApiJson)!;
        using var collDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "collateral.json")));
        var cashflows = JsonSerializer.Deserialize<List<PeriodCashflowDto>>(
            collDoc.RootElement.GetProperty("cashflows").GetRawText(), ApiJson)!;
        var projDate = collDoc.RootElement.GetProperty("projectionDate").GetDateTime();

        var request = new WaterfallRequest
        {
            Deal = deal,
            CollateralCashflows = cashflows,
            ProjectionDate = projDate,
        };

        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var result = controller.Execute(request);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull($"waterfall should succeed; got {(result.Result as ObjectResult)?.Value}");
        var resp = (WaterfallResponse)ok!.Value!;

        double Sum(string tr, Func<TrancheCashflowDto, double> sel) =>
            resp.TrancheCashflows.TryGetValue(tr, out var cfs) ? cfs.Sum(sel) : 0.0;

        var certInt = Sum("CERTIFICATE", c => c.Interest) + Sum("CERTIFICATE", c => c.ExcessInterest);
        var certPrin = Sum("CERTIFICATE", c => c.ScheduledPrincipal);
        var bondInt = resp.TrancheCashflows.Keys
            .Where(k => k is not "CERTIFICATE" and not "RESERVE").Sum(k => Sum(k, c => c.Interest));
        // Every tranche's interest, incl. the RESERVE deposit (which legitimately absorbs interest,
        // net-negative) — the whole of collateral net interest must be accounted for.
        var allInt = resp.TrancheCashflows.Keys.Sum(k => Sum(k, c => c.Interest + c.ExcessInterest));
        var collatNetInt = cashflows.Sum(c => c.NetInterest);

        _out.WriteLine($"cert interest+excess = {certInt:N0}");
        _out.WriteLine($"cert principal       = {certPrin:N0}");
        _out.WriteLine($"bond interest        = {bondInt:N0}");
        _out.WriteLine($"all-tranche interest = {allInt:N0}");
        _out.WriteLine($"collateral net int   = {collatNetInt:N0}");
        _out.WriteLine($"UNACCOUNTED interest = {collatNetInt - allInt:N0}");

        certInt.Should().BeGreaterThan(0,
            "the residual excess interest must be released to the CERTIFICATE, not dropped");
        allInt.Should().BeApproximately(collatNetInt, 1.0,
            "collateral net interest is fully conserved across the distributed tranches (incl. reserve)");
    }
}
