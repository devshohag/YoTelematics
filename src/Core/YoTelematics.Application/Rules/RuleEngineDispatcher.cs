using YoTelematics.Application.Timers;
using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Application.Rules;

/// <summary>
/// Resolves the appropriate IRuleSet plugin dynamically by Asset.Category and coordinates
/// timer scheduling/cancellation via IDurableTimerService (Golden Rule 1).
/// </summary>
public sealed class RuleEngineDispatcher
{
    private readonly IReadOnlyDictionary<string, IRuleSet> _ruleSets;
    private readonly IDurableTimerService _timerService;

    public RuleEngineDispatcher(
        IEnumerable<IRuleSet> ruleSets,
        IDurableTimerService timerService)
    {
        _ruleSets = ruleSets.ToDictionary(r => r.Category, StringComparer.OrdinalIgnoreCase);
        _timerService = timerService;
    }

    public async Task<RuleEvaluationResult> DispatchAsync(
        RuleEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_ruleSets.TryGetValue(context.Asset.Category, out var ruleSet))
        {
            return RuleEvaluationResult.NoOp;
        }

        var result = await ruleSet.EvaluateAsync(context, cancellationToken);

        for (int i = 0; i < result.Timers.Count; i++)
        {
            var timerInstruction = result.Timers[i];
            if (timerInstruction.CancelExisting || !timerInstruction.DueAtUtc.HasValue)
            {
                await _timerService.CancelTimerAsync(
                    context.Asset.Id,
                    context.Asset.TenantId,
                    timerInstruction.TimerType,
                    cancellationToken);
            }
            else
            {
                await _timerService.ScheduleTimerAsync(
                    context.Asset.Id,
                    context.Asset.TenantId,
                    timerInstruction.TimerType,
                    timerInstruction.DueAtUtc.Value,
                    cancellationToken);
            }
        }

        return result;
    }
}
