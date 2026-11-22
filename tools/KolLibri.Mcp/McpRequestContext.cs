namespace KolLibri.Mcp;

/// <summary>The bearer token of the request being handled; set by the HTTP host, null on stdio.</summary>
public static class McpRequestContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Token { get => Current.Value; set => Current.Value = value; }

    private static readonly AsyncLocal<UsageSettlement?> CurrentUsage = new();

    /// <summary>Lets a handler report what a call really consumed. A holder object, because AsyncLocal values set
    /// inside an awaited handler do not flow back to the caller.</summary>
    public static UsageSettlement? Usage { get => CurrentUsage.Value; set => CurrentUsage.Value = value; }
}

/// <summary>Amount a tool call actually used; null means "the whole reservation".</summary>
public sealed class UsageSettlement
{
    public long? Confirmed { get; set; }
}
