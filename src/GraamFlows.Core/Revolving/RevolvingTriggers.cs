namespace GraamFlows.Revolving;

/// <summary>One test's reading at the start of a month.</summary>
/// <param name="Evaluated">false until the test has a full window of months.</param>
/// <param name="Value">the rolling average tested.</param>
/// <param name="Reference">the level or second rolling average it is tested against.</param>
/// <param name="Headroom">how far the test is from failing. Negative means it is failing.</param>
/// <param name="Latched">the test has tripped and has not cured.</param>
/// <param name="Acting">its action is in force this month.</param>
public sealed record TriggerReading(
    string Name,
    bool Evaluated,
    double? Value,
    double? Reference,
    double? Headroom,
    bool Latched,
    bool Acting);

/// <summary>
///     The latch for one test on one series. Latching is per test: EARLY_AMORTIZATION always sticks,
///     and a FUND_RESERVE test releases after its stated run of good months. A tripped test acts
///     <c>actionLag</c> months after the month it tripped.
/// </summary>
internal sealed class TriggerLatch
{
    private readonly int _lag;
    private int? _actsAt;
    private int _goodMonths;

    public TriggerLatch(TriggerTest test, int lag)
    {
        Test = test;
        _lag = lag;
    }

    public TriggerTest Test { get; }
    public bool Latched { get; private set; }
    public int? FirstTrip { get; private set; }

    public bool ActingIn(int period)
    {
        return Latched && _actsAt <= period;
    }

    /// <summary>Tests the months before <paramref name="period" />.</summary>
    public TriggerReading Read(int period, IReadOnlyDictionary<TriggerMetric, List<double>> history)
    {
        var value = RollingAverage(history, Test.Metric);
        var reference = Test.AgainstMetric is { } against ? RollingAverage(history, against) : Test.Level;
        if (value == null || reference == null)
            return new TriggerReading(Test.Name, false, null, null, null, Latched, ActingIn(period));

        var headroom = Test.Comparison == TriggerComparison.Below
            ? value.Value - reference.Value
            : reference.Value - value.Value;

        if (headroom < 0)
            Fail(period);
        else if (Latched)
            Pass();

        return new TriggerReading(Test.Name, true, value, reference, headroom, Latched, ActingIn(period));
    }

    private void Fail(int period)
    {
        _goodMonths = 0;
        if (Latched)
            return;

        Latched = true;
        _actsAt = period + _lag;
        FirstTrip ??= period;
    }

    private void Pass()
    {
        if (Test.Action != TriggerAction.FundReserve)
            return;

        _goodMonths++;
        if (_goodMonths < Test.CurePeriods)
            return;

        Latched = false;
        _actsAt = null;
        _goodMonths = 0;
    }

    private double? RollingAverage(IReadOnlyDictionary<TriggerMetric, List<double>> history, TriggerMetric metric)
    {
        if (!history.TryGetValue(metric, out var months) || months.Count < Test.Window)
            return null;

        return months.Skip(months.Count - Test.Window).Average();
    }
}
