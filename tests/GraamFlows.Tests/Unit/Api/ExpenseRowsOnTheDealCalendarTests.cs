using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Factories;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Waterfall.MarketTranche;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     An expense row is dated on the deal's calendar.
///
///     Both deal readers built every expense tranche on a fixed calendar — PayFrequency 12,
///     PayDay 25, 30/360, Following — and an expense row's date is its tranche's
///     <c>AdjustedCashflowDate</c>, which reads exactly those fields (plus FirstPayDate). On a
///     quarterly deal whose notes pay on the 20th, the notes were dated 07-20, 10-20, ... and every
///     fee row 07-25, 10-25, ...: the right amounts on dates no note row carries, so a consumer
///     joining fees to notes by date showed them on separate rows. The amounts were never wrong,
///     which is why nothing caught it.
///
///     The fixture's <c>WithExpenseTranche</c> already copies the deal's PayDay and FirstPayDate,
///     so engine-level tests could not see this; the defect lived in the two readers, and these
///     tests go through them (reflection, as <see cref="TrancheDatedDateWiringTests" /> does).
/// </summary>
public class ExpenseRowsOnTheDealCalendarTests
{
    private static readonly DateTime QuarterlyFirstPay = new(2026, 7, 20);
    private static readonly DateTime MonthlyFirstPay = new(2026, 2, 25);
    private const string Fee = "AdminFee";
    private const string MgmtFee = "SeniorMgmtFee";

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    public static IEnumerable<object[]> ReadersAndDayCounts =>
        from r in Readers
        from dc in new[] { "30/360", "Actual/360" }
        select new[] { r[0], dc };

    // -- the defect ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ReadersAndDayCounts))]
    public void A_quarterly_deal_paying_on_the_20th_dates_its_fees_on_its_note_dates(Type reader,
        string dayCount)
    {
        var dto = Deal(payFrequency: 4, payDay: 20, QuarterlyFirstPay, dayCount);
        var cf = Run(Build(reader, dto), Pool(QuarterlyFirstPay, 37));

        var noteDates = Dates(cf, "A");
        noteDates.Should().HaveCount(13, "37 collateral months from a pay month are 13 quarterly distributions");
        noteDates.Should().NotContain(d => d.Day == 25);

        foreach (var expense in new[] { Fee, MgmtFee })
        {
            var rows = Rows(cf, expense);
            rows.Should().OnlyContain(r => r.Expense > 0, $"{expense} is charged every distribution");
            rows.Select(r => r.CashflowDate).Should().Equal(noteDates,
                $"{expense} is paid at the EXPENSE step of the same distribution the notes are paid in");
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void An_actual_360_deal_dates_its_fees_on_the_business_day_its_notes_pay(Type reader)
    {
        // The business-day branch of AdjustCashflowDate runs only off 30/360, so this is the case
        // where copying PayDay alone would not be enough: a fee left on 30/360 keeps the raw 20th
        // while the notes roll forward off a weekend.
        var dto = Deal(payFrequency: 4, payDay: 20, QuarterlyFirstPay, "Actual/360");
        var cf = Run(Build(reader, dto), Pool(QuarterlyFirstPay, 37));

        var noteDates = Dates(cf, "A");
        noteDates.Should().Contain(d => d.Day != 20,
            "some 20th in three years falls on a weekend; otherwise this case is not exercised");
        Dates(cf, Fee).Should().Equal(noteDates);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void The_expense_tranche_carries_the_notes_calendar(Type reader)
    {
        var deal = Build(reader, Deal(payFrequency: 4, payDay: 20, QuarterlyFirstPay, "Actual/360",
            businessDayConvention: "ModifiedFollowing"));
        var note = deal.Tranches.Single(t => t.TrancheName == "A");

        foreach (var expense in deal.Tranches.Where(t => t.CashflowTypeEnum == CashflowType.Expense))
        {
            expense.PayDay.Should().Be(note.PayDay);
            expense.PayFrequency.Should().Be(note.PayFrequency);
            expense.DayCount.Should().Be(note.DayCount);
            expense.BusinessDayConvention.Should().Be(note.BusinessDayConvention);
            expense.HolidayCalendar.Should().Be(note.HolidayCalendar);
            expense.FirstPayDate.Should().Be(note.FirstPayDate);
            expense.FirstSettleDate.Should().Be(note.FirstSettleDate);
        }
    }

    [Fact]
    public void Through_the_api_the_fee_rows_join_the_note_rows_by_date()
    {
        // The consumer's view: /api/Waterfall's response, joined on cashflowDate.
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var request = new WaterfallRequest
        {
            ProjectionDate = QuarterlyFirstPay.AddMonths(-1),
            CollateralCashflows = PoolDtos(QuarterlyFirstPay, 37),
            Deal = Deal(payFrequency: 4, payDay: 20, QuarterlyFirstPay, "30/360"),
        };
        var raw = controller.Execute(request).Result;
        var ok = raw as OkObjectResult;
        ok.Should().NotBeNull($"the waterfall request should succeed, got {(raw as ObjectResult)?.Value}");
        var response = (WaterfallResponse)ok!.Value!;

        var noteDates = response.TrancheCashflows["A"].Select(r => r.CashflowDate).ToList();
        noteDates.Should().NotBeEmpty();
        foreach (var expense in new[] { Fee, MgmtFee })
            response.TrancheCashflows[expense].Select(r => r.CashflowDate).Should().Equal(noteDates);
    }

    // -- the unchanged case ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Readers))]
    public void A_monthly_25th_deal_is_byte_identical_to_the_fixed_calendar(Type reader)
    {
        // Every deal before this was monthly, and most pay on the 25th on 30/360 — the calendar the
        // readers hard-coded. Run the same deal twice: once as built, once with its expense rows
        // put back on the old hard-coded calendar, and compare every field of every row.
        var dto = Deal(payFrequency: 12, payDay: 25, MonthlyFirstPay, "30/360");
        var pool = Pool(MonthlyFirstPay, 36);

        var current = Run(Build(reader, dto), pool);
        var legacyDeal = Build(reader, dto);
        foreach (var t in legacyDeal.Tranches.OfType<GraamFlows.Waterfall.Tranche>()
                     .Where(t => t.CashflowTypeEnum == CashflowType.Expense))
        {
            t.PayFrequency = 12;
            t.PayDay = 25;
            t.DayCount = "30/360";
            t.BusinessDayConvention = "Following";
            t.HolidayCalendar = "Settlement";
            t.FirstPayDate = default;
            t.FirstSettleDate = default;
        }

        var legacy = Run(legacyDeal, pool);

        Rows(current, Fee).Should().NotBeEmpty();
        Rows(current, Fee).Should().OnlyContain(r => r.CashflowDate.Day == 25 && r.Expense > 0);
        Fingerprint(current).Should().Equal(Fingerprint(legacy));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void A_deal_with_no_note_keeps_the_fixed_calendar(Type reader)
    {
        var dto = Deal(payFrequency: 4, payDay: 20, QuarterlyFirstPay, "Actual/360");
        dto.Tranches.Clear();
        dto.UnifiedWaterfall = null;
        var expense = Build(reader, dto).Tranches.Single(t => t.TrancheName == Fee);

        expense.PayFrequency.Should().Be(12);
        expense.PayDay.Should().Be(25);
        expense.DayCount.Should().Be("30/360");
        expense.BusinessDayConvention.Should().Be("Following");
        expense.HolidayCalendar.Should().Be("Settlement");
    }

    // -------------------------------------------------------------------------------------------

    private static IDeal Build(Type owner, DealDto dto)
    {
        var m = owner.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{owner.Name}.BuildDeal must exist for this seam to be testable");
        return (IDeal)m!.Invoke(null, new object?[] { dto, dto.ClosingDate!.Value, null })!;
    }

    private static DealCashflows Run(IDeal deal, CollateralCashflows pool)
    {
        var first = pool.PeriodCashflows.First().CashflowDate;
        var projection = first.AddMonths(-1);
        var assumps = DealLevelAssumptions.CreateConstAssumptions(projection, DateUtil.CalcAbsT(projection), 0, 0, 0);
        return WaterfallFactory.GetWaterfall(deal.CashflowEngine)
            .Waterfall(deal, new ConstantTestRateProvider(5.0), first, pool, assumps, new TrancheAllocator());
    }

    private static DealDto Deal(int payFrequency, int payDay, DateTime firstPay, string dayCount,
        string businessDayConvention = "Following") => new()
    {
        DealName = "EXPENSE_CALENDAR_TEST",
        WaterfallType = "ComposableStructure",
        ClosingDate = firstPay.AddMonths(-12 / payFrequency),
        Tranches = new List<TrancheDto>
        {
            Note("A", 80_000_000, 0, payFrequency, payDay, firstPay, dayCount, businessDayConvention),
            Note("B", 20_000_000, 1, payFrequency, payDay, firstPay, dayCount, businessDayConvention),
        },
        Expenses = new List<ExpenseDto>
        {
            new() { ExpenseName = Fee, Formula = "begin_balance * 0.0002 / 12 * period_months", GroupNum = 1 },
            new() { ExpenseName = MgmtFee, Formula = "begin_balance * 0.0015 / 12 * period_months", GroupNum = 1 },
        },
        UnifiedWaterfall = new UnifiedWaterfallDto
        {
            ExecutionOrder = new List<string>
            {
                "EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "WRITEDOWN"
            },
            Steps = new List<WaterfallStepDto>
            {
                new() { Type = "INTEREST", Structure = Seq("A", "B") },
                new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
                new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B") },
                new() { Type = "WRITEDOWN", Structure = Seq("B", "A") },
            },
        },
    };

    private static TrancheDto Note(string name, double balance, int subOrder, int payFrequency, int payDay,
        DateTime firstPay, string dayCount, string businessDayConvention) => new()
    {
        TrancheName = name,
        OriginalBalance = balance,
        TrancheType = "Offered",
        CashflowType = "PI",
        CouponType = "Fixed",
        FixedCoupon = 5.0,
        SubordinationOrder = subOrder,
        GroupNum = "1",
        FirstPayDate = firstPay,
        PayFrequency = payFrequency,
        PayDay = payDay,
        DayCount = dayCount,
        BusinessDayConvention = businessDayConvention,
    };

    private static PayableStructureDto Seq(params string[] tranches) =>
        new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static CollateralCashflows Pool(DateTime start, int months) =>
        new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(start, months, 100_000_000, cpr: 10.0, cdr: 0.0, wac: 8.0)
            .Build();

    private static List<PeriodCashflowDto> PoolDtos(DateTime start, int months)
    {
        var rows = new List<PeriodCashflowDto>();
        var balance = 100_000_000.0;
        for (var i = 0; i < months; i++)
        {
            var interest = balance * 0.08 / 12;
            var fee = balance * 0.0025 / 12;
            var principal = i == months - 1 ? balance : balance * 0.02;
            rows.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = start.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = balance - principal,
                ScheduledPrincipal = principal,
                Interest = interest,
                NetInterest = interest - fee,
                ServiceFee = fee,
                Wac = 8.0,
                Wam = 360 - i,
            });
            balance -= principal;
        }

        return rows;
    }

    private static List<TrancheCashflow> Rows(DealCashflows cf, string tranche) =>
        cf.TrancheCashflows.Single(t => t.Key.TrancheName == tranche).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static List<DateTime> Dates(DealCashflows cf, string tranche) =>
        Rows(cf, tranche).Select(r => r.CashflowDate).ToList();

    /// <summary>Every scalar field of every row of every tranche and class, as text.</summary>
    private static List<string> Fingerprint(DealCashflows cf)
    {
        var props = typeof(TrancheCashflow).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(DateTime)
                        || p.PropertyType == typeof(string) || p.PropertyType.IsEnum)
            .OrderBy(p => p.Name)
            .ToList();
        props.Should().Contain(p => p.Name == nameof(TrancheCashflow.Expense));

        IEnumerable<string> Dump(string kind, string name, IEnumerable<TrancheCashflow> rows) =>
            rows.Select(r => $"{kind}/{name}/" + string.Join("|",
                props.Select(p => $"{p.Name}={Convert.ToString(p.GetValue(r), System.Globalization.CultureInfo.InvariantCulture)}")));

        return cf.TrancheCashflows.OrderBy(t => t.Key.TrancheName)
            .SelectMany(t => Dump("T", t.Key.TrancheName, t.Value.Cashflows.OrderBy(c => c.Key).Select(c => c.Value)))
            .Concat(cf.ClassCashflows.OrderBy(t => t.Key.TrancheName)
                .SelectMany(t => Dump("C", t.Key.TrancheName, t.Value.Cashflows.OrderBy(c => c.Key).Select(c => c.Value))))
            .ToList();
    }
}
