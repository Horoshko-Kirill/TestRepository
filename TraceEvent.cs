namespace AiMultiAgent.Core.Agents.Pm;

public sealed record TraceEvent(
    DateTimeOffset Ts, 
    string Type, 
    string? Tool = null, 
    string? Details = null
);