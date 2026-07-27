using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;

namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     Rate provider backed by a forward curve per index (graam-flows#37). Unlike
///     <see cref="ConstantRateProvider" />, which returns one flat rate regardless
///     of date, this resolves each index's rate as a function of the projection
///     month — so ARM/hybrid resets track a supplied forward path (and move with
///     rate scenarios) instead of a hardcoded constant.
///
///     Each curve is a list of <c>[monthOffset, rate]</c> points, where
///     <c>monthOffset</c> is months from the projection anchor (0 = first
///     projection month). Between points the rate is linearly interpolated; before
///     the first / after the last point it is held flat (no extrapolation beyond
///     the supplied range). An index with no supplied curve resolves to
///     <see cref="_fallbackRate" /> (default 0), so a fully-indexed coupon falls
///     back to its margin alone.
/// </summary>
public class CurveRateProvider : IRateProvider
{
    private readonly int _anchorAbsT;
    private readonly DateTime _anchorDate;
    private readonly Dictionary<MarketDataInstEnum, (double[] Offsets, double[] Rates)> _curves = new();
    private readonly double _fallbackRate;

    public CurveRateProvider(DateTime anchorDate, IReadOnlyDictionary<MarketDataInstEnum, List<double[]>> curves,
        double fallbackRate = 0.0)
    {
        _anchorDate = anchorDate;
        _anchorAbsT = DateUtil.CalcAbsT(anchorDate);
        _fallbackRate = fallbackRate;

        foreach (var (inst, points) in curves)
        {
            var sorted = points
                .Where(p => p is { Length: >= 2 })
                .OrderBy(p => p[0])
                .ToList();
            if (sorted.Count == 0)
                continue;

            _curves[inst] = (
                sorted.Select(p => p[0]).ToArray(),
                sorted.Select(p => p[1]).ToArray());
        }
    }

    public double GetRate(MarketDataInstEnum inst, DateTime value)
    {
        // Month offset from the projection anchor (robust to day-of-month).
        var offset = (value.Year - _anchorDate.Year) * 12 + (value.Month - _anchorDate.Month);
        return RateAtOffset(inst, offset);
    }

    public double GetRate(MarketDataInstEnum inst, int absT)
    {
        return RateAtOffset(inst, absT - _anchorAbsT);
    }

    public double[] GetRates(MarketDataInstEnum inst, int startAbsT, int length)
    {
        var d = new double[length];
        for (var i = 0; i < length; i++)
            d[i] = RateAtOffset(inst, startAbsT - _anchorAbsT + i);
        return d;
    }

    private double RateAtOffset(MarketDataInstEnum inst, double offset)
    {
        if (!_curves.TryGetValue(inst, out var curve))
            return _fallbackRate;

        var xs = curve.Offsets;
        var ys = curve.Rates;

        // Flat hold outside the supplied range.
        if (offset <= xs[0])
            return ys[0];
        if (offset >= xs[^1])
            return ys[^1];

        // Piecewise-linear interpolation between bracketing points.
        for (var i = 1; i < xs.Length; i++)
        {
            if (offset <= xs[i])
            {
                var span = xs[i] - xs[i - 1];
                if (span <= 0)
                    return ys[i];
                var t = (offset - xs[i - 1]) / span;
                return ys[i - 1] + t * (ys[i] - ys[i - 1]);
            }
        }

        return ys[^1];
    }
}
