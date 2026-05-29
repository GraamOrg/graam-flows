using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using Xunit;

namespace GraamFlows.Tests.Unit.Util;

/// <summary>
/// Smoke test for the runtime-compile path used by
/// <see cref="RulesBuilder.CompileRules"/>.
///
/// Context: PR #11 / harmony issue #1164 added a
/// <c>using GraamFlows.Util.Collections;</c> directive and
/// <c>SingleByName</c> calls inside <c>RulesHost.cs</c>, which is
/// embedded as a resource and compiled by Roslyn at runtime (not at
/// <c>dotnet build</c> time). The Roslyn compilation only resolves
/// namespaces that appear in
/// <c>Assembly.GetExecutingAssembly().GetReferencedAssemblies()</c>.
/// If <c>GraamFlows.Util</c> ever stops being a direct reference of
/// <c>GraamFlows.Core</c>, every assembled deal would throw at the
/// first formula evaluation — invisible to the rest of the test
/// suite, which exercises pay-rule formulas but doesn't itself
/// invoke COUPON/INTEREST/WAC on missing tranches.
///
/// This test exercises the runtime-compile path with no pay rules,
/// triggers, or tranche-coupon formulas so the only thing being
/// asserted is that the bare embedded RulesHost.cs source — including
/// the new <c>using</c> and the <c>LookupTranche</c> helper that
/// calls <c>SingleByName</c> — compiles to a loadable assembly.
/// </summary>
public class RulesHostRuntimeCompileTests
{
    [Fact]
    public void BuildAssembly_BareEmbeddedRulesHost_CompilesAndLoads()
    {
        // Drive the lower-level overload with empty collections so we
        // generate the bare embedded RulesHost.cs (no per-deal pay rules
        // or trigger formulas). This isolates the runtime-compile of the
        // committed engine source from any test-supplied formula string.
        var code = RulesBuilder.BuildCode(
            payRules: Array.Empty<IPayRule>(),
            triggers: Array.Empty<IDealTrigger>(),
            tranches: Array.Empty<ITranche>());

        code.Should().Contain("using GraamFlows.Util.Collections;",
            because: "PR #11 added this using to access LookupExtensions.SingleByName");
        code.Should().Contain("SingleByName",
            because: "the LookupTranche helper introduced by PR #11 calls SingleByName");

        // If `GraamFlows.Util.Collections` isn't resolvable from
        // `Assembly.GetExecutingAssembly().GetReferencedAssemblies()`,
        // BuildAssembly throws with a CSharpCompilation error here.
        var assemblyStream = RulesBuilder.BuildAssembly(code);
        assemblyStream.Should().NotBeNull();
        assemblyStream.Length.Should().BeGreaterThan(0,
            because: "a successful Emit produces a non-empty PE image");
    }
}
