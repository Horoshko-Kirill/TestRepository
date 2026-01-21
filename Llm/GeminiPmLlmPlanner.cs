using GenerativeAI.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiMultiAgent.Core.Agents.Pm.Llm;

/// <summary>
/// IPmPlanner на базе Gemini
/// </summary>
public sealed class GeminiPmLlmPlanner(IChatClient chat, IMemoryCache cache) : IPmPlanner
{
    private readonly IChatClient _chat = chat;
    private readonly IMemoryCache _cache = cache;

    private static readonly TimeSpan _llmTimeout = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Создаёт план выполнения шагов для PM агента. Возвращает строго JSON
    /// </summary>
    public async Task<PmPlan> CreatePlanAsync(PmRequest req, CancellationToken ct)
    {
        var system =
        """
            You are a PM planner. Return ONLY valid JSON. No markdown, no code fences, no extra text.
            Output MUST start with '{' and end with '}'.

            Input request JSON contains:
            - files: array of { fileName: string, data: string }
            - componentName: string|null
            - componentDescription: string|null

            You MUST create one "code_review" step for EACH file in "files".
            You MUST create exactly one "generate_docs" step for the component.

            Allowed tools: code_review, generate_docs.

            For tool "code_review" arguments MUST be exactly:
            { "fileName": string, "data": string }

            For tool "generate_docs" arguments MUST be exactly:
            { "componentName": string, "description": string }

            Schema:
            {
              "objective": string,
              "steps": [
                { "id": string, "tool": "code_review"|"generate_docs", "arguments": object, "onFail": "continue"|"stop" }
              ]
            }
        """;

        var user = "Request JSON:\n" + JsonSerializer.Serialize(req);
        var json = await AskJsonAsync("plan", system, user, ct);

        try
        {
            return JsonSerializer.Deserialize<PmPlan>(json, _jsonOpts)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось разобрать JSON плана от LLM: ответ имеет неверный формат.", ex);
        }
    }

    /// <summary>
    /// Агрегирует toolResults + trace в финальный отчёт. Возвращает строго JSON
    /// </summary>
    public async Task<PmReport> AggregateAsync(
        PmRequest req,
        object toolResults,
        List<TraceEvent> traces,
        CancellationToken ct)
    {
        var system =
        """
            You are a PM aggregator. Return ONLY valid JSON. No markdown, no extra text.

            Return JSON that matches EXACTLY this schema:
            {
              "meta": object,
              "toolResults": object,
              "risks": [ object ],
              "nextActions": [ object ],
              "summary": string,
              "trace": [ { "ts": string, "type": string, "tool": string|null, "details": string|null } ]
            }

            Rules:
            - summary: 3-5 short lines.
            - risks / nextActions can be empty arrays.
        """;

        var user = "Request:\n" + JsonSerializer.Serialize(req) +
                   "\nToolResults:\n" + JsonSerializer.Serialize(toolResults) +
                   "\nTrace:\n" + JsonSerializer.Serialize(traces);

        var json = await AskJsonAsync("agg", system, user, ct);
        
        try
        {
            return JsonSerializer.Deserialize<PmReport>(json, _jsonOpts)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось разобрать JSON отчёта от LLM: ответ имеет неверный формат.", ex);
        }
    }

    /// <summary>
    /// Запрашивает JSON у LLM. Если ответ невалидный — пытается починить одним повтором.
    /// Добавляет короткий кеш, чтобы не долбить LLM на одинаковых входах
    /// </summary>
    private async Task<string> AskJsonAsync(string kind, string system, string user, CancellationToken ct)
    {
        var key = CacheKey(kind, system, user);

        if (_cache.TryGetValue<string>(key, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var text = await CallAsync(system, user, ct);
        var extracted = TryExtractJson(text);

        if (extracted is not null)
        {
            _cache.Set(key, extracted, TimeSpan.FromMinutes(2));
            return extracted;
        }

        var repairUser = "Fix output. Return ONLY valid JSON, no extra text.\n\nBAD_OUTPUT:\n" + text;
        var repaired = await CallAsync(system, repairUser, ct);

        var repairedExtracted = TryExtractJson(repaired) ??
                                throw new InvalidOperationException("LLM failed to return valid JSON after retry.");
        
        _cache.Set(key, repairedExtracted, TimeSpan.FromMinutes(2));
        return repairedExtracted;
    }

    /// <summary>
    /// Пытается вытащить JSON из ответа
    /// </summary>
    private static string? TryExtractJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();

        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = s.IndexOf('{');
            var firstBracket = s.IndexOf('[');

            var start = (firstBrace >= 0 && (firstBracket < 0 || firstBrace < firstBracket))
                ? firstBrace
                : firstBracket;

            if (start >= 0)
            {
                s = s[start..];
            }

            var lastBrace = s.LastIndexOf('}');
            var lastBracket = s.LastIndexOf(']');

            var end = Math.Max(lastBrace, lastBracket);
            
            if (end > 0)
            {
                s = s[..(end + 1)];
            }
        }

        try
        {
            JsonDocument.Parse(s);
            return s;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Делает вызов Gemini с таймаутом и ретраями на 429/503
    /// </summary>
    private async Task<string> CallAsync(string system, string user, CancellationToken ct)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, user),
        };

        var delaysMs = new[] { 250, 800, 2000 };

        for (int attempt = 0; attempt < delaysMs.Length + 1; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_llmTimeout);

            try
            {
                ChatResponse response = await _chat.GetResponseAsync(messages, cancellationToken: timeoutCts.Token);

                var text = response.Text ?? response.Messages?.LastOrDefault()?.Text;
                return string.IsNullOrWhiteSpace(text) ? "{}" : text;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Gemini request timed out after {_llmTimeout.TotalSeconds:0}s.");
            }
            catch (ApiException ex) when (IsRetryable(ex) && attempt < delaysMs.Length)
            {
                await Task.Delay(delaysMs[attempt], ct);
            }
        }

        throw new InvalidOperationException("Gemini call failed after retries.");
    }

    /// <summary>
    /// Определяет, можно ли ретраить ошибку Gemini
    /// </summary>
    private static bool IsRetryable(ApiException ex)
    {
        var msg = ex.Message ?? "";

        if (ex.ErrorCode is 429 or 503)
        {
            return true;
        }

        if (msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (msg.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Формирует ключ кеша для plan/agg
    /// </summary>
    private static string CacheKey(string kind, string system, string user)
    {
        var payload = $"{kind}\n{system}\n{user}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"{kind}:{Convert.ToHexString(bytes)}";
    }
}
