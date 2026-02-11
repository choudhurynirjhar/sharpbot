namespace Sharpbot;

/// <summary>
/// Constants for LLM message roles, eliminating magic strings like "user", "assistant", "tool".
/// </summary>
public static class MessageRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}
