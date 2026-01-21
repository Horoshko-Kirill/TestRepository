namespace AiMultiAgent.Core.Agents.Pm.Llm;

public interface IPmPlanner
{
    Task<PmPlan> CreatePlanAsync(PmRequest req, CancellationToken ct);
    
    Task<PmReport> AggregateAsync(
        PmRequest req,
        object toolResults,
        List<TraceEvent> traces,
        CancellationToken ct
    );
}
