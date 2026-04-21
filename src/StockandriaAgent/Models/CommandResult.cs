namespace StockandriaAgent.Models;

public static class CommandResultStatus
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
}

public class CommandResult
{
    public string Status { get; set; } = CommandResultStatus.Success;
    public object? ResultPayload { get; set; }
    public string? ErrorMessage { get; set; }

    public static CommandResult Ok(object? payload = null) => new()
    {
        Status = CommandResultStatus.Success,
        ResultPayload = payload,
    };

    public static CommandResult Fail(string message, object? payload = null) => new()
    {
        Status = CommandResultStatus.Failed,
        ResultPayload = payload,
        ErrorMessage = message,
    };
}
