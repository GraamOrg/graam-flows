namespace GraamFlows.Revolving;

/// <summary>Refuses a master trust run by name before any month is run. The pool is checked by its own validator.</summary>
internal static class MasterTrustValidator
{
    /// <summary>The steps a master trust series' waterfall may state.</summary>
    private static readonly HashSet<OwnerTrustVerb> Allowed = new()
    {
        OwnerTrustVerb.PayFees, OwnerTrustVerb.PayServicing, OwnerTrustVerb.PayInterest,
        OwnerTrustVerb.PriorityPrincipal, OwnerTrustVerb.FundReserve, OwnerTrustVerb.RegularPrincipal,
        OwnerTrustVerb.PayCertificate
    };

    public static void Validate(MasterTrustRun run)
    {
        var trust = run.Trust;
        var pool = run.Pool;

        if (pool.Drivers?.Reinvest != null)
            throw new RevolvingConfigException(
                "A master trust buys with what its series leave the certificates: state masterTrust.acquisition, "
                + "not pool.drivers.reinvest.");
        if (pool.Purchases.Count > 0)
            throw new RevolvingConfigException(
                "A master trust buys with what its series leave the certificates: state masterTrust.acquisition, "
                + "not pool.purchases.");
        if (run.ActualPeriods.Count > pool.ActualPeriods.Count)
            throw new RevolvingConfigException(
                $"The master trust states {run.ActualPeriods.Count} actual periods and the pool "
                + $"{pool.ActualPeriods.Count}: a trust month is printed only where its pool month is.");
        if (run.DistributionDay is < 1 or > 28)
            throw new RevolvingConfigException($"distributionDay is {run.DistributionDay}. It must be from 1 to 28.");
        if (trust.Series.Count == 0)
            throw new RevolvingConfigException("A master trust states at least one series.");

        RequireShare("masterTrust.servicingRate", trust.ServicingRate);
        var rounding = trust.Rounding;
        foreach (var (name, decimals) in new[]
                 {
                     ("discountRatio", rounding.DiscountRatio), ("seriesShare", rounding.SeriesShare),
                     ("allocation", rounding.Allocation)
                 })
            if (decimals is < 0 or > 12)
                throw new RevolvingConfigException(
                    $"masterTrust.rounding.{name} is {decimals} decimals. It must be from 0 to 12.");

        var tests = trust.Tests;
        if (tests.WriteOffWindow < 1 || tests.DelinquencyWindow < 1)
            throw new RevolvingConfigException("A test's window must be at least 1 month.");
        foreach (var value in trust.Delinquency)
            RequireShare("masterTrust.delinquency", value);
        foreach (var (group, path) in trust.Concentration)
        foreach (var value in path)
            RequireAmount($"masterTrust.concentration['{group}']", value);

        if (trust.Acquisition is { } acquisition)
        {
            InstallmentPoolValidator.ValidatePrice("masterTrust.acquisition.price", acquisition.Price);
            if (acquisition.Terms.Count == 0)
                throw new RevolvingConfigException("masterTrust.acquisition states no terms.");
            foreach (var term in acquisition.Terms)
            {
                if (term.Term < 1)
                    throw new RevolvingConfigException($"masterTrust.acquisition has a term of {term.Term} months.");
                if (term.Age < 0)
                    throw new RevolvingConfigException($"masterTrust.acquisition term {term.Term} has age {term.Age}.");
                if (!double.IsFinite(term.Weight) || term.Weight <= 0.0)
                    throw new RevolvingConfigException(
                        $"masterTrust.acquisition term {term.Term} has weight {term.Weight * 100.0}%. It must be above 0%.");
            }

            var weights = acquisition.Terms.Sum(t => t.Weight);
            if (Math.Abs(weights - 1.0) > 1e-9)
                throw new RevolvingConfigException(
                    $"masterTrust.acquisition term weights come to {weights * 100.0}%, not 100%.");
        }

        var names = new HashSet<string>();
        foreach (var series in trust.Series)
        {
            if (string.IsNullOrWhiteSpace(series.Name) || !names.Add(series.Name))
                throw new RevolvingConfigException($"Series '{series.Name}' is unnamed or named twice.");
            ValidateSeries(series, run.Pool.StatementDate);
        }

        RequireAmount("masterTrust.state.certificateCash", run.State.CertificateCash);
        foreach (var (group, amount) in run.State.Concentration)
            RequireAmount($"masterTrust.state.concentration['{group}']", amount);

        for (var i = 0; i < run.ActualPeriods.Count; i++)
        {
            var a = run.ActualPeriods[i];
            var name = $"masterTrust.actualPeriods[{i}]";
            RequireAmount($"{name}.otherFunds", a.OtherFunds);
            if (a.PoolBalanceDeficit is { } deficit)
                RequireAmount($"{name}.poolBalanceDeficit", deficit);
            for (var f = 0; f < a.Fads.Count; f++)
            {
                RequireAmount($"{name}.fads[{f}].poolBalance", a.Fads[f].PoolBalance);
                if (f > 0 && a.Fads[f].Date <= a.Fads[f - 1].Date)
                    throw new RevolvingConfigException($"{name}.fads are not in date order.");
                foreach (var (group, amount) in a.Fads[f].Concentration)
                    RequireAmount($"{name}.fads[{f}].concentration['{group}']", amount);
            }

            foreach (var (key, s) in a.Series)
            {
                if (!names.Contains(key))
                    throw new RevolvingConfigException($"{name}.series names '{key}', which is not a series of the trust.");
                if (s.DiscountRate is { } rate)
                    InstallmentPoolValidator.RequireRate($"{name}.series['{key}'].discountRate", rate);
                foreach (var (field, value) in new[]
                         {
                             ("presentValueStart", s.PresentValueStart), ("presentValueEnd", s.PresentValueEnd),
                             ("allocationNumerator", s.AllocationNumerator), ("noteBalance", s.NoteBalance),
                             ("fees", s.Fees), ("redemptionDeposit", s.RedemptionDeposit)
                         })
                    if (value is { } amount)
                        RequireAmount($"{name}.series['{key}'].{field}", amount);
                foreach (var (cls, coupon) in s.Coupons)
                    if (!double.IsFinite(coupon) || coupon < 0.0)
                        throw new RevolvingConfigException(
                            $"{name}.series['{key}'].coupons['{cls}'] is {coupon}. It must be a finite percent of at least 0.");
            }
        }
    }

    private static void ValidateSeries(MasterTrustSeries series, DateTime statementDate)
    {
        var name = $"masterTrust.series['{series.Name}']";
        InstallmentPoolValidator.RequireRate($"{name}.discountRate", series.DiscountRate);
        if (!double.IsFinite(series.OcPct) || series.OcPct is < 0.0 or >= 1.0)
            throw new RevolvingConfigException($"{name}.ocPct is {series.OcPct * 100.0}%. It must be at least 0% and below 100%.");
        RequireAmount($"{name}.requiredReserve", series.RequiredReserve);
        RequireAmount($"{name}.monthlyFees", series.MonthlyFees);
        RequireShare($"{name}.pfaLimitPct", series.PfaLimitPct);
        RequireAmount($"{name}.redemptionPrice", series.RedemptionPrice);
        if (series.Tests.AllocatedPoolLimit is { } limit)
            RequireShare($"{name}.tests.allocatedPoolLimit", limit);
        if (series.ClosingDate is { } closing && closing.Date <= statementDate.Date)
            throw new RevolvingConfigException(
                $"{name}.closingDate is on or before the statement date: a series outstanding then states no closing date.");

        var state = series.State;
        if (state.Phase is not (SeriesPhase.Revolving or SeriesPhase.EarlyAmortization))
            throw new RevolvingConfigException(
                $"{name} is REVOLVING or EARLY_AMORTIZATION, not {PhaseNames.ToWire(state.Phase)}.");
        if (state.Phase == SeriesPhase.EarlyAmortization && (state.Event == null || state.FrozenNumerator == null
                                                             || state.FrozenNotes == null))
            throw new RevolvingConfigException(
                $"{name} in EARLY_AMORTIZATION states its event, frozenNotes and frozenNumerator.");
        RequireAmount($"{name}.state.reserve", state.Reserve);
        RequireAmount($"{name}.state.pfa", state.Pfa);
        RequireAmount($"{name}.state.pfaReleaseDue", state.PfaReleaseDue);

        if (series.Private)
        {
            if (series.Classes.Count > 0 || series.Steps.Count > 0 || series.Tiers.Count > 0)
                throw new RevolvingConfigException(
                    $"{name} is private: it states its notes as privateNotes and has no classes, tiers or steps.");
            RequireAmount($"{name}.privateNotes", series.PrivateNotes);
            return;
        }

        if (series.Classes.Count == 0)
            throw new RevolvingConfigException($"{name} has no classes: state its notes, or mark it private.");
        if (series.Tiers.Count == 0)
            throw new RevolvingConfigException($"{name} states no tiers.");
        var tiers = new HashSet<string>();
        foreach (var tier in series.Tiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Name) || !tiers.Add(tier.Name))
                throw new RevolvingConfigException($"{name} tier '{tier.Name}' is unnamed or named twice.");
            if (tier.Classes.Count == 0)
                throw new RevolvingConfigException($"{name} tier '{tier.Name}' has no classes.");
        }

        var placed = series.Tiers.SelectMany(t => t.Classes).ToList();
        foreach (var c in series.Classes)
        {
            var count = placed.Count(k => k == c.Key);
            if (count != 1)
                throw new RevolvingConfigException(
                    $"{name} class '{c.Key}' sits in {count} tiers. Each class sits in exactly one.");
            if (c.Index == null && c.Coupon == null)
                throw new RevolvingConfigException($"{name} fixed class '{c.Key}' states no coupon.");
            RequireAmount($"{name} class '{c.Key}' balance", c.Balance);
            RequireAmount($"{name} class '{c.Key}' interestShortfall", c.InterestShortfall);
        }

        foreach (var key in placed)
            if (series.Classes.All(c => c.Key != key))
                throw new RevolvingConfigException($"{name} tier names class '{key}', which the series does not have.");

        if (series.Steps.Count == 0)
            throw new RevolvingConfigException($"{name} states no steps.");
        foreach (var step in series.Steps)
        {
            var wire = OwnerTrustVerbs.ToWire(step.Verb);
            if (!Allowed.Contains(step.Verb))
                throw new RevolvingConfigException(
                    $"{name} step {wire} is an owner trust's. A master trust series states PAY_FEES, PAY_SERVICING, "
                    + "PAY_INTEREST, PRIORITY_PRINCIPAL, FUND_RESERVE, REGULAR_PRINCIPAL and PAY_CERTIFICATE.");
            if (OwnerTrustVerbs.NamesTier(step.Verb))
            {
                if (step.Tier == null || !tiers.Contains(step.Tier))
                    throw new RevolvingConfigException($"{name} step {wire} names tier '{step.Tier}', which is not a tier.");
            }
            else if (step.Tier != null)
            {
                throw new RevolvingConfigException(
                    $"{name} step {wire} names a tier. Only PAY_INTEREST and PRIORITY_PRINCIPAL act on one.");
            }
        }

        if (series.Steps[^1].Verb != OwnerTrustVerb.PayCertificate)
            throw new RevolvingConfigException($"{name}'s last step is PAY_CERTIFICATE: what is left is the Class R interest's.");
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
