namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     A yield supplement overcollateralization (YSOC) schedule: the amount by which each Payment
///     Date's Pool Balance exceeds the Adjusted Pool Balance the notes are paid against.
///     <para>
///         A pool with receivables at subsidized APRs earns less interest than the notes and fees
///         need, so the deal discounts those receivables: the YSOC on a date is what their
///         principal balance exceeds the present value of their payments at a required rate. The
///         Adjusted Pool Balance is the Pool Balance less the YSOC, and the overcollateralization
///         the notes are paid to is the Adjusted Pool Balance less the notes. A model that measures
///         it on the Pool Balance reads the whole YSOC as overcollateralization above target and
///         holds that much principal back from the notes.
///     </para>
///     <para>
///         The prospectus prints the amount for each Payment Date, by month. Some deals print a
///         second schedule, at a lower required rate, that applies to every Payment Date after
///         named classes (a floating class, say) are paid in full: <see cref="After" />.
///     </para>
/// </summary>
public record YsocSchedule
{
    /// <summary>The YSOC for each Payment Date, keyed by the date's year and month.</summary>
    public IReadOnlyDictionary<(int Year, int Month), double> Amounts { get; init; } =
        new Dictionary<(int Year, int Month), double>();

    /// <summary>
    ///     The schedule in force on a Payment Date after every one of <see cref="YsocSwitch.Classes" />
    ///     has been paid in full, or null.
    /// </summary>
    public YsocSwitch? After { get; init; }

    /// <summary>
    ///     The YSOC on <paramref name="paymentDate" />. <paramref name="paidInFull" /> says whether
    ///     every class it is given was paid in full on an earlier Payment Date. A month past the last
    ///     printed one is zero (the schedule runs to zero); a month before the first printed one
    ///     takes the first.
    /// </summary>
    public double AmountOn(DateTime paymentDate, Func<IReadOnlyList<string>, bool> paidInFull)
    {
        if (After != null && paidInFull(After.Classes))
            return Lookup(After.Amounts, paymentDate);
        return Lookup(Amounts, paymentDate);
    }

    private static double Lookup(IReadOnlyDictionary<(int Year, int Month), double> amounts, DateTime on)
    {
        if (amounts.Count == 0)
            return 0;
        if (amounts.TryGetValue((on.Year, on.Month), out var amount))
            return amount;
        var first = amounts.Keys.Min();
        return (on.Year, on.Month).CompareTo(first) < 0 ? amounts[first] : 0;
    }
}

/// <summary>The schedule that applies once <see cref="Classes" /> are all paid in full.</summary>
public record YsocSwitch
{
    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<(int Year, int Month), double> Amounts { get; init; } =
        new Dictionary<(int Year, int Month), double>();
}
