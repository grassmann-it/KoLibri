using System.Collections.ObjectModel;
using System.Text.Json;
using KolArbitrage.Core.Accounts;

namespace KolLibri.Mcp;

/// <summary>A typed, fixed allowlist entry exposed through <c>tools/list</c>.</summary>
public sealed record McpToolDefinition
{
    public McpToolDefinition(
        string name,
        string description,
        JsonElement inputSchema,
        Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Tool description is required.", nameof(description));
        ArgumentNullException.ThrowIfNull(handler);
        Name = name;
        Description = description;
        Handler = handler;
        InputSchema = inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? McpJson.EmptyObject
            : inputSchema.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }
    public Func<JsonElement, CancellationToken, Task<object?>> Handler { get; }

    /// <summary>Tool metadata without the executable delegate.</summary>
    public object ToProtocolDescriptor()
        => new { name = Name, description = Description, inputSchema = InputSchema };
}

/// <summary>
/// Registry for the small read-only tool surface. Handlers are registered by
/// the host rather than discovered from arbitrary reflection, keeping the MCP
/// boundary auditable and preventing mutation routes from being exposed.
/// </summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<McpToolDefinition> Tools
        => new ReadOnlyCollection<McpToolDefinition>(_tools.Values.ToArray());

    public void Register(McpToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_tools.TryAdd(definition.Name, definition))
            throw new InvalidOperationException($"MCP tool '{definition.Name}' is already registered.");
    }

    public bool TryGet(string name, out McpToolDefinition? definition)
        => _tools.TryGetValue(name, out definition);

    public McpToolDefinition Get(string name)
        => TryGet(name, out var definition)
            ? definition!
            : throw new KeyNotFoundException($"MCP tool '{name}' is not registered.");
}

/// <summary>Options controlling the protocol metadata and request timeout.</summary>
public record KolibriMcpServerOptions
{
    public string ServerName { get; init; } = "KoLibri.Mcp";
    public string ServerVersion { get; init; } = "0.1.0";
    public string ProtocolVersion { get; init; } = "2024-11-05";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Fallback role used when a read tool omits its optional role.</summary>
    public AccountRole DefaultRole { get; init; } = AccountRole.Main;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerName)) throw new ArgumentException("Server name is required.", nameof(ServerName));
        if (string.IsNullOrWhiteSpace(ServerVersion)) throw new ArgumentException("Server version is required.", nameof(ServerVersion));
        if (string.IsNullOrWhiteSpace(ProtocolVersion)) throw new ArgumentException("Protocol version is required.", nameof(ProtocolVersion));
        if (RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
    }
}

/// <summary>Short options name for hosts that do not use the KoLibri prefix.</summary>
public sealed record McpServerOptions : KolibriMcpServerOptions;
