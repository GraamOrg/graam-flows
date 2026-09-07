using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     Pay rules are invoked by reflection (<c>GenericExecutor.EvaluateUnknown</c>), so every
///     deal-authoring guard in RulesHost reaches the controller wrapped in a
///     TargetInvocationException whose own message is "Exception has been thrown by the target
///     of an invocation." Returning that told the caller nothing about which rule was wrong —
///     and graam-harmony #4830's fallback reads the error to decide what to say.
/// </summary>
public class WaterfallErrorUnwrapTests
{
    [Fact]
    public void AReflectionWrappedRuleErrorIsReportedByItsOwnMessage()
    {
        var inner = new DealModelingException("TEST-DEAL", "the ladder names no class in group '1'");
        var wrapped = new TargetInvocationException(inner);

        var reported = WaterfallController.Unwrap(wrapped);

        reported.Should().BeSameAs(inner);
        reported.Message.Should().Contain("names no class in group");
        reported.Message.Should().NotContain("target of an invocation");
    }

    [Fact]
    public void NestedWrappersAreUnwrappedToTheInnermostCause()
    {
        var inner = new DealModelingException("TEST-DEAL", "root cause");
        var reported = WaterfallController.Unwrap(
            new TargetInvocationException(new TargetInvocationException(inner)));

        reported.Should().BeSameAs(inner);
    }

    [Fact]
    public void AnOrdinaryExceptionIsReturnedUntouched()
    {
        // The existing error contract for every non-rule failure must not move.
        var plain = new InvalidOperationException("plain");
        WaterfallController.Unwrap(plain).Should().BeSameAs(plain);
    }

    [Fact]
    public void AWrapperWithNoInnerExceptionIsReturnedAsItself()
    {
        var bare = new TargetInvocationException(null);
        WaterfallController.Unwrap(bare).Should().BeSameAs(bare);
    }
}
