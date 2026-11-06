namespace GraamFlows.Revolving;

/// <summary>Refuses a run by name before any month is run.</summary>
internal static class RevolvingValidator
{
    public static void Validate(RevolvingRun run)
    {
        var program = run.Program;
        if (string.IsNullOrWhiteSpace(program.Name))
            throw new RevolvingConfigException("A revolving program must state its name.");

        if (program.PrincipalFreeze == null)
            throw new RevolvingConfigException($"Program '{program.Name}' states no principalFreeze.");

        ValidateRun(run);
        ValidateSteps(program.Name, "group", program.GroupSteps, WaterfallVerbs.RunsOnGroup);
        ValidateSteps(program.Name, "finance", program.FinanceSteps, WaterfallVerbs.RunsOnFinance);
        ValidateSteps(program.Name, "principal", program.PrincipalSteps, WaterfallVerbs.RunsOnPrincipal);
        foreach (var test in program.Triggers)
            ValidateTrigger(program, test);

        var names = new HashSet<string>();
        foreach (var series in run.Series)
        {
            if (!names.Add(series.Name))
                throw new RevolvingConfigException($"Series '{series.Name}' appears twice in the run.");
            ValidateSeries(program, series);
        }
    }

    private static void ValidateRun(RevolvingRun run)
    {
        var statement = run.StatementDate;
        if (statement.Day != DateTime.DaysInMonth(statement.Year, statement.Month))
            throw new RevolvingConfigException(
                $"statementDate {statement:yyyy-MM-dd} is not a month end. It must be the record date of "
                + "a closed monthly period.");

        if (run.DistributionDay is < 1 or > 28)
            throw new RevolvingConfigException($"distributionDay {run.DistributionDay} must be from 1 to 28.");

        if (run.Periods < 1)
            throw new RevolvingConfigException("A revolving run needs at least one period.");

        if (run.Series.Count == 0)
            throw new RevolvingConfigException("A revolving run needs at least one series.");

        if (run.Periods > run.ActualPeriods.Count && run.Drivers == null)
            throw new RevolvingConfigException(
                $"The run asks for {run.Periods} periods with {run.ActualPeriods.Count} actual periods and "
                + "no drivers to forecast the rest.");

        if (run.Drivers is { HoldPool: true, NewReceivablesRate: not null })
            throw new RevolvingConfigException("State holdPool or newReceivablesRate, not both.");

        if (run.Drivers is { HoldPool: false, NewReceivablesRate: null })
            throw new RevolvingConfigException("Forecast drivers must state newReceivablesRate or holdPool.");
    }

    private static void ValidateSteps(string program, string list, IReadOnlyList<WaterfallStep> steps,
        Func<WaterfallVerb, bool> belongs)
    {
        foreach (var step in steps)
        {
            if (!belongs(step.Verb))
                throw new RevolvingConfigException(
                    $"Program '{program}' has {step.Verb} in its {list} steps, where it cannot run.");

            var needsClass = step.Verb is WaterfallVerb.PayInterest or WaterfallVerb.ReimburseChargeoff
                or WaterfallVerb.PayClass or WaterfallVerb.ReallocatePrincipal;
            if (needsClass && string.IsNullOrWhiteSpace(step.Class))
                throw new RevolvingConfigException($"Program '{program}': {step.Verb} must name a class.");

            if (step.Verb == WaterfallVerb.PayFee && string.IsNullOrWhiteSpace(step.Fee))
                throw new RevolvingConfigException($"Program '{program}': {step.Verb} must name a fee.");

            if (step.Verb == WaterfallVerb.ReallocatePrincipal && (step.From == null || step.From.Count == 0))
                throw new RevolvingConfigException(
                    $"Program '{program}': {step.Verb} must name the classes it takes from.");
        }
    }

    private static void ValidateTrigger(RevolvingProgram program, TriggerTest test)
    {
        if (program.ActionLag == null)
            throw new RevolvingConfigException(
                $"Program '{program.Name}' has trigger '{test.Name}' but states no actionLag.");

        if (program.ActionLag < 0)
            throw new RevolvingConfigException($"Program '{program.Name}' has a negative actionLag.");

        if (test.Window < 1)
            throw new RevolvingConfigException($"Trigger '{test.Name}' needs a window of at least one month.");

        if ((test.Level == null) == (test.AgainstMetric == null))
            throw new RevolvingConfigException($"Trigger '{test.Name}' must state a level or againstMetric, not both.");

        if (test.Action == TriggerAction.EarlyAmortization && test.CurePeriods != null)
            throw new RevolvingConfigException(
                $"Trigger '{test.Name}' states a cure, but EARLY_AMORTIZATION always sticks.");

        if (test.Action == TriggerAction.FundReserve && test.CurePeriods is null or < 1)
            throw new RevolvingConfigException($"FUND_RESERVE trigger '{test.Name}' must state its curePeriods.");

        if (test.Action == TriggerAction.FundReserve && test.ReserveAmount == null)
            throw new RevolvingConfigException($"FUND_RESERVE trigger '{test.Name}' must state its reserveAmount.");
    }

    private static void ValidateSeries(RevolvingProgram program, RevolvingSeries series)
    {
        var keys = series.Classes.Select(c => c.Key).ToHashSet();
        if (keys.Count != series.Classes.Count || keys.Count == 0)
            throw new RevolvingConfigException($"Series '{series.Name}' must have classes with distinct keys.");

        if (!keys.SetEquals(series.WritedownOrder))
            throw new RevolvingConfigException(
                $"Series '{series.Name}': writedownOrder must name every class once.");

        foreach (var key in series.FundedClasses)
            RequireClass(series, keys, key, "fundedClasses");

        foreach (var step in program.FinanceSteps.Concat(program.PrincipalSteps))
        {
            if (step.Class != null)
                RequireClass(series, keys, step.Class, step.Verb.ToString());
            foreach (var key in (step.From ?? Array.Empty<string>()).Concat(step.WhenPaid ?? Array.Empty<string>()))
                RequireClass(series, keys, key, step.Verb.ToString());
        }

        if (series.AccumulationStart != null || series.Phase == SeriesPhase.Accumulating)
        {
            if (series.ExpectedDate == null)
                throw new RevolvingConfigException($"Series '{series.Name}' accumulates but states no expectedDate.");
            if (series.ControlledAmounts.Count == 0)
                throw new RevolvingConfigException($"Series '{series.Name}' accumulates but states no controlled amount.");
            if (series.FundedClasses.Count == 0)
                throw new RevolvingConfigException($"Series '{series.Name}' accumulates but states no fundedClasses.");
        }

        if (series.ExpectedDate != null && series.AccumulationStart == null && series.Phase == SeriesPhase.Revolving)
            throw new RevolvingConfigException(
                $"Series '{series.Name}' has an expectedDate but no accumulationStart, so it would never pay.");

        if (series.ClosingDate is { } closing)
        {
            if (series.FirstPayDate is not { } firstPay)
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' states a closingDate but no firstPayDate, so its first period has no end.");
            if (closing >= firstPay)
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' closes on {closing:yyyy-MM-dd}, on or after its first pay date.");
        }

        if (series.Phase != SeriesPhase.Revolving)
            RequireFrozenValue(program, series);
    }

    private static void RequireFrozenValue(RevolvingProgram program, RevolvingSeries series)
    {
        var starts = $"Series '{series.Name}' starts in {PhaseNames.ToWire(series.Phase)}";
        if (program.PrincipalFreeze == PrincipalFreeze.FrozenPercent && series.FrozenPrincipalPct == null)
            throw new RevolvingConfigException($"{starts} and must state frozenPrincipalPct.");

        if (program.PrincipalFreeze == PrincipalFreeze.FrozenNumerator && series.FrozenNumerator == null)
            throw new RevolvingConfigException($"{starts} and must state frozenNumerator.");
    }

    private static void RequireClass(RevolvingSeries series, HashSet<string> keys, string key, string where)
    {
        if (!keys.Contains(key))
            throw new RevolvingConfigException($"{where} names class '{key}', which series '{series.Name}' does not have.");
    }
}
