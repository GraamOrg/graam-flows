namespace GraamFlows.Revolving;

/// <summary>Refuses an owner trust run by name before any month is run. The pool is checked by its own validator.</summary>
internal static class OwnerTrustValidator
{
    public static void Validate(OwnerTrustRun run)
    {
        var trust = run.Trust;
        var pool = run.Pool;

        if (pool.Drivers?.Reinvest != null)
            throw new RevolvingConfigException(
                "An owner trust buys through its acquisition account: state ownerTrust.acquisition, not "
                + "pool.drivers.reinvest.");
        if (pool.Purchases.Count > 0)
            throw new RevolvingConfigException(
                "An owner trust buys through its acquisition account: state ownerTrust.acquisition, not "
                + "pool.purchases.");
        if (run.ActualPeriods.Count > pool.ActualPeriods.Count)
            throw new RevolvingConfigException(
                $"The owner trust states {run.ActualPeriods.Count} actual periods and the pool "
                + $"{pool.ActualPeriods.Count}: a trust month is printed only where its pool month is.");

        InstallmentPoolValidator.RequireRate("ownerTrust.discountRate", trust.DiscountRate);
        if (run.DistributionDay is < 1 or > 28)
            throw new RevolvingConfigException($"distributionDay is {run.DistributionDay}. It must be from 1 to 28.");

        ValidateTiers(run);
        ValidateSteps(trust);
        ValidateOc(trust.Oc);
        RequireAmount("ownerTrust.requiredReserve", trust.RequiredReserve);
        RequireAmount("ownerTrust.monthlyFees", trust.MonthlyFees);
        RequireShare("ownerTrust.servicingRate", trust.ServicingRate);
        RequireShare("ownerTrust.reserveEarningsRate", trust.ReserveEarningsRate);
        foreach (var value in trust.Delinquency)
            RequireShare("ownerTrust.delinquency", value);

        var tests = trust.Tests;
        if (tests.WriteOffWindow < 1 || tests.DelinquencyWindow < 1)
            throw new RevolvingConfigException("A test's window must be at least 1 month.");
        if (tests.OcGraceMonths < 0)
            throw new RevolvingConfigException("ownerTrust.tests.ocGraceMonths must be at least 0.");

        if (trust.CleanUp is { } cleanUp)
        {
            if (!double.IsFinite(cleanUp.PoolFactor) || cleanUp.PoolFactor is <= 0.0 or > 1.0)
                throw new RevolvingConfigException(
                    $"ownerTrust.cleanUp.poolFactor is {cleanUp.PoolFactor * 100.0}%. It must be above 0% and at most 100%.");
            if (!double.IsFinite(cleanUp.ClosingPoolBalance) || cleanUp.ClosingPoolBalance <= 0.0)
                throw new RevolvingConfigException("ownerTrust.cleanUp.closingPoolBalance must be above 0.");
            RequireAmount("ownerTrust.cleanUp.price", cleanUp.Price);
            if (cleanUp.DelayMonths < 0)
                throw new RevolvingConfigException("ownerTrust.cleanUp.delayMonths must be at least 0.");
        }

        if (trust.AcquisitionPrice is { } price)
        {
            InstallmentPoolValidator.ValidatePrice("ownerTrust.acquisition.price", price);
            if (trust.AcquisitionTerms.Count == 0)
                throw new RevolvingConfigException("ownerTrust.acquisition states a price and no terms.");
            foreach (var term in trust.AcquisitionTerms)
            {
                if (term.Term < 1)
                    throw new RevolvingConfigException($"ownerTrust.acquisition has a term of {term.Term} months.");
                if (term.Age < 0)
                    throw new RevolvingConfigException($"ownerTrust.acquisition term {term.Term} has age {term.Age}.");
                if (!double.IsFinite(term.Weight) || term.Weight <= 0.0)
                    throw new RevolvingConfigException(
                        $"ownerTrust.acquisition term {term.Term} has weight {term.Weight * 100.0}%. It must be above 0%.");
            }

            var weights = trust.AcquisitionTerms.Sum(t => t.Weight);
            if (Math.Abs(weights - 1.0) > 1e-9)
                throw new RevolvingConfigException(
                    $"ownerTrust.acquisition term weights come to {weights * 100.0}%, not 100%.");
        }
        else if (trust.AcquisitionTerms.Count > 0)
        {
            throw new RevolvingConfigException("ownerTrust.acquisition states terms and no price.");
        }

        var state = run.State;
        if (state.Phase is not (SeriesPhase.Revolving or SeriesPhase.Amortizing or SeriesPhase.EarlyAmortization))
            throw new RevolvingConfigException(
                $"An owner trust is REVOLVING, AMORTIZING or EARLY_AMORTIZATION, not {PhaseNames.ToWire(state.Phase)}.");
        if (state.Phase == SeriesPhase.EarlyAmortization && state.Event == null)
            throw new RevolvingConfigException(
                "An owner trust in EARLY_AMORTIZATION states the amortization event that started it.");
        RequireAmount("state.reserve", state.Reserve);
        RequireAmount("state.acquisitionAccount", state.AcquisitionAccount);
        RequireAmount("state.negativeCarry", state.NegativeCarry);
        if (state.AdjustedPool is { } adjusted)
            RequireAmount("state.adjustedPool", adjusted);

        for (var i = 0; i < run.ActualPeriods.Count; i++)
        {
            var a = run.ActualPeriods[i];
            var name = $"ownerTrust.actualPeriods[{i}]";
            if (a.Fees is { } fees)
                RequireAmount($"{name}.fees", fees);
            RequireAmount($"{name}.otherFunds", a.OtherFunds);
            if (a.CleanUpDeposit is { } deposit)
                RequireAmount($"{name}.cleanUpDeposit", deposit);
            if (a.AdjustedPool is { } pooled)
                RequireAmount($"{name}.adjustedPool", pooled);
        }
    }

    private static void ValidateTiers(OwnerTrustRun run)
    {
        var tiers = run.Trust.Tiers;
        if (tiers.Count == 0)
            throw new RevolvingConfigException("ownerTrust states no tiers.");
        if (run.Classes.Count == 0)
            throw new RevolvingConfigException("An owner trust needs at least one class of notes.");

        var names = new HashSet<string>();
        foreach (var tier in tiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Name) || !names.Add(tier.Name))
                throw new RevolvingConfigException($"Tier '{tier.Name}' is unnamed or named twice.");
            if (tier.Classes.Count == 0)
                throw new RevolvingConfigException($"Tier '{tier.Name}' has no classes.");
        }

        var placed = tiers.SelectMany(t => t.Classes).ToList();
        foreach (var c in run.Classes)
        {
            var count = placed.Count(k => k == c.Key);
            if (count != 1)
                throw new RevolvingConfigException(
                    $"Class '{c.Key}' sits in {count} tiers. Each class sits in exactly one.");
            if (c.Index == null && c.Coupon == null)
                throw new RevolvingConfigException($"Fixed class '{c.Key}' states no coupon.");
            RequireAmount($"class '{c.Key}' balance", c.Balance);
            RequireAmount($"class '{c.Key}' interestShortfall", c.InterestShortfall);
        }

        foreach (var key in placed)
            if (run.Classes.All(c => c.Key != key))
                throw new RevolvingConfigException($"A tier names class '{key}', which the series does not have.");
    }

    private static void ValidateSteps(OwnerTrustConfig trust)
    {
        if (trust.Steps.Count == 0)
            throw new RevolvingConfigException("ownerTrust states no steps.");
        var tiers = trust.Tiers.Select(t => t.Name).ToHashSet();
        foreach (var step in trust.Steps)
        {
            var wire = OwnerTrustVerbs.ToWire(step.Verb);
            if (OwnerTrustVerbs.NamesTier(step.Verb))
            {
                if (step.Tier == null || !tiers.Contains(step.Tier))
                    throw new RevolvingConfigException($"Step {wire} names tier '{step.Tier}', which is not a tier.");
            }
            else if (step.Tier != null)
            {
                throw new RevolvingConfigException($"Step {wire} names a tier. Only PAY_INTEREST and "
                                                   + "PRIORITY_PRINCIPAL act on one.");
            }
        }

        if (trust.Steps[^1].Verb != OwnerTrustVerb.PayCertificate)
            throw new RevolvingConfigException("An owner trust's last step is PAY_CERTIFICATE: what is left is the certificate's.");
    }

    private static void ValidateOc(OwnerTrustOc oc)
    {
        foreach (var (name, pct) in new[]
                 {
                     ("revolvingPct", oc.RevolvingPct), ("revolvingFailingPct", oc.RevolvingFailingPct),
                     ("amortizingPct", oc.AmortizingPct), ("amortizingFailingPct", oc.AmortizingFailingPct)
                 })
            if (!double.IsFinite(pct) || pct is < 0.0 or >= 1.0)
                throw new RevolvingConfigException(
                    $"ownerTrust.oc.{name} is {pct * 100.0}%. It must be at least 0% and below 100%.");
        RequireAmount("ownerTrust.oc.floor", oc.Floor);
    }

    private static void RequireShare(string name, double value)
    {
        if (!double.IsFinite(value) || value is < 0.0 or > 1.0)
            throw new RevolvingConfigException($"{name} is {value * 100.0}%. It must be from 0% to 100%.");
    }

    private static void RequireAmount(string name, double value)
    {
        InstallmentPoolValidator.RequireAmount(name, value);
    }
}
