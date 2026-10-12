using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps <see cref="UnifiedWaterfallDto.ExpensesAfterInterest" />: the expenses the Priority of
///     Payments pays after the notes' interest. Each must be one of the deal's expenses — a name
///     that is not would be paid nowhere, so it is refused.
/// </summary>
public static class ExpensesAfterInterestMapper
{
    public static IReadOnlyList<string> Map(IList<string>? names, IEnumerable<ExpenseDto>? expenses, string? dealName,
        string field = "expensesAfterInterest")
    {
        if (names is not { Count: > 0 })
            return Array.Empty<string>();
        var defined = (expenses ?? Enumerable.Empty<ExpenseDto>())
            .Select(e => e.ExpenseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(n => !defined.Contains(n)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Deal {dealName}: {field} names {string.Join(", ", unknown)}, which the deal does " +
                "not define as an expense — it would be paid nowhere.");
        return names.ToList();
    }

    /// <summary>
    ///     Maps <see cref="UnifiedWaterfallDto.PrincipalAfterNotes" />. Each expense must be one the
    ///     deal pays after interest (only those carry an unpaid amount to pay), and each note one of
    ///     the deal's tranches; either refused rather than silently ignored.
    /// </summary>
    public static PrincipalAfterNotesConfig? MapPrincipalAfterNotes(PrincipalAfterNotesDto? dto,
        IReadOnlyList<string> afterInterest, IEnumerable<string> trancheNames, string? dealName)
    {
        if (dto == null || dto.Expenses.Count == 0)
            return null;
        var notAfter = dto.Expenses.Where(e => !afterInterest.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (notAfter.Count > 0)
            throw new ArgumentException(
                $"Deal {dealName}: principalAfterNotes pays {string.Join(", ", notAfter)}, which is not paid after " +
                "interest (expensesAfterInterest): only an after-interest expense carries an unpaid amount to pay.");
        var tranches = trancheNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = dto.Notes.Where(n => !tranches.Contains(n)).ToList();
        if (dto.Notes.Count == 0 || unknown.Count > 0)
            throw new ArgumentException(
                $"Deal {dealName}: principalAfterNotes must name the notes repaid ahead of it" +
                (unknown.Count > 0 ? $"; {string.Join(", ", unknown)} is not a tranche." : "."));
        return new PrincipalAfterNotesConfig { Expenses = dto.Expenses.ToList(), Notes = dto.Notes.ToList(), FromDate = dto.FromDate };
    }

    /// <summary>
    ///     Maps <see cref="UnifiedWaterfallDto.InterestFromPrincipal" />: each a tranche, listed once;
    ///     refused rather than silently ignored.
    /// </summary>
    public static IReadOnlyList<string> MapInterestFromPrincipal(List<string>? names,
        IEnumerable<string> trancheNames, string? dealName)
    {
        if (names == null || names.Count == 0)
            return Array.Empty<string>();
        var tranches = trancheNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(n => !tranches.Contains(n)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Deal {dealName}: interestFromPrincipal names {string.Join(", ", unknown)}, which is not a tranche.");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ArgumentException($"Deal {dealName}: interestFromPrincipal lists a class twice.");
        return names.ToList();
    }

    /// <summary>
    ///     Maps <see cref="UnifiedWaterfallDto.DeferrableNotes" />. Every class must be one of the deal's
    ///     tranches and name at least one Priority Class other than itself (a class with none can
    ///     never defer); each class is listed once. Refused rather than silently ignored.
    /// </summary>
    public static IReadOnlyList<DeferrableNoteConfig> MapDeferrableNotes(List<DeferrableNoteDto>? dtos,
        IEnumerable<string> trancheNames, string? dealName)
    {
        if (dtos == null || dtos.Count == 0)
            return Array.Empty<DeferrableNoteConfig>();
        var tranches = trancheNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dtos)
        {
            var unknown = d.PriorityClasses.Prepend(d.Class).Where(n => !tranches.Contains(n)).Distinct().ToList();
            if (unknown.Count > 0)
                throw new ArgumentException(
                    $"Deal {dealName}: deferrableNotes names {string.Join(", ", unknown)}, which is not a tranche.");
            if (!seen.Add(d.Class))
                throw new ArgumentException($"Deal {dealName}: deferrableNotes lists class {d.Class} twice.");
            if (d.PriorityClasses.Count == 0 ||
                d.PriorityClasses.Any(p => string.Equals(p, d.Class, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"Deal {dealName}: deferrable class {d.Class} must name its Priority Classes (the classes " +
                    "senior to it), not itself: its unpaid interest is deferred only while one is outstanding.");
        }

        return dtos.Select(d => new DeferrableNoteConfig
            { Class = d.Class, PriorityClasses = d.PriorityClasses.ToList() }).ToList();
    }

    /// <summary>
    ///     Maps <see cref="UnifiedWaterfallDto.ExpensesFromPrincipal" />: head-of-waterfall expenses
    ///     topped up from principal ahead of the notes. Validated as <see cref="Map" />, and none may
    ///     also be paid after interest — an expense paid after the notes is not paid ahead of them.
    /// </summary>
    public static IReadOnlyList<string> MapFromPrincipal(IList<string>? names, IEnumerable<ExpenseDto>? expenses,
        IReadOnlyList<string> afterInterest, string? dealName)
    {
        var mapped = Map(names, expenses, dealName, "expensesFromPrincipal");
        var both = mapped.Intersect(afterInterest, StringComparer.OrdinalIgnoreCase).ToList();
        if (both.Count > 0)
            throw new ArgumentException(
                $"Deal {dealName}: {string.Join(", ", both)} is both paid after interest and topped up from " +
                "principal ahead of the notes; an expense paid after the notes is not paid ahead of them.");
        return mapped;
    }
}
