using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
 
namespace AiMultiAgent.Core.Agents.Pm;
 
/// <summary>
/// Быстрые структурные логи для PM orchestration
/// </summary>
internal sealed class PmAgentTelemetry(ILogger log  )
{
    private const int DiffPreviewChars = 0;
    private const int MaxVerboseJsonChars = 20_000;

    private readonly ILogger _log = log;

    // JSON настройки для verbose-логов
    private static readonly JsonSerializerOptions VerboseJsonOptions = new()
    {
        WriteIndented = false,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Action<ILogger, string, string, string?, int, Exception?> _suggestedCodeReviewArgs =
        LoggerMessage.Define<string, string?, string?, int>(
            LogLevel.Information,
            new EventId(2101, nameof(_suggestedCodeReviewArgs)),
            "LLM suggested args for tool={Tool}. title={Title} diffSha256={DiffSha} diffLen={DiffLen}");

    private static readonly Action<ILogger, string, string?, int, Exception?> _suggestedGenerateDocsArgs =
        LoggerMessage.Define<string, string?, int>(
            LogLevel.Information,
            new EventId(2102, nameof(_suggestedGenerateDocsArgs)),
            "LLM suggested args for tool={Tool}. componentName={ComponentName} descLen={DescLen}");

    private static readonly Action<ILogger, string, string, Exception?> _suggestedArgsGeneric =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2103, nameof(_suggestedArgsGeneric)),
            "LLM suggested args for tool={Tool}. argsType={ArgsType}");

    private static readonly Action<ILogger, string, string, Exception?> _verboseArgsJson =
        LoggerMessage.Define<string, string>(
            LogLevel.Trace,
            new EventId(2199, nameof(_verboseArgsJson)),
            "Verbose args JSON for tool={Tool}: {ArgsJson}");

    public void LogSuggestedArgs(string toolName, PmRequest req, PmFile? file, object? llmSuggestedArgs)
    {
        if (_log.IsEnabled(LogLevel.Information))
        {
            if (string.Equals(toolName, "code_review", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = file?.FileName ?? "неизвестно";
                var data = file?.Data;
                var dataLen = data?.Length ?? 0;
                var dataSha = Sha256Hex(data);

                _suggestedCodeReviewArgs(
                    _log,
                    toolName,
                    fileName,
                    dataSha,
                    dataLen,
                    null
                );

                if (DiffPreviewChars > 0 && !string.IsNullOrEmpty(data))
                {
                    _log.LogInformation("Фрагмент кода: {Preview}", Preview(data, DiffPreviewChars));
                }
            }
            else if (string.Equals(toolName, "generate_docs", StringComparison.OrdinalIgnoreCase))
            {
                _suggestedGenerateDocsArgs(
                    _log,
                    toolName,
                    req.ComponentName,
                    req.ComponentDescription?.Length ?? 0,
                    null
                );
            }
            else
            {
                _suggestedArgsGeneric(
                    _log,
                    toolName,
                    llmSuggestedArgs?.GetType().FullName ?? "null",
                    null
                );
            }
        }

        if (_log.IsEnabled(LogLevel.Trace) && IsVerboseArgsEnabled() && llmSuggestedArgs is not null)
        {
            TryLogVerboseArgsJson(toolName, llmSuggestedArgs);
        }
    }


    private static bool IsVerboseArgsEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("PM_LOG_VERBOSE_ARGS"), "true", StringComparison.OrdinalIgnoreCase);

    private void TryLogVerboseArgsJson(string toolName, object llmSuggestedArgs)
    {
        try
        {
            var json = llmSuggestedArgs as string ?? 
                       JsonSerializer.Serialize(llmSuggestedArgs, VerboseJsonOptions);

            if (json.Length > MaxVerboseJsonChars)
            {
                json = json[..MaxVerboseJsonChars] + "…(truncated)";
            }

            _verboseArgsJson(_log, toolName, json, null);
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(ex, "Failed to serialize verbose args for tool {Tool}", toolName);
            }
        }
    }

    private static string? Sha256Hex(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string Preview(string s, int maxChars)
        => s.Length <= maxChars ? s : s[..maxChars] + "…";
}
