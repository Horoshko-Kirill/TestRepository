namespace AiMultiAgent.Core.Agents.Pm;

public sealed class PmRequest
{
    public List<PmFile>? Files { get; init; }

    public string? ComponentName { get; init; }
    public string? ComponentDescription { get; init; }
}

public sealed class PmFile
{
    public string FileName { get; init; } = "";
    public string Data { get; init; } = "";
}