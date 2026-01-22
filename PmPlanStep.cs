 namespace AiMultiAgent.Core.Agents.Pm;
  
public sealed class PmPlanStep
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";              // "code_review" | "generate_docs"
    public object Arguments { get; set; } = new { };
    public string OnFail { get; set; } = "continue";    // "continue" | "stop"
}
