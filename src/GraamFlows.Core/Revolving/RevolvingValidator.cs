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
        ValidateProgramFigures(program);
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

        RequireAmount("poolBalance", run.PoolBalance);
        if (run.Drivers is { } drivers)
            ValidateDrivers(drivers);

        for (var month = 0; month < run.ActualPeriods.Count; month++)
            ValidateActual(month, run.ActualPeriods[month]);

        foreach (var rate in run.FundingEarningsRate ?? Array.Empty<double>())
            RequirePercent("fundingEarningsRate", rate);
    }

    private static void ValidateDrivers(PoolDriverPath drivers)
    {
        RequireShare("paymentRate", drivers.PaymentRate);
        RequireShare("newReceivablesRate", drivers.NewReceivablesRate, double.PositiveInfinity);
        RequireShare("yield", drivers.Yield);
        RequireShare("chargeOffRate", drivers.ChargeOffRate);
        RequireShare("recoveryRate", drivers.RecoveryRate);
        RequireShare("dilutionRate", drivers.DilutionRate);
    }

    /// <summary>
    ///     A driver is a share of the month's beginning balance, annual or not. Collections cannot pass
    ///     the balance, and new receivables can: a month's new charges are not drawn from it.
    /// </summary>
    private static void RequireShare(string name, PoolDriver? driver, double most = 1.0)
    {
        foreach (var value in driver?.Values ?? Array.Empty<double>())
        {
            // 31 days is the most an annual rate can be worth in a month.
            var share = driver!.Annual ? PoolLedger.MonthlyShare(value, 31) : value;
            if (!double.IsFinite(share) || share < 0.0 || share > most)
                throw new RevolvingConfigException(
                    $"Driver {name} is {value * 100.0}%. A month's value must come to at least 0%"
                    + (double.IsInfinity(most) ? "." : " and at most 100% of beginning principal receivables."));
        }
    }

    private static void ValidateActual(int month, ActualPeriod actual)
    {
        var name = $"actualPeriods[{month}]";
        if (actual.Beginning is { } beginning)
            RequireAmount($"{name}.beginningBalance", beginning);
        RequireAmount($"{name}.newReceivables", actual.NewReceivables);
        RequireAmount($"{name}.principalCollections", actual.PrincipalCollections);
        RequireAmount($"{name}.financeCollections", actual.FinanceCollections);
        RequireAmount($"{name}.recoveries", actual.Recoveries);
        RequireAmount($"{name}.chargeOffs", actual.ChargeOffs);
        RequireAmount($"{name}.dilution", actual.Dilution);
        if (actual.Ending is { } ending)
            RequireAmount($"{name}.endingBalance", ending);

        if (actual.Days <= 0)
            throw new RevolvingConfigException($"{name}.days is {actual.Days}. It must be at least 1.");
    }

    private static void ValidateProgramFigures(RevolvingProgram program)
    {
        if (program.SeriesRequiredTransferorPct is { } seriesPct)
            RequirePercent("seriesRequiredTransferorPct", seriesPct);
        if (program.RequiredTransferorPct is { } transferorPct)
            RequirePercent("requiredTransferorPct", transferorPct);
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

        if (test.Level is { } level)
            RequireFinite($"Trigger '{test.Name}' level", level);
        if (test.ReserveAmount is { } reserve)
            RequireAmount($"Trigger '{test.Name}' reserveAmount", reserve);
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

        foreach (var (test, state) in series.TriggerStates)
        {
            if (program.Triggers.All(t => t.Name != test))
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' states triggerState on test '{test}', which the program does not have.");
            if (state.MonthsToAct < 0 || state.GoodMonths < 0)
                throw new RevolvingConfigException($"Series '{series.Name}' triggerState of '{test}' must be at least 0.");
        }

        RequireStepsForClaims(program, series);
        RequireGroupWhereItMatters(program, series);
        ValidateFigures(series);

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

        if (series is { AccumulationStart: { } start, ExpectedDate: { } expected } && expected < start)
            throw new RevolvingConfigException(
                $"Series '{series.Name}' expects to pay on {expected:yyyy-MM-dd}, before it starts accumulating "
                + $"on {start:yyyy-MM-dd}.");

        if (series.ClosingDate is { } closing)
        {
            if (series.FirstPayDate is not { } firstPay)
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' states a closingDate but no firstPayDate, so its first period has no end.");
            if (closing >= firstPay)
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' closes on {closing:yyyy-MM-dd}, on or after its first pay date.");

            // A series not yet issued has no months to accumulate in.
            if (series.AccumulationStart is { } begins && begins < new DateTime(closing.Year, closing.Month, 1))
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' starts accumulating on {begins:yyyy-MM-dd}, before the month it closes in, "
                    + $"{closing:yyyy-MM}.");
        }

        if (series.Phase != SeriesPhase.Revolving && !series.FreezePending)
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

    /// <summary>
    ///     An amount that accrues with no step to pay it adds to base rate and to what is owed, and
    ///     never leaves. A program that means that must say so, so this is refused.
    /// </summary>
    private static void RequireStepsForClaims(RevolvingProgram program, RevolvingSeries series)
    {
        var interest = program.FinanceSteps.Where(s => s.Verb == WaterfallVerb.PayInterest).Select(s => s.Class).ToHashSet();
        foreach (var c in series.Classes.Where(c => OwesInterest(c) && !interest.Contains(c.Key)))
            throw new RevolvingConfigException(
                $"Class '{c.Key}' of series '{series.Name}' has no PAY_INTEREST step, so its interest would "
                + "accrue and never be paid.");

        var names = new HashSet<string>();
        var fees = program.FinanceSteps.Where(s => s.Verb == WaterfallVerb.PayFee).Select(s => s.Fee).ToHashSet();
        foreach (var fee in series.Fees)
        {
            if (string.IsNullOrWhiteSpace(fee.Name))
                throw new RevolvingConfigException($"Series '{series.Name}' has a fee with no name.");
            if (!names.Add(fee.Name))
                throw new RevolvingConfigException($"Series '{series.Name}' states fee '{fee.Name}' twice.");
            if (!fees.Contains(fee.Name) && (fee.RatePct > 0.0 || series.FeesDue.GetValueOrDefault(fee.Name) >= SeriesRun.Cent))
                throw new RevolvingConfigException(
                    $"Series '{series.Name}' states fee '{fee.Name}' and no PAY_FEE step pays it, so it would "
                    + "accrue into base rate and never be paid.");
        }
    }

    /// <summary>An empty group would be a group of its own, joining every series that omits one.</summary>
    private static void RequireGroupWhereItMatters(RevolvingProgram program, RevolvingSeries series)
    {
        if (!string.IsNullOrWhiteSpace(series.Group))
            return;

        if (series.PrincipalSharing || series.ExcessSharing)
            throw new RevolvingConfigException($"Series '{series.Name}' shares cash but states no group.");

        if (program.GroupSteps.Count > 0)
            throw new RevolvingConfigException(
                $"Series '{series.Name}' states no group, and program '{program.Name}' reallocates finance across one.");
    }

    /// <summary>A class with no balance or no rate accrues nothing, so it needs no step to pay it.</summary>
    private static bool OwesInterest(RevolvingClass c)
    {
        return c.InterestShortfall >= SeriesRun.Cent || (c.Balance >= SeriesRun.Cent && (c.Index != null || c.Coupon > 0.0));
    }

    private static void ValidateFigures(RevolvingSeries series)
    {
        var name = $"Series '{series.Name}'";
        foreach (var c in series.Classes)
        {
            var label = $"{name} class '{c.Key}'";
            if (c.Index == null && c.Coupon == null)
                throw new RevolvingConfigException($"{label} is fixed and states no coupon.");
            if (c.Coupon is { } coupon)
                RequirePercent($"{label} coupon", coupon);
            RequireFinite($"{label} margin", c.Margin);
            RequireAmount($"{label} balance", c.Balance);
            if (c.OriginalBalance is { } original)
                RequireAmount($"{label} originalBalance", original);
            RequireAmount($"{label} writtenDown", c.WrittenDown);
            RequireAmount($"{label} interestShortfall", c.InterestShortfall);
            RequireAmount($"{label} defaultDue", c.DefaultDue);
            if (c.WrittenDown > c.Balance + SeriesRun.Cent)
                throw new RevolvingConfigException(
                    $"{label} has {c.WrittenDown:F2} written down of a {c.Balance:F2} balance.");
        }

        foreach (var fee in series.Fees)
            RequirePercent($"{name} fee '{fee.Name}' rate", fee.RatePct);
        foreach (var amount in series.ControlledAmounts)
            RequireAmount($"{name} controlledAmount", amount);

        RequireAmount($"{name} requiredReserve", series.RequiredReserve);
        RequireAmount($"{name} fundingAccount", series.FundingAccount);
        RequireAmount($"{name} fundingDeficit", series.FundingDeficit);
        RequireAmount($"{name} reserve", series.Reserve);
        RequireAmount($"{name} carriedFinance", series.CarriedFinance);
        foreach (var (fee, amount) in series.FeesDue)
        {
            if (series.Fees.All(f => f.Name != fee))
                throw new RevolvingConfigException($"{name} states feesDue on fee '{fee}', which it does not state.");
            RequireAmount($"{name} feesDue of '{fee}'", amount);
        }

        foreach (var (metric, months) in series.History)
        foreach (var value in months)
            RequireFinite($"{name} history of {metric}", value);

        if (series.AccumulationMonthsRun < 0)
            throw new RevolvingConfigException($"{name} has a negative accumulationMonthsRun.");

        if (series.FrozenNumerator is { } numerator)
            RequireAmount($"{name} frozenNumerator", numerator);

        // No clamp: a frozen share the allocator scales back would pay something nobody stated.
        if (series.FrozenPrincipalPct is { } frozen && !(frozen is >= 0.0 and <= 1.0))
            throw new RevolvingConfigException(
                $"{name} frozenPrincipalPct is {frozen * 100.0}. It must be a percent from 0 to 100.");
    }

    private static void RequireFinite(string name, double value)
    {
        if (!double.IsFinite(value))
            throw new RevolvingConfigException($"{name} is {value}. It must be a finite number.");
    }

    /// <summary>
    ///     One run's close is the next run's open, and a close can sit a hair under zero, so anything
    ///     under a cent is dust here, as it is in the cash proof.
    /// </summary>
    private static void RequireAmount(string name, double value)
    {
        if (!double.IsFinite(value) || value <= -SeriesRun.Cent)
            throw new RevolvingConfigException($"{name} is {value}. It must be a finite amount of at least 0.");
    }

    private static void RequirePercent(string name, double value)
    {
        if (!(value is >= 0.0 and <= 100.0))
            throw new RevolvingConfigException($"{name} is {value}. It must be a percent from 0 to 100.");
    }

    private static void RequireClass(RevolvingSeries series, HashSet<string> keys, string key, string where)
    {
        if (!keys.Contains(key))
            throw new RevolvingConfigException($"{where} names class '{key}', which series '{series.Name}' does not have.");
    }
}
