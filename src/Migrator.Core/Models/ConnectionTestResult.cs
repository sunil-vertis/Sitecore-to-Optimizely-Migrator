namespace Migrator.Core.Models;

public sealed class ConnectionTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? HttpStatus { get; init; }
    public string? RequestUrl { get; init; }
    public string? ResponseSnippet { get; init; }

    public static ConnectionTestResult Ok(string message, string? requestUrl = null) =>
        new() { Success = true, Message = message, RequestUrl = requestUrl };

    public static ConnectionTestResult Fail(
        string message,
        int? status = null,
        string? requestUrl = null,
        string? snippet = null) =>
        new()
        {
            Success = false,
            Message = message,
            HttpStatus = status,
            RequestUrl = requestUrl,
            ResponseSnippet = snippet
        };
}
