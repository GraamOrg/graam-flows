namespace GraamFlows.Revolving;

/// <summary>Refuses an installment pool run by name before any month is run.</summary>
internal static class InstallmentPoolValidator
{
    public static void Validate(InstallmentPoolRun run)
    {
        var statement = run.StatementDate;
        if (statement.Day != DateTime.DaysInMonth(statement.Year, statement.Month))
            throw new RevolvingConfigException(
                $"statementDate {statement:yyyy-MM-dd} is not a month end. It must be the record date of "
                + "a closed monthly period.");

        if (run.Periods < 1)
            throw new RevolvingConfigException("An installment pool run needs at least one period.");

        if (run.Cohorts.Count == 0)
            throw new RevolvingConfigException("An installment pool must state at least one cohort.");

        for (var i = 0; i < run.Cohorts.Count; i++)
            ValidateCohort($"pool.cohorts[{i}]", run.Cohorts[i]);

        // Several series of one pool can share a rate: a repeated rate is valued once.
        foreach (var rate in run.DiscountRates)
            RequireRate("pool.discountRates", rate);

        if (run.ActualPeriods.Count > run.Periods)
            throw new RevolvingConfigException(
                $"The run asks for {run.Periods} periods and states {run.ActualPeriods.Count} actual periods.");

        if (run.Periods > run.ActualPeriods.Count && run.Drivers == null)
            throw new RevolvingConfigException(
                $"The run asks for {run.Periods} periods with {run.ActualPeriods.Count} actual periods and "
                + "no drivers to forecast the rest.");

        if (run.Drivers is { } drivers)
            ValidateDrivers(drivers);

        for (var month = 0; month < run.ActualPeriods.Count; month++)
            ValidateActual($"pool.actualPeriods[{month}]", run.ActualPeriods[month]);

        for (var i = 0; i < run.Purchases.Count; i++)
        {
            var purchase = run.Purchases[i];
            var name = $"pool.purchases[{i}]";
            ValidatePurchase(name, purchase);
            if (purchase.Period is not { } period)
                throw new RevolvingConfigException($"{name} states no period.");
            if (period < run.ActualPeriods.Count || period >= run.Periods)
                throw new RevolvingConfigException(
                    $"{name} closes in period {period}, which is not a forecast month of this run: those are "
                    + $"{run.ActualPeriods.Count} to {run.Periods - 1}. A replayed month states its own additions.");
        }
    }

    private static void ValidateCohort(string name, InstallmentCohort cohort)
    {
        if (cohort.Age < 0)
            throw new RevolvingConfigException($"{name}.age is {cohort.Age}. It must be at least 0.");

        if (cohort.Schedule.Count == 0)
            throw new RevolvingConfigException($"{name}.schedule has no payments.");

        for (var k = 0; k < cohort.Schedule.Count; k++)
        {
            var pay = cohort.Schedule[k];
            if (!double.IsFinite(pay) || pay < 0.0)
                throw new RevolvingConfigException(
                    $"{name}.schedule[{k}] is {pay}. A scheduled payment must be a finite amount of at least 0.");
        }

        if (cohort.Face <= 0.0)
            throw new RevolvingConfigException($"{name}.schedule pays nothing.");
    }

    private static void ValidatePurchase(string name, InstallmentPurchase purchase)
    {
        ValidateCohort(name, purchase.Cohort);
        if ((purchase.Price == null) == (purchase.Cash == null))
            throw new RevolvingConfigException($"{name} must state a price or its cash, not both.");
        if (purchase.Cash is { } cash)
            RequireAmount($"{name}.cash", cash);
        if (purchase.Price is { } price)
            ValidatePrice($"{name}.price", price);
    }

    private static void ValidatePrice(string name, InstallmentPrice price)
    {
        if ((price.PerFace == null) == (price.PvRate == null))
            throw new RevolvingConfigException($"{name} must state percentOfFace or pvRate, not both.");
        if (price.PerFace is { } perFace)
            RequireAmount($"{name}.percentOfFace", perFace * 100.0);
        if (price.PvRate is { } rate)
            RequireRate($"{name}.pvRate", rate);
    }

    private static void ValidateDrivers(InstallmentDrivers drivers)
    {
        RequireCurve("drivers.prepaymentRate", drivers.Prepayment);
        RequireCurve("drivers.upgradeRate", drivers.Upgrade);
        RequireCurve("drivers.writeOffRate", drivers.WriteOff);

        // Prepayments and upgrades take their shares of the same part of the schedule.
        var ages = Math.Max(drivers.Prepayment.Values.Count, drivers.Upgrade?.Values.Count ?? 0);
        for (var age = 0; age < ages; age++)
        {
            var both = drivers.Prepayment.At(age) + (drivers.Upgrade?.At(age) ?? 0.0);
            if (both > 1.0 + 1e-12)
                throw new RevolvingConfigException(
                    $"At age {age} prepayments and upgrades come to {both * 100.0}% of the schedule. "
                    + "Together they can take at most 100%.");
        }

        for (var month = 0; month < drivers.RepurchaseAmounts.Count; month++)
            RequireAmount($"drivers.repurchaseAmounts[{month}]", drivers.RepurchaseAmounts[month]);

        if (drivers.RepurchasePrice is { } repurchasePrice)
            ValidatePrice("drivers.repurchasePrice", repurchasePrice);
        else if (drivers.RepurchaseAmounts.Any(a => a > 0.0))
            throw new RevolvingConfigException("drivers.repurchaseAmounts are stated with no repurchasePrice.");

        if (drivers.Reinvest is { } reinvest)
        {
            if (!double.IsFinite(reinvest.Share) || reinvest.Share is < 0.0 or > 1.0)
                throw new RevolvingConfigException(
                    $"drivers.reinvest.share is {reinvest.Share * 100.0}%. It must be from 0% to 100% of collections.");
            ValidatePrice("drivers.reinvest.price", reinvest.Price);
            if (reinvest.Terms.Count == 0)
                throw new RevolvingConfigException("drivers.reinvest states no terms.");
            foreach (var term in reinvest.Terms)
            {
                if (term.Term < 1)
                    throw new RevolvingConfigException($"drivers.reinvest has a term of {term.Term} months.");
                if (!double.IsFinite(term.Weight) || term.Weight <= 0.0)
                    throw new RevolvingConfigException(
                        $"drivers.reinvest term {term.Term} has weight {term.Weight * 100.0}%. It must be above 0%.");
            }

            var weights = reinvest.Terms.Sum(t => t.Weight);
            if (Math.Abs(weights - 1.0) > 1e-9)
                throw new RevolvingConfigException(
                    $"drivers.reinvest term weights come to {weights * 100.0}%, not 100%.");
        }
    }

    private static void ValidateActual(string name, InstallmentActual actual)
    {
        if (actual.BeginningFace is { } beginning)
            RequireAmount($"{name}.beginningFace", beginning);
        RequireAmount($"{name}.scheduledCollections", actual.ScheduledCollections);
        RequireAmount($"{name}.prepaidCollections", actual.PrepaidCollections);
        RequireAmount($"{name}.upgradeCollections", actual.UpgradeCollections);
        RequireAmount($"{name}.writeOffs", actual.WriteOffs);
        RequireAmount($"{name}.repurchases", actual.Repurchases);
        if (actual.EndingFace is { } ending)
            RequireAmount($"{name}.endingFace", ending);

        if (actual.RepurchaseCash != null && actual.RepurchasePrice != null)
            throw new RevolvingConfigException($"{name} must state repurchaseCash or repurchasePrice, not both.");
        if (actual.RepurchaseCash is { } cash)
            RequireAmount($"{name}.repurchaseCash", cash);
        if (actual.RepurchasePrice is { } price)
            ValidatePrice($"{name}.repurchasePrice", price);
        if (actual.Repurchases > 0.0 && actual.RepurchaseCash == null && actual.RepurchasePrice == null)
            throw new RevolvingConfigException($"{name} repurchases face and states no repurchaseCash or repurchasePrice.");

        for (var i = 0; i < actual.Additions.Count; i++)
        {
            var addition = actual.Additions[i];
            ValidatePurchase($"{name}.additions[{i}]", addition);
            if (addition.Period != null)
                throw new RevolvingConfigException(
                    $"{name}.additions[{i}] states a period. A replayed month's additions belong to that month.");
        }
    }

    private static void RequireCurve(string name, InstallmentCurve? curve)
    {
        if (curve == null)
            return;
        if (curve.Values.Count == 0)
            throw new RevolvingConfigException($"{name} has no values.");
        foreach (var value in curve.Values)
            if (!double.IsFinite(value) || value is < 0.0 or > 1.0)
                throw new RevolvingConfigException(
                    $"{name} has {value * 100.0}%. A rate must be from 0% to 100%.");
    }

    private static void RequireRate(string name, double rate)
    {
        if (!double.IsFinite(rate) || rate < 0.0)
            throw new RevolvingConfigException($"{name} is {rate * 100.0}%. A discount rate must be at least 0%.");
    }

    private static void RequireAmount(string name, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0.0)
            throw new RevolvingConfigException($"{name} is {amount}. It must be a finite amount of at least 0.");
    }
}
