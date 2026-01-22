namespace AiMultiAgent.Core.Agents.Pm;

public sealed class PmPlan
{
    public string Objective { get; set; } = "";
    public List<PmPlanStep> Steps { get; set; } = [];
} 
