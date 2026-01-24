namespace AiMultiAgent.Core.Agents.CodeReview;
  
public sealed class CodeReviewResult
{
    public string Summary { get; init; } = default!;
    public List<CodeReviewIssue> Issues { get; init; } = new();
    public List<string> Suggestions { get; init; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Summary))
            throw new InvalidOperationException("Summary is empty");

        foreach (var i in Issues)
        {
            if (i.Severity is not ("info" or "warning" or "error"))
                throw new InvalidOperationException($"Invalid severity: {i.Severity}");
        }
    }
}
