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
