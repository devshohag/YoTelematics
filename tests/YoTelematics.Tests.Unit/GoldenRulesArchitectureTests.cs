using FluentAssertions;
using YoTelematics.Application.Common;
using YoTelematics.Domain.Entities;
using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Tests.Unit;

public class GoldenRulesArchitectureTests
{
    [Fact]
    public void Core_Domain_And_Application_Should_Contain_Zero_Hardcoded_Bike_Types()
    {
        var domainTypes = typeof(Asset).Assembly.GetTypes().Select(t => t.Name).ToList();
        var appTypes = typeof(ITenantContextAccessor).Assembly.GetTypes().Select(t => t.Name).ToList();

        domainTypes.Should().NotContain(name => name.Contains("Bike", StringComparison.OrdinalIgnoreCase));
        appTypes.Should().NotContain(name => name.Contains("Bike", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuleEvaluationResult_NoOp_Should_Be_Empty_And_NonMutating()
    {
        RuleEvaluationResult.NoOp.StateMutated.Should().BeFalse();
        RuleEvaluationResult.NoOp.Timers.Should().BeEmpty();
        RuleEvaluationResult.NoOp.Alerts.Should().BeEmpty();
        RuleEvaluationResult.NoOp.Commands.Should().BeEmpty();
    }
}
