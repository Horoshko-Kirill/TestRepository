using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
    
namespace AiMultiAgent.Core.Agents.CodeReview;
          
/// <summary>         
/// Агент для выполнен      ия   автом  атическ   ого   code review с помощью LLM.
/// Вызывается PM-аг  ентом через MCP как инструмент <c>code_review</c>.
/// Получает файл и код, зап  ра   шивает LLM и возвращает структурированный результат ревью.
/// </summary>
public sealed class CodeReviewerAgent
{
    private readonly IChatClient _chat;
    private readonly ILogger<CodeReviewerAgent> _log;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Создаёт новый экземпляр агента code review.
    /// </summary>
    /// <param name="chat">LLM-клиент (Gemini/OpenAI и т.п.)</param>
    /// <param name="log">Логгер для трассировки и диагностики</param>
    public CodeReviewerAgent(IChatClient chat, ILogger<CodeReviewerAgent> log)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public CodeReviewerAgent()
    {
    }

    /// <summary>
    /// Выполняет ревью указанного файла.
    /// </summary>
    /// <param name="fileName">Имя файла, который анализируется</param>
    /// <param name="data">Содержимое файла (исходный код)</param>
    /// <param name="ct">Токен отмены</param>
    /// <returns>Структурированный результат code review</returns>
    public async Task<CodeReviewResult> ReviewAsync(
        string fileName,
        string data,
        CancellationToken ct = default)
    {
        _log.LogInformation("CodeReview started for {File}", fileName);

        var system = SystemPrompt;

        var user = $"""
            File: {fileName}

            Analyze the following code:

            {data}
            """;

        var json = await AskJsonAsync(system, user, ct);

        _log.LogDebug("LLM raw JSON for {File}: {Json}", fileName, json);

        CodeReviewResult? result;

        try
        {
            result = JsonSerializer.Deserialize<CodeReviewResult>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to deserialize CodeReview JSON: {Json}", json);
            throw new InvalidOperationException("Invalid CodeReview JSON returned by LLM");
        }

        if (result == null)
        {
            throw new InvalidOperationException("Empty CodeReview result");
        }

        result.Validate();

        _log.LogInformation(
            "CodeReview completed for {File}. Issues: {Count}",
            fileName,
            result.Issues.Count
        );

        return result;
    }

    /// <summary>
    /// Отправляет запрос LLM с ожиданием JSON-ответа.
    /// Если LLM вернул некорректный JSON, выполняется один повторный запрос на исправление.
    /// </summary>
    private async Task<string> AskJsonAsync(
        string system,
        string user,
        CancellationToken ct)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, user)
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        var response = await _chat.GetResponseAsync(messages, cancellationToken: timeout.Token);
        var text = response.Text ?? response.Messages?.LastOrDefault()?.Text;

        var extracted = TryExtractJson(text);
        if (extracted != null)
        {
            return extracted;
        }

        _log.LogWarning("Invalid JSON from LLM. Attempting repair.");

        var repairMessages = new[]
        {
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(
                ChatRole.User,
                "Fix the output. Return ONLY valid JSON that matches the schema.\n\nBAD_OUTPUT:\n" + text
            )
        };

        var repair = await _chat.GetResponseAsync(repairMessages, cancellationToken: timeout.Token);
        extracted = TryExtractJson(repair.Text ?? repair.Messages?.LastOrDefault()?.Text);

        if (extracted == null)
        {
            _log.LogError(
                "LLM failed to produce valid JSON after repair.\nOriginal:\n{Orig}\nRepaired:\n{Rep}",
                text,
                repair.Text
            );

            throw new InvalidOperationException("LLM failed to return valid CodeReview JSON");
        }

        return extracted;
    }

    /// <summary>
    /// Пытается извлечь валидный JSON-объект из произвольного текста LLM.
    /// </summary>
    private static string? TryExtractJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();

        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        var json = s[start..(end + 1)];

        try
        {
            JsonDocument.Parse(json);
            return json;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Системный prompt для LLM, определяющий формат и правила code review.
    /// Гарантирует, что модель вернёт строго структурированный JSON.
    /// </summary>
    private const string SystemPrompt = """
        You are a senior software engineer performing a strict code review.

        Return ONLY valid JSON.
        No markdown.
        No comments.
        No explanations.
        No backticks.

        Always check:
        1. Bugs, security, performance, design flaws.
        2. Naming conventions for classes, methods, and files.
        3. That the main class name matches the file name.
        4. That file names are in PascalCase for C#.

        Schema:
        {
          "summary": string,
          "issues": [
            {
              "severity": "info" | "warning" | "error",
              "title": string,
              "details": string
            }
          ],
          "suggestions": [ string ]
        }

        Rules:
        - Find real bugs, security risks, performance problems and design flaws.
        - Be strict and professional.
        - If code is clean, return empty arrays for issues and suggestions.
        """;
        }
