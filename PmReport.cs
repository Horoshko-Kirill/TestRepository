namespace AiMultiAgent.Core.Agents.Pm;
    
public sealed class PmReport
{
    public Dictionary<string, object?> Meta { get; set; } = [];
    public Dictionary<string, object?> ToolResults { get; set; } = [];

    public List<object> Risks { get; set; } = [];
    public List<object> NextActions { get; set; } = [];
    public string Summary { get; set; } = "";
    public List<TraceEvent> Trace { get; set; } = [];
}