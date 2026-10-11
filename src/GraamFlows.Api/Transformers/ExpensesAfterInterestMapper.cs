using GraamFlows.Api.Models;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps <see cref="UnifiedWaterfallDto.ExpensesAfterInterest" />: the expenses the Priority of
///     Payments pays after the notes' interest. Each must be one of the deal's expenses — a name
///     that is not would be paid nowhere, so it is refused.
/// </summary>
public static class ExpensesAfterInterestMapper
{
    public static IReadOnlyList<string> Map(IList<string>? names, IEnumerable<ExpenseDto>? expenses, string? dealName)
    {
        if (names is not { Count: > 0 })
            return Array.Empty<string>();
        var defined = (expenses ?? Enumerable.Empty<ExpenseDto>())
            .Select(e => e.ExpenseName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(n => !defined.Contains(n)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Deal {dealName}: expensesAfterInterest names {string.Join(", ", unknown)}, which the deal does " +
                "not define as an expense — it would be paid nowhere.");
        return names.ToList();
    }
}
