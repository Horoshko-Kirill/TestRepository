namespace AiMultiAgent.Core.Agents.CodeReview;
  
public sealed class CodeReviewIssue
{
    public string Severity { get; init; } = "info"; // info|warning|error
    public string Title { get; init; } = default!;
    public string Details { get; init; } = default!;
}
