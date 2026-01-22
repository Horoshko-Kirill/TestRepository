using AiMultiAgent.Core.Agents.CodeReview;
using AiMultiAgent.Core.Agents.Documentation;
using AiMultiAgent.Core.Agents.Pm.Llm;
using AiMultiAgent.Mcp.Client;
using GenerativeAI.Exceptions;
using Microsoft.Extensions.Logging; 
using System.Text;

namespace AiMultiAgent.Core.Agents.Pm;
  
/// <summary>
/// Project Manager Agent
/// Оркестрирует вызовы MCP tools, собирает ToolResults + Trace и просит LLM собрать финальный отчёт.
/// При ошибках MCP/LLM возвращает явные локальные fallback-результаты (без обращения к LLM).
/// </summary>
public sealed class PmAgent(
    IMcpClient mcp,
    IPmPlanner planner,
    ILogger<PmAgent> logger)
{
    private readonly IMcpClient _mcp = mcp;
    private readonly IPmPlanner _planner = planner;
    private readonly ILogger<PmAgent> _log = logger;

    private readonly PmAgentTelemetry _telemetry = new(logger);

    // Чтобы LLM/инструменты не падали на огромных файлах
    private const int MaxCharsPerFileForLlm = 30_000;

    /// <summary>
    /// Оркестрирует выполнение плана
    /// а затем агрегирует всё в единый <see cref="PmReport"/>
    /// </summary>
    public async Task<PmReport> OrchestrateAsync(PmRequest req, CancellationToken ct = default)
    {
        if (req.Files is null || req.Files.Count == 0)
        {
            throw new ArgumentException("Некорректный запрос: поле 'files' отсутствует или пустое.");
        }

        var files = req.Files
            .Where(f => !string.IsNullOrWhiteSpace(f.FileName))
            .ToList();

        if (files.Count == 0)
        {
            throw new ArgumentException("Некорректный запрос: все элементы 'files' имеют пустой 'fileName'.");
        }

        var componentName = string.IsNullOrWhiteSpace(req.ComponentName)
            ? GuessComponentName(files[0].FileName)
            : req.ComponentName!.Trim();

        var componentDescription = ResolveComponentDescription(req);

        var toolResults = new Dictionary<string, object?>();
        var trace = new List<TraceEvent>();

        void Trace(string type, string? tool = null, string? details = null)
            => trace.Add(new TraceEvent(DateTimeOffset.UtcNow, type, tool, details));

        void Step(string message)
        {
            Trace("REASONING", details: message);
            
            if (_log.IsEnabled(LogLevel.Information))
            {
                _log.LogInformation("[PM reasoning] {Message}", message);
            }
        }

        Step($"PM: старт. Файлов для ревью: {files.Count}");

        // --- 1) CODE REVIEW для каждого файла ---
        var codeReviews = new List<object?>();

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var f = files[i];
            var label = $"{i + 1}/{files.Count}";

            if (f.Data is null)
            {
                _log.LogWarning("Файл '{FileName}' пропущен: отсутствует поле 'data'.", f.FileName);
                
                codeReviews.Add(new
                {
                    fileName = f.FileName,
                    error = "Отсутствует поле 'data' (содержимое файла)."
                });
                
                continue;
            }

            var safeData = PrepareDataForLlm(f.FileName, f.Data);

            Step($"PM: code_review для файла {label}: {f.FileName}");
            Trace("TOOL_CALL_START", tool: "code_review", details: f.FileName);

            try
            {
                var mcpArgs = new 
                { 
                    fileName = f.FileName, 
                    data = safeData
                };

                _telemetry.LogSuggestedArgs(
                    toolName: "code_review",
                    req: req,
                    file: f,
                    llmSuggestedArgs: mcpArgs
                );

                var (result, usedFallback) = await TryMcpOrFallbackAsync(
                    toolName: "code_review",
                    mcpArgs: mcpArgs,
                    fallbackFactory: () =>
                    {
                        Step("PM: MCP code_review недоступен, возвращаю локальный отчёт (без LLM).");
                        return Task.FromResult(LocalCodeReviewFallback(f.FileName, safeData));
                    },
                    step: Step,
                    ct: ct
                );

                codeReviews.Add(new
                {
                    fileName = f.FileName,
                    usedFallback,
                    truncated = safeData.Length != f.Data.Length,
                    result
                });

                Trace("TOOL_CALL_END", tool: "code_review", details: "ok");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Trace("TOOL_CALL_END", tool: "code_review", details: "cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Trace("TOOL_CALL_END", tool: "code_review", details: "error: " + ex.Message);

                _log.LogWarning(ex, "Ошибка code_review для файла '{FileName}'. Возвращаю локальный результат", f.FileName);

                codeReviews.Add(new
                {
                    fileName = f.FileName,
                    usedFallback = true,
                    truncated = safeData.Length != f.Data.Length,
                    result = LocalCodeReviewFallback(f.FileName, safeData),
                    error = ex.Message,
                    exception = ex.GetType().Name
                });
            }
        }

        toolResults["code_review"] = codeReviews;

        // --- 2) DOCS (один раз на компонент) ---
        Step($"PM: generate_docs для компонента '{componentName}'");
        Trace("TOOL_CALL_START", tool: "generate_docs", details: componentName);

        try
        {
            var mcpArgs = new 
            {
                componentName,
                description = componentDescription 
            };

            _telemetry.LogSuggestedArgs(
                toolName: "code_review",
                req: req,
                file: null,
                llmSuggestedArgs: mcpArgs
            );

            var (docs, usedFallback) = await TryMcpOrFallbackAsync(
                toolName: "generate_docs",
                mcpArgs: mcpArgs,
                fallbackFactory: () =>
                {
                    Step("PM: MCP generate_docs недоступен, возвращаю локальную документацию без LLM");
                    return Task.FromResult(LocalDocsFallback(componentName, componentDescription));
                },
                step: Step,
                ct: ct
            );

            toolResults["generate_docs"] = new { usedFallback, result = docs };
            Trace("TOOL_CALL_END", tool: "generate_docs", details: "ok");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Trace("TOOL_CALL_END", tool: "generate_docs", details: "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Trace("TOOL_CALL_END", tool: "generate_docs", details: "error: " + ex.Message);

            _log.LogWarning(ex, "Ошибка generate_docs для компонента '{ComponentName}'. Возвращаю локальную заглушку.", componentName);

            toolResults["generate_docs"] = new
            {
                usedFallback = true,
                result = LocalDocsFallback(componentName, componentDescription),
                error = ex.Message,
                exception = ex.GetType().Name
            };
        }

        // --- 3) Агрегация отчёта через planner ---
        Step("PM: запрашиваю у LLM финальную агрегацию отчёта");

        try
        {
            var report = await _planner.AggregateAsync(req, toolResults, trace, ct);

            report.ToolResults = toolResults;
            report.Trace = trace;

            report.Meta ??= [];
            report.Meta["timestamp"] = DateTimeOffset.UtcNow;
            report.Meta["filesCount"] = files.Count;
            report.Meta["componentName"] = componentName;
            report.Meta["aggregation"] = "llm";

            Step("PM: отчёт готов (агрегация через LLM)");

            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Агрегация через LLM недоступна, возвращаю резервный отчёт.");

            return new PmReport
            {
                Meta = new Dictionary<string, object?>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow,
                    ["filesCount"] = files.Count,
                    ["componentName"] = componentName,
                    ["aggregation"] = ClassifyAggregationFailure(ex),
                    ["llm_error"] = ex.GetType().Name,
                    ["llm_error_message"] = ex.Message
                },
                ToolResults = toolResults,
                Summary = "Резервный PM отчёт: агрегация через LLM недоступна.",
                Risks = [],
                NextActions = [],
                Trace = trace
            };
        }
    }

    /// <summary>
    /// Пытается вызвать MCP tool, а при ошибке — возвращает ЯВНЫЙ локальный fallback
    /// </summary>
    private async Task<(TResult Result, bool UsedFallback)> TryMcpOrFallbackAsync<TResult>(
        string toolName,
        object mcpArgs,
        Func<Task<TResult>> fallbackFactory,
        Action<string> step,
        CancellationToken ct)
    {
        try
        {
            step($"PM: вызываю MCP tool '{toolName}'");

            if (_log.IsEnabled(LogLevel.Information))
            {
                _log.LogInformation("[PM MCP] tools/call -> {Tool}", toolName);
            }

            var result = await _mcp.CallToolAsync<TResult>(
                toolName: toolName,
                arguments: mcpArgs,
                jsonRpcId: null,
                ct: ct
            ) ?? throw new InvalidOperationException($"MCP tool '{toolName}' вернул null.");

            step($"PM: MCP tool '{toolName}' успешно вернул результат");
            return (result, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            step($"PM: MCP вызов '{toolName}' не удался ({ex.GetType().Name}). Перехожу на локальный fallback.");

            if (_log.IsEnabled(LogLevel.Warning))
                _log.LogWarning(ex, "[PM MCP] tool '{Tool}' завершился ошибкой. Использую локальный fallback.", toolName);

            ct.ThrowIfCancellationRequested();

            var fallback = await fallbackFactory();
            return (fallback, true);
        }
    }

    private static string ClassifyAggregationFailure(Exception ex)
    {
        if (ex is ApiException apiEx)
        {
            var msg = apiEx.Message ?? "";

            if (apiEx.ErrorCode == 429 || msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
            {
                return "fallback_quota";
            }

            if (apiEx.ErrorCode == 503 || msg.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) || msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            {
                return "fallback_overloaded";
            }

            return "fallback_api";
        }

        if (ex is HttpRequestException)
        {
            return "fallback_network";
        }

        return ex is TimeoutException || ex is TaskCanceledException ? "fallback_timeout" : "fallback";
    }

    private static string GuessComponentName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(name) ? "Component" : name;
    }

    private string PrepareDataForLlm(string fileName, string data)
    {
        if (data.Length <= MaxCharsPerFileForLlm)
        {
            return data;
        }

        _log.LogWarning(
            "Файл '{FileName}' слишком большой ({Len} символов). Обрезаю до {Max} символов для инструментов",
            fileName, data.Length, MaxCharsPerFileForLlm
        );

        return data[..MaxCharsPerFileForLlm];
    }

    private static string ResolveComponentDescription(PmRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.ComponentDescription))
        {
            return req.ComponentDescription!.Trim();
        }

        if (req.Files is null || req.Files.Count == 0)
        {
            return "Описание компонента отсутствует";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Описание компонента сформировано автоматически на основе входных файлов");

        const int maxChars = 800;
        var used = 0;

        foreach (var file in req.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Data))
            {
                continue;
            }

            sb.AppendLine();
            sb.AppendLine($"Файл: {file.FileName}");

            var chunk = file.Data.Trim();
            
            if (chunk.Length > 300)
            {
                chunk = chunk[..300] + "...";
            }

            if (used + chunk.Length > maxChars)
            {
                break;
            }

            sb.AppendLine(chunk);
            used += chunk.Length;
        }

        return sb.ToString();
    }

    // =========================
    // ЯВНЫЕ ЛОКАЛЬНЫЕ FALLBACK
    // =========================
    private static CodeReviewResult LocalCodeReviewFallback(string fileName, string data)
    {
        var issues = new List<CodeReviewIssue>();

        if (string.IsNullOrWhiteSpace(data))
        {
            issues.Add(new CodeReviewIssue
            {
                Severity = "warning",
                Title = "Пустое содержимое файла",
                Details = "Файл передан пустым — выполнить ревью невозможно."
            });
        }

        if (data.Length > MaxCharsPerFileForLlm)
        {
            issues.Add(new CodeReviewIssue
            {
                Severity = "info",
                Title = "Содержимое файла было обрезано",
                Details = $"Для обработки было использовано не более {MaxCharsPerFileForLlm} символов."
            });
        }

        if (data.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
            data.Contains("FIXME", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new CodeReviewIssue
            {
                Severity = "info",
                Title = "Найдены TODO/FIXME",
                Details = "В коде присутствуют TODO/FIXME — проверьте, что это ожидаемо."
            });
        }

        if (data.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            data.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            data.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            data.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new CodeReviewIssue
            {
                Severity = "warning",
                Title = "Потенциальные секреты в коде",
                Details = "Найдены слова вроде password/apiKey/token. Убедитесь, что секреты не захардкожены и вынесены в .env."
            });
        }

        return new CodeReviewResult
        {
            Summary = $"Локальный отчёт code review для '{fileName}'. Инструмент недоступен или вернул ошибку.",
            Issues = issues,
            Suggestions = issues.Count == 0
                ? []
                : ["Исправьте замечания и повторите проверку, когда инструмент будет доступен."]
        };
    }

    private static DocumentationResult LocalDocsFallback(string componentName, string description)
    {
        var markdown = 
        $"""
            # {componentName}

            _Локальная документация (инструмент недоступен или вернул ошибку)._

            ## Описание

            {(string.IsNullOrWhiteSpace(description) ? "Описание не предоставлено." : description)}

            ## Разделы (шаблон)

            - Overview
            - Responsibilities
            - Usage
            - Dependencies
            - Notes
         """;

        var uml = 
        $$"""
            @startuml
            class {{componentName}} {
                + Handle()
            }
            @enduml
        """.Replace("{{componentName}}", componentName);

        return new DocumentationResult
        {
            Markdown = markdown,
            UmlPlantUml = uml,
            UmlPlantUmlImageBase64 = null,
            StructuredJson = new DocumentationJsonData
            {
                ComponentName = componentName,
                Description = description ?? ""
            }
        };
    }
}
