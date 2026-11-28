using GraamFlows.Api.Models;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Transforms unified waterfall (steps-based) definitions into PayRules and DealStructures.
///     Eliminates classGroups dependency by inferring subordination from writedown order.
/// </summary>
public static class UnifiedWaterfallBuilder
{
    /// <summary>
    ///     Auto-generates DealStructure objects from tranche list.
    ///     Subordination order is inferred from WRITEDOWN step's structure order.
    /// </summary>
    public static List<DealStructureDto> BuildDealStructures(
        UnifiedWaterfallDto waterfall,
        List<TrancheDto> tranches,
        List<ExchangeShareDto>? exchangeShares = null)
    {
        // Each group's WRITEDOWN step gives its classes' subordination order (group "1"'s is the
        // deal's own steps); each class is in the group whose steps name it.
        var writedownOrders = GroupSteps(waterfall).ToDictionary(g => g.GroupNum, g =>
        {
            var writedownStep = g.Steps.FirstOrDefault(s =>
                s.Type.Equals("WRITEDOWN", StringComparison.OrdinalIgnoreCase));
            return writedownStep?.Structure != null
                ? ExtractTrancheOrder(writedownStep.Structure)
                : new List<string>();
        });
        var classGroups = ClassGroups(waterfall);

        // Map each exchange (combinable/MACR) class to the comma-joined list of its
        // component tranche names. PayExchangeables/ClassesByNameOrTag expect this
        // "A1A,A1B" format (split on ',') on the exchange class's DealStructure.
        var exchangeComponents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (exchangeShares != null)
            foreach (var es in exchangeShares)
                if (!string.IsNullOrEmpty(es.ExchangeTranche) && es.Shares.Count > 0)
                    exchangeComponents[es.ExchangeTranche] =
                        string.Join(",", es.Shares.Select(s => s.TrancheName));

        var accrualClasses = AccrualClasses(waterfall, tranches);
        // Exact, as the engine looks classes up (DynamicGroup.ClassByName is ordinal).
        var byName = tranches.Where(t => !string.IsNullOrEmpty(t.TrancheName))
            .GroupBy(t => t.TrancheName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Create DealStructure for each tranche
        // An exchange class is in its components' group, or shared when they are in more than one.
        if (classGroups.Count > 0)
            foreach (var (exchange, components) in exchangeComponents)
                if (!classGroups.ContainsKey(exchange))
                    classGroups[exchange] = ExchangeGroup(exchange, components, byName, classGroups);

        return tranches.Select((t, idx) =>
        {
            var groupNum = classGroups.GetValueOrDefault(t.TrancheName, MainGroup);
            // A shared class takes its place in group 1's loss order (each group's names it).
            var writedownOrder = writedownOrders[groupNum == SharedGroup ? MainGroup : groupNum];
            var writedownIdx = writedownOrder.IndexOf(t.TrancheName);
            exchangeComponents.TryGetValue(t.TrancheName, out var exchangableTranche);
            // A class an ACCRUAL step names accrues: its coupon is added to its balance and paid
            // as principal to the classes the step directs it to (PayAccrualStructures).
            string payFrom;
            string? statedNotional = null;
            if (accrualClasses.Contains(t.TrancheName))
                payFrom = AccrualPayFrom(t);
            else if (StatedPayFrom(t, byName, exchangeComponents, PoolReferences(waterfall)) is { } stated)
                (payFrom, statedNotional) = stated;
            else
                payFrom = PayFromForTranche(t);
            // #3691: a class-cut IO (PayFrom=Notional) amortizes off its NotionalReference
            // bond — expose it as the ExchangableTranche so PayNotionalClasses reads that
            // bond's principal. (Carried on the tranche, NOT via ExchangeShares, so the
            // exchange-share pay-block doesn't also pay it — that double-counts.)
            if (payFrom == "Notional" && string.IsNullOrEmpty(exchangableTranche))
                exchangableTranche = statedNotional ?? t.NotionalReference;
            var weights = NotionalWeightsOf(t, payFrom, exchangableTranche);
            return new DealStructureDto
            {
                ClassGroupName = t.TrancheName,
                // Higher order = more junior. First in writedown list = most junior
                SubordinationOrder = writedownIdx >= 0
                    ? writedownOrder.Count - writedownIdx
                    : idx,
                PayFrom = payFrom,
                GroupNum = groupNum,
                ExchangableTranche = exchangableTranche,
                NotionalWeights = weights,
                NotionalReference = payFrom == "Exchange" && exchangeComponents.ContainsKey(t.TrancheName)
                    ? ExchangeNotional(t, byName)
                    : null
            };
        }).ToList();
    }

    /// <summary>
    ///     The classes an interest-only EXCHANGE class's notional follows, when its tranche states
    ///     them, or null. A Ginnie Mae REMIC prints an MX interest-only class's notional apart from
    ///     the notionals exchanged for it: Class IB of GNMA 2026-059 is received for all of Class
    ///     IA ($77,100,891) and half of Class AI ($28,317,000), and its Class Notional Balance is
    ///     "100% of A, VA and ZA" — $77,100,891. Following its components would cut it twice as
    ///     fast; following nothing reset it to the pool, $187,100,891, every period.
    ///     <para>
    ///         Only an interest-only class with declared exchange shares: every other exchange class
    ///         is built exactly as before, whatever it states here. Agency CRT strips (M-2AI, M-2I)
    ///         are sent as exchange classes with principal and a notionalReference the engine has
    ///         always ignored; refusing that reference turned 28 of harmony's engine tests into
    ///         400s before this was deployed.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     The share of each class a notional class follows (<see cref="TrancheDto.NotionalWeights" />),
    ///     checked against the classes it follows, or null when it states none. Refused: weights on
    ///     a class not paid on a notional, on a collateral reference ("GROUP_1", "SUBGROUP_1B"), a
    ///     class it follows with no weight (never taken as 1), a weight on a class it does not
    ///     follow, and a weight that is not a positive number.
    /// </summary>
    private static Dictionary<string, double>? NotionalWeightsOf(TrancheDto t, string payFrom, string? followed)
    {
        if (t.NotionalWeights == null || t.NotionalWeights.Count == 0)
            return null;
        if (payFrom != "Notional")
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states notionalWeights but is paid '{payFrom}', not on a notional.");
        var refs = (followed ?? "").Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (refs.FirstOrDefault(r => r.StartsWith("GROUP_", StringComparison.Ordinal) ||
                                     r.StartsWith("SUBGROUP_", StringComparison.Ordinal)) is { } pool)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states notionalWeights on '{pool}', a collateral reference: a notional " +
                "on collateral follows it from the share it began the period at, and takes no weight.");
        var missing = refs.Where(r => !t.NotionalWeights.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states notionalWeights with none for {string.Join(", ", missing)}, which " +
                "its notional follows. State every class's share; none is taken as 1.");
        var extra = t.NotionalWeights.Keys.Where(k => !refs.Contains(k)).ToList();
        if (extra.Count > 0)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states notionalWeights for {string.Join(", ", extra)}, which its " +
                $"notionalReference ('{followed}') does not name.");
        if (t.NotionalWeights.FirstOrDefault(kv => !(kv.Value > 0) || double.IsInfinity(kv.Value)) is
            { Key: not null } bad)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states a notional weight of {bad.Value} for {bad.Key}: a share is a " +
                "positive number.");
        return new Dictionary<string, double>(t.NotionalWeights);
    }

    private static string? ExchangeNotional(TrancheDto t, IReadOnlyDictionary<string, TrancheDto> byName)
    {
        var refs = (t.NotionalReference ?? "").Split(',')
            .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (refs.Count == 0 || !string.Equals(t.CashflowType, "IO", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var r in refs)
        {
            if (!byName.TryGetValue(r, out var followed))
                throw new InvalidOperationException(
                    $"'{t.TrancheName}' states a notional that follows '{r}', which is not a tranche of " +
                    "this deal (names match exactly).");
            if (string.Equals(followed.CashflowType, "IO", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{t.TrancheName}' states a notional that follows '{r}', which pays no principal, " +
                    "so the notional would never amortize. A notional follows classes with principal.");
        }

        return string.Join(",", refs);
    }

    /// <summary>
    ///     The accrual (Z) classes the ACCRUAL steps name. A step that names no class, names one the
    ///     deal does not carry, names an interest-only class, states no structure to pay the accrual
    ///     through, or names a class another ACCRUAL step already names is refused: each would leave
    ///     the class paid its coupon in cash, with no error, which is the shape a Z took before this
    ///     step existed.
    /// </summary>
    private static HashSet<string> AccrualClasses(UnifiedWaterfallDto waterfall, List<TrancheDto> tranches)
    {
        var byName = tranches.Where(t => !string.IsNullOrEmpty(t.TrancheName))
            .GroupBy(t => t.TrancheName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in GroupSteps(waterfall).SelectMany(g => g.Steps).Where(s =>
                     s.Type.Equals("ACCRUAL", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(step.Tranche))
                throw new InvalidOperationException(
                    "An ACCRUAL step names no tranche. State the accrual (Z) class in \"tranche\".");
            if (!byName.TryGetValue(step.Tranche, out var z))
                throw new InvalidOperationException(
                    $"An ACCRUAL step names '{step.Tranche}', which is not a tranche of this deal.");
            if (string.Equals(z.CashflowType, "IO", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"An ACCRUAL step names '{step.Tranche}', an interest-only class. An accrual class " +
                    "carries a principal balance that its interest is added to.");
            if (step.Structure == null)
                throw new InvalidOperationException(
                    $"The ACCRUAL step for '{step.Tranche}' states no structure. State the classes its " +
                    "Accrual Amount is paid to, in order (e.g. SEQ of the accretion-directed class, then " +
                    $"'{step.Tranche}').");
            if (!named.Add(z.TrancheName))
                throw new InvalidOperationException(
                    $"'{step.Tranche}' is named by two ACCRUAL steps. Name each accrual class once.");
        }

        return named;
    }

    /// <summary>The pay sources a tranche may state (<see cref="TrancheDto.PayFrom" />).</summary>
    private static readonly string[] StatablePayFrom = { "Sequential", "Notional", "ExcessServicing", "Exchange" };

    /// <summary>
    ///     An accrual (Z) class pays from "Accrual". It carries a principal balance, so a stated
    ///     pay source other than "Sequential" contradicts the ACCRUAL step that names it.
    /// </summary>
    private static string AccrualPayFrom(TrancheDto t)
    {
        if (!string.IsNullOrWhiteSpace(t.PayFrom) &&
            !string.Equals(t.PayFrom.Trim(), "Sequential", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{t.TrancheName}' is named by an ACCRUAL step but states payFrom '{t.PayFrom}'. An " +
                "accrual class accrues on its own balance: state \"Sequential\" or leave payFrom out.");
        return "Accrual";
    }

    /// <summary>
    ///     The pay source the tranche states, checked against what the engine will do with it,
    ///     or null when it states none. For "Notional" it also returns the reference list as the
    ///     engine is to read it (trimmed, comma-joined). Refused, never coerced: each case below
    ///     would otherwise run as something other than what was stated, with no error.
    ///     <list type="bullet">
    ///         <item>
    ///             A class with exchange components, or typed Exchanged, is paid as an exchange.
    ///             Anything else leaves it flat at its issuance balance.
    ///         </item>
    ///         <item>
    ///             What else a class may state is one table, by what it is: an interest-only class with
    ///             an excess-interest coupon states "Sequential"; one with a coupon of its own states
    ///             "Notional" or "ExcessServicing"; a class with principal states "Sequential". Only a
    ///             "Notional" may carry a notional reference.
    ///         </item>
    ///         <item>
    ///             A "Notional" follows either other classes of the deal, named exactly as the
    ///             engine looks them up, that carry principal, or the collateral group alone
    ///             ("GROUP_1", the only group a unified waterfall builds). A name the engine cannot
    ///             find leaves the notional flat at its face; a list mixing classes and the group
    ///             amortizes it twice.
    ///         </item>
    ///     </list>
    /// </summary>
    private static (string PayFrom, string? Notional)? StatedPayFrom(
        TrancheDto t, IReadOnlyDictionary<string, TrancheDto> byName,
        IReadOnlyDictionary<string, string> exchangeComponents, IReadOnlyCollection<string> poolReferences)
    {
        if (string.IsNullOrWhiteSpace(t.PayFrom))
            return null;
        var stated = StatablePayFrom.FirstOrDefault(p =>
            string.Equals(p, t.PayFrom.Trim(), StringComparison.OrdinalIgnoreCase));
        if (stated == null)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states payFrom '{t.PayFrom}'. State one of: " +
                string.Join(", ", StatablePayFrom) + ".");

        var isExchange = exchangeComponents.ContainsKey(t.TrancheName) ||
                         string.Equals(t.TrancheType, "Exchanged", StringComparison.OrdinalIgnoreCase);
        if (isExchange != (stated == "Exchange"))
            throw new InvalidOperationException(isExchange
                ? $"'{t.TrancheName}' is an exchange class but states payFrom '{stated}'. An exchange " +
                  "class mirrors its components: state \"Exchange\"."
                : $"'{t.TrancheName}' states payFrom 'Exchange' but no exchange shares name its components.");

        if (isExchange)
            return (stated, null);

        // What the class may state, by what it is (one table, not a check per bad combination):
        //   interest-only, excess-interest coupon -> Sequential: the leftover interest, on the pool
        //   interest-only, any other coupon       -> Notional (on what it follows) or ExcessServicing
        //   with principal                        -> Sequential
        // An interest-only class paid Sequential is reset to the pool every period, whatever its
        // face (DynamicGroup.NotionalClasses): that is the excess-interest strip's convention, and
        // for any other interest-only class it would silently replace its notional with the pool.
        var isIo = string.Equals(t.CashflowType, "IO", StringComparison.OrdinalIgnoreCase);
        var (allowed, what) = !isIo
            ? (new[] { "Sequential" }, $"has principal (cashflowType '{t.CashflowType}')")
            : IsExcessInterest(t)
                ? (new[] { "Sequential" }, "is interest-only with an excess interest coupon: it takes the " +
                                          "interest left over, on the pool")
                : (new[] { "Notional", "ExcessServicing" }, "is interest-only with a coupon of its own: " +
                                                            "it is paid on a notional or out of the servicing fee");
        if (!allowed.Contains(stated))
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states payFrom '{stated}' but {what}. State " +
                string.Join(" or ", allowed.Select(p => $"\"{p}\"")) + ".");

        var refs = (t.NotionalReference ?? "").Split(',')
            .Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (stated != "Notional" && refs.Count > 0)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states payFrom '{stated}' but names a notional reference " +
                $"('{t.NotionalReference}'), which only a \"Notional\" follows. Its reference would be " +
                "dropped with no error.");

        if (stated != "Notional")
            return (stated, null);

        if (refs.Count == 0)
            throw new InvalidOperationException(
                $"'{t.TrancheName}' states payFrom 'Notional' but names nothing its notional follows. " +
                "State the classes (\"A1,A2\") or the collateral group (\"GROUP_1\") in notionalReference.");
        // A subgroup's collateral ("SUBGROUP_1B", the collateral stated by subgroup): alone, as a
        // group's is; that the collateral states it is checked when the run reads its rows.
        if (refs.FirstOrDefault(r => r.StartsWith("SUBGROUP_", StringComparison.Ordinal) && r.Length > 9) is { } sub)
        {
            if (refs.Count > 1)
                throw new InvalidOperationException(
                    $"'{t.TrancheName}' states a notional on '{t.NotionalReference}', which mixes a " +
                    $"subgroup's collateral with other references. State \"{sub}\" alone.");
            return (stated, sub);
        }

        if (refs.FirstOrDefault(poolReferences.Contains) is { } pool)
        {
            if (refs.Count > 1)
                throw new InvalidOperationException(
                    $"'{t.TrancheName}' states a notional on '{t.NotionalReference}', which mixes the " +
                    $"collateral group with classes. State the classes, or \"{pool}\" alone.");
            return (stated, pool);
        }

        foreach (var r in refs)
        {
            if (!byName.TryGetValue(r, out var followed))
                throw new InvalidOperationException(
                    $"'{t.TrancheName}' states a notional that follows '{r}', which is not a tranche of " +
                    $"this deal (names match exactly) or a collateral group " +
                    $"(\"{string.Join("\", \"", poolReferences)}\").");
            if (string.Equals(r, t.TrancheName, StringComparison.Ordinal) ||
                string.Equals(followed.CashflowType, "IO", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{t.TrancheName}' states a notional that follows '{r}', which pays no principal, " +
                    "so the notional would never amortize. A notional follows classes with principal.");
        }

        return (stated, string.Join(",", refs));
    }

    /// <summary>The group a unified waterfall's own <see cref="UnifiedWaterfallDto.Steps" /> build.</summary>
    private const string MainGroup = "1";

    /// <summary>The step types a group of <see cref="UnifiedWaterfallDto.Groups" /> may state.</summary>
    private static readonly string[] GroupStepTypes = { "INTEREST", "PRINCIPAL", "WRITEDOWN", "ACCRUAL", "PREPAYMENT_PENALTY" };

    /// <summary>
    ///     Every group's steps, group "1" (the waterfall's own <see cref="UnifiedWaterfallDto.Steps" />)
    ///     first, then <see cref="UnifiedWaterfallDto.Groups" /> in the order stated.
    /// </summary>
    public static IEnumerable<GroupWaterfallDto> GroupSteps(UnifiedWaterfallDto waterfall)
    {
        yield return new GroupWaterfallDto { GroupNum = MainGroup, Steps = waterfall.Steps };
        foreach (var g in waterfall.Groups ?? new List<GroupWaterfallDto>())
            yield return g;
    }

    /// <summary>The collateral groups a notional may follow: "GROUP_1", and "GROUP_n" for each stated group.</summary>
    private static List<string> PoolReferences(UnifiedWaterfallDto waterfall) =>
        GroupSteps(waterfall).Select(g => $"GROUP_{g.GroupNum}").ToList();

    /// <summary>
    ///     {class: the group whose steps name it}, for a waterfall that states
    ///     <see cref="UnifiedWaterfallDto.Groups" /> (empty otherwise: every class is group "1"'s, as
    ///     before). A class two groups' steps name is refused: it would be paid from one group's
    ///     collateral and named in the other's priorities, which then pay nothing to it.
    /// </summary>
    public static Dictionary<string, string> ClassGroups(UnifiedWaterfallDto waterfall)
    {
        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        if (waterfall.Groups == null || waterfall.Groups.Count == 0)
            return groupOf;
        var namedBy = new Dictionary<string, List<GroupWaterfallDto>>(StringComparer.Ordinal);
        foreach (var g in GroupSteps(waterfall))
            foreach (var name in g.Steps.SelectMany(StepClasses).Distinct(StringComparer.Ordinal))
            {
                if (!namedBy.TryGetValue(name, out var list))
                    namedBy[name] = list = new List<GroupWaterfallDto>();
                list.Add(g);
            }

        foreach (var (name, groups) in namedBy)
        {
            if (groups.Count == 1)
            {
                groupOf[name] = groups[0].GroupNum;
                continue;
            }

            var unstated = groups.Where(g => g.GroupNum != MainGroup && !g.SharesClasses)
                .Select(g => g.GroupNum).ToList();
            if (unstated.Count > 0)
                throw new InvalidOperationException(
                    $"'{name}' is named by the steps of groups {string.Join(" and ", groups.Select(g => g.GroupNum))}. " +
                    "A class is paid from one collateral group: name it in that group's steps only, or " +
                    $"state \"sharesClasses\": true on group {string.Join(" and ", unstated)} to share it.");
            groupOf[name] = SharedGroup;
        }

        return groupOf;
    }

    /// <summary>The engine's group number for a class every group holds (a shared class).</summary>
    private const string SharedGroup = "0";

    /// <summary>The groups whose interest is posted with group 1's (<see cref="GroupWaterfallDto.Interest" />).</summary>
    public static List<string> PooledInterestGroups(UnifiedWaterfallDto waterfall) =>
        (waterfall.Groups ?? new List<GroupWaterfallDto>())
        .Where(g => string.Equals(g.Interest, "pooled", StringComparison.OrdinalIgnoreCase))
        .Select(g => g.GroupNum).ToList();

    /// <summary>
    ///     The group an exchange class is in: its components' (and, for an interest-only one, the
    ///     classes its notional follows). An exchange class whose components are in more than one
    ///     group, or are shared, is shared itself (<see cref="SharedGroup" />): it is settled once a
    ///     period, after every group has paid, from its components wherever they are (an agency REMIC's
    ///     MX class received for classes of two Security Groups).
    /// </summary>
    private static string ExchangeGroup(string exchange, string components,
        IReadOnlyDictionary<string, TrancheDto> byName, IReadOnlyDictionary<string, string> classGroups)
    {
        var names = components.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (byName.TryGetValue(exchange, out var t) && ExchangeNotional(t, byName) is { } notional)
            names.AddRange(notional.Split(','));
        var groups = names.Select(n => classGroups.GetValueOrDefault(n, MainGroup)).Distinct().ToList();
        return groups.Count == 1 ? groups.Single() : SharedGroup;
    }

    /// <summary>Every class a step names: its structures, its accrual class, its penalty shares.</summary>
    private static IEnumerable<string> StepClasses(WaterfallStepDto step)
    {
        var structures = new List<PayableStructureDto?> { step.Structure, step.Default, step.OnTriggerFail?.Structure };
        structures.AddRange((step.Rules ?? new List<WaterfallRuleDto>()).Select(r => r.Structure));
        foreach (var structure in structures.Where(x => x != null))
            foreach (var name in ExtractTrancheOrder(structure!))
                yield return name;
        if (!string.IsNullOrWhiteSpace(step.Tranche))
            yield return step.Tranche!;
        foreach (var share in step.Shares ?? new List<PrepaymentPenaltyShareDto>())
            if (!string.IsNullOrWhiteSpace(share.Tranche))
                yield return share.Tranche!;
    }

    /// <summary>
    ///     Refuse <see cref="UnifiedWaterfallDto.Groups" /> it cannot run: a group numbered other than
    ///     a whole number past 1, numbered twice, missing a required step, or stating a step type a
    ///     group's own priorities do not build (those are the deal's, read from
    ///     <see cref="UnifiedWaterfallDto.Steps" /> only, so in a group they would be dropped with no
    ///     error). Only the ComposableStructure engine (an execution order, or that waterfall type)
    ///     runs them.
    /// </summary>
    public static void ValidateGroups(UnifiedWaterfallDto waterfall, string dealName, bool composable)
    {
        if (waterfall.Groups == null || waterfall.Groups.Count == 0)
            return;
        if (!composable)
            throw new InvalidOperationException(
                $"Deal {dealName}: collateral groups are run by the ComposableStructure engine only: state " +
                "an executionOrder or waterfallType \"ComposableStructure\".");
        var seen = new HashSet<string>(StringComparer.Ordinal) { MainGroup };
        foreach (var g in waterfall.Groups)
        {
            if (!int.TryParse(g.GroupNum, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 2 ||
                n.ToString(System.Globalization.CultureInfo.InvariantCulture) != g.GroupNum)
                throw new InvalidOperationException(
                    $"Deal {dealName}: a collateral group is numbered '{g.GroupNum}'. Number each group past " +
                    "the first 2, 3, ... as its collateral rows carry it; the waterfall's own steps are group 1's.");
            if (!seen.Add(g.GroupNum))
                throw new InvalidOperationException(
                    $"Deal {dealName}: collateral group {g.GroupNum} is stated twice.");
            var other = g.Steps.Select(st => st.Type.ToUpperInvariant())
                .Where(t => !GroupStepTypes.Contains(t)).Distinct().ToList();
            if (other.Count > 0)
                throw new InvalidOperationException(
                    $"Deal {dealName}: collateral group {g.GroupNum} states {string.Join(", ", other)}, which a " +
                    $"group does not build. A group states {string.Join(", ", GroupStepTypes)}.");
            ValidateSteps(new UnifiedWaterfallDto { Steps = g.Steps }, $"{dealName} group {g.GroupNum}");
            if (g.Interest != null && !string.Equals(g.Interest, "own", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g.Interest, "pooled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Deal {dealName}: collateral group {g.GroupNum} states interest '{g.Interest}'. State " +
                    "\"own\" (its INTEREST step pays its interest) or \"pooled\" (paid with group 1's).");
            if (string.Equals(g.Interest, "pooled", StringComparison.OrdinalIgnoreCase))
            {
                // Its interest is posted in group 1's row: a class only its own INTEREST step pays
                // would be paid from nothing.
                var mainInterest = waterfall.Steps.Where(st => st.Type.Equals("INTEREST", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(StepClasses).ToHashSet(StringComparer.Ordinal);
                var own = g.Steps.Where(st => st.Type.Equals("INTEREST", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(StepClasses).Where(n => !mainInterest.Contains(n)).Distinct().ToList();
                if (own.Count > 0)
                    throw new InvalidOperationException(
                        $"Deal {dealName}: collateral group {g.GroupNum} pools its interest with group 1, but its " +
                        $"INTEREST step pays {string.Join(", ", own)}, which group 1's INTEREST step does not: " +
                        "they would be paid from nothing. Pay them in group 1's INTEREST step, or keep the " +
                        "group's interest its own.");
            }
        }
    }

    /// <summary>
    ///     The pay rules of <see cref="UnifiedWaterfallDto.Groups" />: each group's steps built as
    ///     <see cref="BuildPayRules" /> builds the deal's, for "GROUP_n" (the engine runs a group's
    ///     rules on its own collateral only), numbered after <paramref name="after" />.
    /// </summary>
    public static List<PayRuleDto> BuildGroupPayRules(UnifiedWaterfallDto waterfall, int after)
    {
        var rules = new List<PayRuleDto>();
        foreach (var g in waterfall.Groups ?? new List<GroupWaterfallDto>())
            foreach (var rule in BuildPayRules(new UnifiedWaterfallDto { Steps = g.Steps }, $"GROUP_{g.GroupNum}"))
            {
                rule.Priority = after + 1 + rules.Count;
                rules.Add(rule);
            }

        return rules;
    }

    private static bool IsExcessInterest(TrancheDto t) =>
        string.Equals(t.CouponType, "ExcessInterest", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.CouponType, "ResidualInterest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Refuse a request whose tranches state <see cref="TrancheDto.PayFrom" /> where it is not
    ///     read: only <see cref="BuildDealStructures" /> reads it, and a request that gives its deal
    ///     structures (or class groups) itself, or has no unified waterfall, never calls it. The
    ///     statement would be dropped with no error.
    /// </summary>
    public static void RefuseUnreadPayFrom(
        IEnumerable<TrancheDto> tranches, UnifiedWaterfallDto? waterfall, bool explicitStructures)
    {
        if (waterfall != null && !explicitStructures)
            return;
        var stating = tranches.Where(t => !string.IsNullOrWhiteSpace(t.PayFrom))
            .Select(t => t.TrancheName).ToList();
        if (stating.Count == 0)
            return;
        var why = explicitStructures
            ? "this request states its deal structures itself, so state the pay source there"
            : "this request has no unifiedWaterfall, and only a unified waterfall reads it";
        throw new InvalidOperationException(
            $"'{string.Join("', '", stating)}' state payFrom, but {why}.");
    }

    /// <summary>
    ///     Pick the DealStructure pay source for a tranche that states none. Tranches default to
    ///     "Sequential" (the unified-waterfall steps drive their order), but an
    ///     excess-servicing IO strip (Class A-IO-S) must pay from
    ///     "ExcessServicing" so it draws its strip from the servicing fee —
    ///     capped at the fee collected and WITHOUT reducing interest to the
    ///     offered classes (see <c>TrancheAllocator</c>). It is identified as a
    ///     non-residual IO strip that is either explicitly a Reference tranche
    ///     or carries an "IOS" class name; the excess-SPREAD strip (Class XS)
    ///     is a ResidualInterest IO and is deliberately excluded so it keeps
    ///     the interest sweep. An <c>Exchanged</c> (combinable / MACR) class pays
    ///     from "Exchange" so the exchange overlay (<c>PayExchangeables</c>) derives
    ///     its cashflow from its component tranches instead of leaving it flat at
    ///     its issuance balance.
    /// </summary>
    private static string PayFromForTranche(TrancheDto t)
    {
        var isIo = string.Equals(t.CashflowType, "IO", StringComparison.OrdinalIgnoreCase);
        // The monthly-excess-cashflow (XS) strip keeps the interest sweep and must not be
        // re-routed to ExcessServicing/Notional. Accept both the new "ExcessInterest" type
        // and the legacy "ResidualInterest" string.
        var isResidualInterest = IsExcessInterest(t);
        var name = (t.TrancheName ?? "").ToUpperInvariant().Replace("-", "");
        var isReference =
            string.Equals(t.TrancheType, "Reference", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(t.TrancheType, "Exchanged", StringComparison.OrdinalIgnoreCase))
            return "Exchange";

        if (isIo && !isResidualInterest && (isReference || name.Contains("IOS")))
            return "ExcessServicing";

        // #3691: a class-cut IO strip carries a NotionalReference — the funded bond its
        // notional is cut off (A-1AX -> A-1A). Pay from Notional so PayNotionalClasses
        // amortizes it off that bond (via the ExchangableTranche set from NotionalReference
        // above) instead of the pool fallback. Kept BELOW the IOS/Reference case so an
        // A-IO-S excess-servicing pool strip is unaffected.
        if (isIo && !isResidualInterest && !string.IsNullOrEmpty(t.NotionalReference))
            return "Notional";

        return "Sequential";
    }

    /// <summary>
    ///     Generates PayRule DTOs from a unified waterfall definition
    /// </summary>
    public static List<PayRuleDto> BuildPayRules(UnifiedWaterfallDto waterfall, string groupName = "GROUP_1")
    {
        var rules = new List<PayRuleDto>();
        var priority = 0;

        // Emit computed variable rules first (they run before structure selection)
        if (waterfall.ComputedVariables != null && waterfall.ComputedVariables.Count > 0)
            rules.AddRange(BuildComputedVariableRules(waterfall.ComputedVariables, groupName, ref priority));

        // Track principal structures for "useStructure" references
        var principalStructures = new Dictionary<string, WaterfallStepDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in waterfall.Steps)
            switch (step.Type.ToUpperInvariant())
            {
                case "INTEREST":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "InterestStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_INTEREST_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "PRINCIPAL":
                    var source = step.Source?.ToLower() ?? "scheduled";
                    var setFunc = source switch
                    {
                        "scheduled" => "SET_SCHED_STRUCT",
                        "unscheduled" => "SET_PREPAY_STRUCT",
                        "recovery" => "SET_RECOV_STRUCT",
                        _ => "SET_SCHED_STRUCT"
                    };
                    var prefix = source switch
                    {
                        "scheduled" => "Sched",
                        "unscheduled" => "Prepay",
                        "recovery" => "Recov",
                        _ => "Prin"
                    };

                    // Handle useStructure reference
                    var effectiveStep = step;
                    if (!string.IsNullOrEmpty(step.UseStructure) &&
                        principalStructures.TryGetValue(step.UseStructure, out var refStep))
                        effectiveStep = refStep;

                    // Store this step for potential future references
                    if (effectiveStep.Default != null) principalStructures[source] = effectiveStep;

                    // Generate rules (with trigger conditions if present).
                    // Prefix is derived from the *current* step's source, not effectiveStep's,
                    // so useStructure references produce distinct rule names per source.
                    rules.AddRange(BuildPrincipalStepRules(effectiveStep, setFunc, prefix, groupName, ref priority));
                    break;

                case "WRITEDOWN":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "WritedownStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_WRITEDOWN_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "EXCESS":
                    // EXCESS step defines where excess spread accretes (typically OC/residual tranche)
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "ExcessStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_EXCESS_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "EXCESS_TURBO":
                    // EXCESS_TURBO pays down notes up to OC shortfall
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "TurboStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_TURBO_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "EXCESS_RELEASE":
                    // EXCESS_RELEASE releases remainder to certificates
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "ReleaseStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_RELEASE_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "CAP_CARRYOVER":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "CapCarryoverStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_CAP_CARRYOVER_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "MODIFICATION_LOSS":
                    rules.AddRange(BuildModificationLossRules(step, groupName, ref priority));
                    break;

                case "PREPAYMENT_PENALTY":
                    rules.Add(BuildPrepaymentPenaltyRule(step, groupName, ref priority));
                    break;

                case "ACCRUAL":
                    // Where this Z's Accrual Amount is paid (BuildDealStructures has already
                    // refused a step without a tranche or a structure).
                    if (step.Structure != null && !string.IsNullOrWhiteSpace(step.Tranche))
                        rules.Add(new PayRuleDto
                        {
                            RuleName = $"AccrualStruct_{step.Tranche}",
                            ClassGroupName = groupName,
                            Formula =
                                $"SET_SPEC_ACC_STRUCT('{step.Tranche}', {WaterfallBuilder.BuildStructureDsl(step.Structure)})",
                            Priority = priority++
                        });
                    break;

                case "SUPPLEMENTAL_REDUCTION":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "SupplStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_SUPPL_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    if (!string.IsNullOrEmpty(step.CapVariable) && step.OfferedTranches != null && step.SeniorTranches != null)
                    {
                        var subList = string.Join(",", step.OfferedTranches);
                        var senList = string.Join(",", step.SeniorTranches);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "SupplConfig",
                            ClassGroupName = groupName,
                            Formula = $"SET_SUPPL_CONFIG('{step.CapVariable}', '{subList}', '{senList}')",
                            Priority = priority++
                        });
                    }

                    break;
            }

        return rules;
    }

    /// <summary>
    ///     Builds PayRules for a PRINCIPAL step, handling trigger conditions.
    ///     Supports both legacy Default/OnTriggerFail and new multi-branch Rules array.
    /// </summary>
    private static List<PayRuleDto> BuildPrincipalStepRules(
        WaterfallStepDto step,
        string setStructFunc,
        string prefix,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();

        // Multi-branch rules array (new format for complex deals like STACR)
        if (step.Rules != null && step.Rules.Count > 0)
        {
            rules.AddRange(BuildMultiBranchRules(step.Rules, prefix, setStructFunc, groupName, ref priority));
            return rules;
        }

        // Legacy: simple Default/OnTriggerFail two-branch model
        if (step.OnTriggerFail != null && step.Default != null)
        {
            var triggerNames = string.Join(",", step.OnTriggerFail.Triggers);
            var passedDsl = WaterfallBuilder.BuildStructureDsl(step.Default);
            var failedDsl = WaterfallBuilder.BuildStructureDsl(step.OnTriggerFail.Structure!);

            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}PrinPass",
                ClassGroupName = groupName,
                Formula = $"if (PASSED('{triggerNames}')) {setStructFunc}({passedDsl})",
                Priority = priority++
            });

            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}PrinFail",
                ClassGroupName = groupName,
                Formula = $"if (FAILED('{triggerNames}')) {setStructFunc}({failedDsl})",
                Priority = priority++
            });
        }
        else if (step.Default != null)
        {
            var structDsl = WaterfallBuilder.BuildStructureDsl(step.Default);
            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}Struct",
                ClassGroupName = groupName,
                Formula = $"{setStructFunc}({structDsl})",
                Priority = priority++
            });
        }
        else if (step.Structure != null)
        {
            // Fallback: use Structure directly (e.g., recovery with unconditional structure)
            var structDsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}Struct",
                ClassGroupName = groupName,
                Formula = $"{setStructFunc}({structDsl})",
                Priority = priority++
            });
        }

        return rules;
    }

    /// <summary>
    ///     Builds PayRules from a multi-branch rules array.
    ///     Each rule becomes a separate PayRule with its condition compiled to DSL.
    ///     Rules are emitted in order - last matching rule wins (Payscen convention).
    ///
    ///     Fallback rules (no <c>When</c>) are guarded with the negation of all prior
    ///     conditions so they only fire when none of the prior conditional rules
    ///     matched. Without this guard the fallback's unconditional formula would
    ///     execute every period and overwrite whatever the prior conditional rules
    ///     just set (issue #9, exposed by graam-harmony#1054 where the sequential
    ///     fallback overwrote the conditional PRORATA seniors). Mirrors the same
    ///     negation pattern used by <see cref="BuildComputedVariableRules"/>.
    /// </summary>
    private static List<PayRuleDto> BuildMultiBranchRules(
        List<WaterfallRuleDto> branchRules,
        string prefix,
        string setStructFunc,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();
        var priorConditions = new List<string>();

        for (var i = 0; i < branchRules.Count; i++)
        {
            var branch = branchRules[i];
            var structDsl = WaterfallBuilder.BuildStructureDsl(branch.Structure);
            var formula = $"{setStructFunc}({structDsl})";

            if (branch.When != null)
            {
                var condition = BuildConditionExpression(branch.When);
                formula = $"if ({condition}) {formula}";
                priorConditions.Add(condition);
            }
            else if (priorConditions.Count > 0)
            {
                // Fallback rule: negate all prior conditions so it only fires
                // when none of the prior conditional rules matched.
                var negation = string.Join(" && ", priorConditions.Select(c => $"!({c})"));
                formula = $"if ({negation}) {formula}";
            }

            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}Rule{i}",
                ClassGroupName = groupName,
                Formula = formula,
                Priority = priority++
            });
        }

        return rules;
    }

    /// <summary>
    ///     Converts a RuleConditionDto to a DSL condition expression string.
    ///     All conditions are ANDed together.
    /// </summary>
    private static string BuildConditionExpression(RuleConditionDto condition)
    {
        var parts = new List<string>();

        if (condition.Pass != null && condition.Pass.Count > 0)
            parts.Add($"PASSED('{string.Join(",", condition.Pass)}')");

        if (condition.Fail != null && condition.Fail.Count > 0)
            parts.Add($"FAILED('{string.Join(",", condition.Fail)}')");

        if (condition.Vars != null)
        {
            foreach (var vc in condition.Vars)
            {
                if (vc.Op is not (">" or "<" or ">=" or "<=" or "==" or "!="))
                    throw new ArgumentException($"Unknown comparison operator: '{vc.Op}'");
                parts.Add($"VAR('{vc.Var}') {vc.Op} {vc.Value}");
            }
        }

        return string.Join(" && ", parts);
    }

    /// <summary>
    ///     Builds PayRules for computed variables (evaluated before waterfall each period).
    ///     Since all pay rules execute (last matching wins), fallback rules (no "when")
    ///     must be guarded with the negation of preceding conditions to avoid overwriting.
    /// </summary>
    public static List<PayRuleDto> BuildComputedVariableRules(
        List<ComputedVariableDto> computedVars,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();

        foreach (var cv in computedVars)
        {
            // Collect all conditions from prior rules to build negation for fallback
            var priorConditions = new List<string>();

            for (var i = 0; i < cv.Rules.Count; i++)
            {
                var rule = cv.Rules[i];
                var formula = $"SET_VAR('{cv.Name}', {rule.Formula})";

                if (rule.When != null)
                {
                    var condition = BuildConditionExpression(rule.When);
                    formula = $"if ({condition}) {formula}";
                    priorConditions.Add(condition);
                }
                else if (priorConditions.Count > 0)
                {
                    // Fallback rule: negate all prior conditions so it only fires
                    // when none of the prior conditional rules matched.
                    var negation = string.Join(" && ", priorConditions.Select(c => $"!({c})"));
                    formula = $"if ({negation}) {formula}";
                }

                rules.Add(new PayRuleDto
                {
                    RuleName = $"ComputeVar_{cv.Name}_{i}",
                    ClassGroupName = groupName,
                    Formula = formula,
                    Priority = priority++
                });
            }
        }

        return rules;
    }

    /// <summary>
    ///     Extracts tranche names in order from a payable structure (depth-first)
    /// </summary>
    public static List<string> ExtractTrancheOrder(PayableStructureDto structure)
    {
        var tranches = new List<string>();
        ExtractTranchesRecursive(structure, tranches);
        return tranches;
    }

    private static void ExtractTranchesRecursive(PayableStructureDto structure, List<string> tranches)
    {
        // Handle SINGLE type
        if (structure.Type.Equals("SINGLE", StringComparison.OrdinalIgnoreCase))
        {
            var tranche = structure.Tranche ?? structure.Tranches?.FirstOrDefault();
            if (!string.IsNullOrEmpty(tranche)) tranches.Add(tranche);
            return;
        }

        // Handle shorthand Tranches list
        if (structure.Tranches != null && structure.Tranches.Count > 0) tranches.AddRange(structure.Tranches);

        // Handle Children
        if (structure.Children != null)
            foreach (var child in structure.Children)
                ExtractTranchesRecursive(child, tranches);

        // Handle SHIFTI seniors/subordinates
        if (structure.Seniors != null) ExtractTranchesRecursive(structure.Seniors, tranches);
        if (structure.Subordinates != null) ExtractTranchesRecursive(structure.Subordinates, tranches);

        // Handle CSCAP primary/cap
        if (structure.Primary != null) ExtractTranchesRecursive(structure.Primary, tranches);
        if (structure.Cap != null) ExtractTranchesRecursive(structure.Cap, tranches);

        // Handle FIXED primary/overflow
        if (structure.Overflow != null) ExtractTranchesRecursive(structure.Overflow, tranches);

        // Handle FORCE_PAYDOWN forced/support (and PAC senior/support — senior first)
        if (structure.Forced != null) ExtractTranchesRecursive(structure.Forced, tranches);
        if (structure.Senior != null) ExtractTranchesRecursive(structure.Senior, tranches);
        if (structure.Support != null) ExtractTranchesRecursive(structure.Support, tranches);
    }

    /// <summary>
    ///     The rule for a PREPAYMENT_PENALTY step: one share per class it names. A step with no
    ///     share, a share naming no class, or a share outside (0, 100] is refused here; a class the
    ///     deal does not carry and shares that do not total 100% are refused by the engine
    ///     (SET_PENALTY_STRUCT), which resolves the names.
    /// </summary>
    private static PayRuleDto BuildPrepaymentPenaltyRule(WaterfallStepDto step, string groupName, ref int priority)
    {
        if (step.Shares == null || step.Shares.Count == 0)
            throw new InvalidOperationException(
                "A PREPAYMENT_PENALTY step requires a non-empty 'shares' list: the classes the " +
                "prepayment penalties are paid to, each its percent.");
        var shareDsl = new List<string>();
        for (var i = 0; i < step.Shares.Count; i++)
        {
            var share = step.Shares[i];
            if (string.IsNullOrWhiteSpace(share.Tranche))
                throw new InvalidOperationException($"PREPAYMENT_PENALTY share {i + 1} names no tranche.");
            if (!double.IsFinite(share.Pct) || share.Pct <= 0 || share.Pct > 100)
                throw new InvalidOperationException(
                    $"PREPAYMENT_PENALTY share {i + 1} ('{share.Tranche}') is {share.Pct}%; a share is " +
                    "more than 0 and at most 100.");
            shareDsl.Add(
                $"PENALTY_SHARE(SINGLE('{share.Tranche}'), {share.Pct.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
        }

        return new PayRuleDto
        {
            RuleName = "PenaltyStruct",
            ClassGroupName = groupName,
            Formula = $"SET_PENALTY_STRUCT({string.Join(", ", shareDsl)})",
            Priority = priority++
        };
    }

    /// <summary>
    ///     Builds the rules for a MODIFICATION_LOSS step: one rung per stated priority, in
    ///     document order, plus the optional write-up target.
    ///
    ///     REJECTS a malformed rung rather than skipping it. A dropped priority does not fail —
    ///     it shifts every amount below it up the ladder and quietly changes the answer, which is
    ///     the failure mode this whole step exists to remove.
    /// </summary>
    private static List<PayRuleDto> BuildModificationLossRules(WaterfallStepDto step, string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();
        if (step.Rungs == null || step.Rungs.Count == 0)
            throw new InvalidOperationException(
                "A MODIFICATION_LOSS step requires a non-empty 'rungs' list — the Modification " +
                "Loss Priority, in document order.");

        var rungDsl = new List<string>();
        for (var i = 0; i < step.Rungs.Count; i++)
        {
            var rung = step.Rungs[i];
            var effect = (rung.Effect ?? "").Trim().ToUpperInvariant();
            if (effect != "NOTIONAL" && effect != "INTEREST")
                throw new InvalidOperationException(
                    $"MODIFICATION_LOSS rung {i + 1} has effect '{rung.Effect}'; expected " +
                    "'NOTIONAL' (reduce the Class Notional Amount) or 'INTEREST' (reduce the " +
                    "Interest Accrual Amount).");

            var names = !string.IsNullOrWhiteSpace(rung.Tranche)
                ? new List<string> { rung.Tranche! }
                : rung.Tranches ?? new List<string>();
            if (names.Count == 0)
                throw new InvalidOperationException(
                    $"MODIFICATION_LOSS rung {i + 1} names no tranche; set 'tranche' for a " +
                    "single-class priority or 'tranches' for a pro-rata one.");

            var target = names.Count == 1
                ? $"SINGLE('{names[0]}')"
                : $"PRORATA('{string.Join("','", names)}')";

            if (effect == "NOTIONAL")
            {
                rungDsl.Add($"ML_NOTIONAL({target}, {names.Count})");
                continue;
            }

            // A pro-rata interest priority is capped on ONE member's Interest Accrual Amount, so
            // an ambiguous rung is rejected rather than guessed at: picking the first name would
            // silently size the rung off the retained H class on a roster ordered the other way.
            if (names.Count > 1 && string.IsNullOrWhiteSpace(rung.CapTranche))
                throw new InvalidOperationException(
                    $"MODIFICATION_LOSS rung {i + 1} is a pro-rata INTEREST priority over " +
                    $"{string.Join(", ", names)} but states no 'capTranche'. The document caps " +
                    "such a priority at ONE named class's Interest Accrual Amount.");

            var capClass = string.IsNullOrWhiteSpace(rung.CapTranche) ? names[0] : rung.CapTranche!;
            if (!names.Contains(capClass, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"MODIFICATION_LOSS rung {i + 1} caps on '{capClass}', which is not one of " +
                    $"the classes it allocates to ({string.Join(", ", names)}).");

            rungDsl.Add($"ML_INTEREST({target}, '{capClass}', {names.Count})");
        }

        rules.Add(new PayRuleDto
        {
            RuleName = "ModLossStruct",
            ClassGroupName = groupName,
            Formula = $"SET_MODLOSS_STRUCT({string.Join(", ", rungDsl)})",
            Priority = priority++
        });

        if (!string.IsNullOrWhiteSpace(step.WriteUpTranche))
            rules.Add(new PayRuleDto
            {
                RuleName = "ModLossWriteup",
                ClassGroupName = groupName,
                Formula = $"SET_MODLOSS_WRITEUP('{step.WriteUpTranche}')",
                Priority = priority++
            });

        return rules;
    }

    /// <summary>
    ///     Validates that required steps are present in the unified waterfall
    /// </summary>
    public static bool DeclaresReserveDeposit(UnifiedWaterfallDto waterfall) =>
        waterfall.Steps.Any(s => s.Type.Equals("RESERVE_DEPOSIT", StringComparison.OrdinalIgnoreCase));

    public static void ValidateSteps(UnifiedWaterfallDto waterfall, string dealName)
    {
        var stepTypes = waterfall.Steps.Select(s => s.Type.ToUpperInvariant()).ToHashSet();

        if (!stepTypes.Contains("INTEREST"))
            throw new InvalidOperationException(
                $"Deal {dealName}: UnifiedStructure requires INTEREST step in waterfall");

        if (!stepTypes.Contains("WRITEDOWN"))
            throw new InvalidOperationException(
                $"Deal {dealName}: UnifiedStructure requires WRITEDOWN step in waterfall");

        var hasPrincipal = waterfall.Steps.Any(s =>
            s.Type.Equals("PRINCIPAL", StringComparison.OrdinalIgnoreCase) &&
            (s.Source?.Equals("scheduled", StringComparison.OrdinalIgnoreCase) ?? true));

        if (!hasPrincipal)
            throw new InvalidOperationException(
                $"Deal {dealName}: UnifiedStructure requires PRINCIPAL (scheduled) step in waterfall");
    }
}