namespace KolLibri.Mcp;

/// <summary>The bearer token of the request being handled; set by the HTTP host, null on stdio.</summary>
public static class McpRequestContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Token { get => Current.Value; set => Current.Value = value; }
}
