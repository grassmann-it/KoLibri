using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using KoLibri;

namespace KolLibri.Mcp;

/// <summary>
/// Minimal read-only MCP host. The host composes the existing LibKolibri/Core
/// contracts and only registers queries, deterministic plans, and previews.
/// No handler in this class submits a KoL request that changes account state.
/// </summary>
public partial class KolibriMcpServer
{
    private readonly McpAccounts _accounts;
    private readonly KolibriMcpServerOptions _options;
    private readonly McpToolRegistry _registry;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Tool, Func<JsonElement, JsonElement> Map)> _previewAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly AdventureMcpExecutionBridge? _executionBridge;

    /// <summary>
    /// A server over the stored accounts (0.5: KoLibri sessions, opened per tool call). Credentials stay inside the
    /// account store; tool arguments carry only the role name.
    /// </summary>
    public KolibriMcpServer(
        McpAccounts? accounts = null,
        KolibriMcpServerOptions? options = null,
        TimeProvider? clock = null,
        AdventureMcpExecutionBridge? executionBridge = null)
    {
        _accounts = accounts ?? new McpAccounts();
        _options = options ?? new KolibriMcpServerOptions();
        _options.Validate();
        _clock = clock ?? TimeProvider.System;
        _registry = new McpToolRegistry();
        _executionBridge = executionBridge;
        RegisterReadOnlyTools();
    }

    /// <summary>The accounts the tools act on.</summary>
    public McpAccounts Accounts => _accounts;

    /// <summary>Fixed read-only tool allowlist exposed by this server.</summary>
    public McpToolRegistry Tools => _registry;

    /// <summary>Handles one already parsed JSON-RPC request and returns a response.</summary>
    public async Task<JsonElement?> HandleAsync(
        JsonElement request,
        CancellationToken cancellationToken = default)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return ErrorResponse(null, -32600, "JSON-RPC request must be an object.");

        if (!request.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(version.GetString(), "2.0", StringComparison.Ordinal))
            return ErrorResponse(request.TryGetProperty("id", out var invalidId) ? invalidId : null, -32600, "jsonrpc must be '2.0'.");

        var id = request.TryGetProperty("id", out var idElement)
            ? idElement.Clone()
            : default;
        var isNotification = id.ValueKind is JsonValueKind.Undefined;
        var method = StringProperty(request, "method");
        if (string.IsNullOrWhiteSpace(method))
            return isNotification ? null : ErrorResponse(id, -32600, "JSON-RPC method is required.");

        var parameters = request.TryGetProperty("params", out var paramsElement)
            ? McpJson.CloneOrEmpty(paramsElement)
            : McpJson.EmptyObject;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (method)
            {
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "ping":
                    return isNotification ? null : SuccessResponse(id, new { });
                case "initialize":
                    return isNotification ? null : SuccessResponse(id, InitializeResult());
                case "tools/list":
                    return isNotification ? null : SuccessResponse(id, new { tools = _registry.Tools.Select(x => x.ToProtocolDescriptor()).ToArray() });
                case "tools/call":
                    if (isNotification)
                    {
                        // A notification has no result, but still execute no
                        // handler. This avoids surprising side effects for a
                        // caller that accidentally omitted an id.
                        return null;
                    }
                    return await CallToolAsync(id, parameters, cancellationToken).ConfigureAwait(false);
                default:
                    return isNotification ? null : ErrorResponse(id, -32601, $"Method '{method}' is not supported.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return isNotification ? null : ErrorResponse(id, -32000, "Request timed out.");
        }
        catch (McpInvalidParamsException exception)
        {
            return isNotification ? null : ErrorResponse(id, -32602, exception.Message);
        }
        catch (Exception exception)
        {
            return isNotification ? null : ErrorResponse(id, -32001, SanitizeError(exception));
        }
    }

    /// <summary>Runs the stdio JSON-RPC loop until EOF or cancellation.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var firstFraming = true;
        var contentLengthFraming = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            McpInboundMessage? inbound;
            try
            {
                inbound = await McpStdioFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (McpProtocolException exception)
            {
                var error = ErrorResponse(null, -32700, exception.Message);
                await McpStdioFraming.WriteAsync(output, error, contentLengthFraming, _writes, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (inbound is null) break;
            if (firstFraming)
            {
                contentLengthFraming = inbound.ContentLengthFraming;
                firstFraming = false;
            }

            JsonElement? response;
            using (inbound.Document)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                response = await HandleAsync(inbound.Document.RootElement, timeout.Token).ConfigureAwait(false);
            }

            if (response is { } value)
            {
                await McpStdioFraming.WriteAsync(output, value, contentLengthFraming, _writes, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Convenience entry point for an executable host: read-only tools over stdio.</summary>
    public static async Task<int> RunStdioAsync(
        McpAccounts? accounts = null,
        KolibriMcpServerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await new KolibriMcpServer(accounts, options) { Authorizer = McpAccessPolicy.ReadOnly }
            .RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Runs before every authorized tool call; used to rebind role clients to the current sessions.</summary>
    public Func<CancellationToken, Task>? BeforeToolCall { get; set; }

    private async Task<JsonElement> CallToolAsync(
        JsonElement id,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            return ErrorResponse(id, -32602, "tools/call params must be an object.");

        var name = StringProperty(parameters, "name");
        if (string.IsNullOrWhiteSpace(name))
            return ErrorResponse(id, -32602, "tools/call requires a tool name.");

        if (_aliases.TryGetValue(name, out var canonical))
            name = canonical;
        Func<JsonElement, JsonElement>? previewOnly = null;
        if (_previewAliases.TryGetValue(name, out var preview))
            (name, previewOnly) = preview;
        if (!_registry.TryGet(name, out var tool))
            return ErrorResponse(id, -32602, $"Unknown MCP tool '{name}'.");

        var arguments = parameters.TryGetProperty("arguments", out var args)
            ? McpJson.CloneOrEmpty(args)
            : McpJson.EmptyObject;
        if (arguments.ValueKind != JsonValueKind.Object)
            return ErrorResponse(id, -32602, "Tool arguments must be an object.");
        if (previewOnly is not null)
        {
            try { arguments = previewOnly(arguments); }
            catch (McpInvalidParamsException exception) { return ToolErrorResponse(id, exception.Message); }
        }

        var authorization = Authorizer?.Invoke(name, arguments) ?? ToolAuthorization.Ok();
        if (!authorization.Allowed)
            return ToolErrorResponse(id, authorization.Message ?? "Not allowed.");

        if (BeforeToolCall is { } beforeCall)
        {
            // Let role clients follow a login/logout/renewal that happened while the server runs.
            try { await beforeCall(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
        }

        if (authorization.Plan is { } plan)
        {
            // Execute asked without a plan: run as preview only and hand out the single-use plan id.
            try
            {
                var previewArgs = ForcePreview(arguments);
                var details = await tool!.Handler(previewArgs, cancellationToken).ConfigureAwait(false);
                return SuccessResponse(id, ToolResult(new
                {
                    preview = true, planId = plan.PlanId, expiresAt = plan.ExpiresAt, details,
                    next = $"Call {name} again with the same arguments and this planId to execute."
                }));
            }
            catch (McpInvalidParamsException exception) { return ToolErrorResponse(id, exception.Message); }
            catch (Exception exception) { return ToolErrorResponse(id, SanitizeError(exception)); }
        }

        var confirmed = false;
        var booking = new UsageBooking();
        _booking.Value = booking;
        try
        {
            var value = await tool!.Handler(arguments, cancellationToken).ConfigureAwait(false);
            confirmed = true;
            var serialized = JsonSerializer.Serialize(value, McpJson.Options);
            using var document = JsonDocument.Parse(serialized);
            var structured = document.RootElement.Clone();
            var result = new
            {
                content = new[] { new { type = "text", text = serialized } },
                structuredContent = structured,
                isError = false
            };
            return SuccessResponse(id, result);
        }
        catch (McpInvalidParamsException exception)
        {
            return ToolErrorResponse(id, exception.Message);
        }
        catch (Exception exception)
        {
            return ToolErrorResponse(id, SanitizeError(exception));
        }
        finally
        {
            // Requested amount counts only when the handler ran through; a failed call frees the hold.
            // A tool may book fewer units than it reserved (a run that sent nothing books 0).
            if (confirmed) authorization.Reservation?.Commit(
                booking.Units is { } units ? Math.Min(units, authorization.Reservation.Amount) : authorization.Reservation.Amount, name);
            else authorization.Reservation?.Release();
        }
    }

    private sealed class UsageBooking { public long? Units { get; set; } }

    private readonly AsyncLocal<UsageBooking?> _booking = new();

    /// <summary>Lets the running tool book the units it really used instead of the reserved amount.</summary>
    private void BookUnits(long units) { if (_booking.Value is { } booking) booking.Units = Math.Max(0, units); }

    private object InitializeResult()
        => new
        {
            protocolVersion = _options.ProtocolVersion,
            capabilities = new { tools = new { } },
            serverInfo = new { name = _options.ServerName, version = _options.ServerVersion },
            instructions = $"KoLibri {_options.ServerVersion} (early alpha): " + (_options.ExperimentalTools
                ? "read tools for status, inventory, pricing, shop, arbitrage plans and workflow previews, plus write tools (mall buy, transfer, messages, item use, consume, rest, reprice, meme shop, adventures) that need a granted right."
                : "read tools for status, inventory, mall prices, choices, fights and the daily audit, plus write tools (adventures and choices, item use, eat/drink, rest, mall buy, KMail, meme shop) that need a granted right.")
                + " Every write is two-step: the first call only previews and returns a planId, the second call with the same arguments plus planId executes once. Preview and read tools send no purchase or action. Always show the user the preview and get their confirmation before executing; if a result has needsDecision, ask the user instead of guessing. Credentials stay in the configured account runtime."
        };

    private void RegisterReadOnlyTools()
    {
        Register("kol_status", "Read the configured KoL account status snapshot.", RefreshSchema(), StatusAsync);
        Register("kol_inventory", "Read the configured KoL inventory snapshot.", RefreshSchema(), InventoryAsync);
        Register("kol_price_summary", "Summarize cached/read-only buy and sell observations for an item.", ItemQuerySchema(), PriceSummaryAsync);
        RegisterArbitrageTool();
        RegisterAdventurePreviewTools();
        if (_executionBridge is not null) RegisterAdventureExecutionTools();
        RegisterDailyPlannerTools();
        RegisterLoginTools();
        RegisterDailyAuditTool();
        RegisterDailyBridgeTool();

        // Friendly command-shaped aliases are accepted by tools/call while the
        // advertised list stays the fixed, documented `kol_*` surface.
        Alias("status", "kol_status");
        Alias("inventory", "kol_inventory");
        Alias("price", "kol_price_summary");
        Alias("price-summary", "kol_price_summary");

        // 0.5: the old review-only previews are the preview step of the two-step write tools (planId is dropped,
        // so these names never buy or send). Experimental only, like the tools they replace.
        if (_options.ExperimentalTools)
        {
            PreviewAlias("kol_preview_purchase", "kol_mall_buy", PurchasePreviewArgs);
            PreviewAlias("preview-purchase", "kol_mall_buy", PurchasePreviewArgs);
            PreviewAlias("kol_preview_transfer", "kol_transfer", TransferPreviewArgs);
            PreviewAlias("preview-transfer", "kol_transfer", TransferPreviewArgs);
        }
    }

    private void Register(
        string name,
        string description,
        JsonElement schema,
        Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        // Outside the core, a tool exists only when experimental tools are on: not listed, not callable.
        if (!_options.ExperimentalTools && !McpToolSurface.IsCore(name)) return;
        _registry.Register(new McpToolDefinition(name, description, schema, handler));
    }

    /// <summary>True when this server also registers the experimental tools.</summary>
    public bool ExperimentalTools => _options.ExperimentalTools;

    /// <summary>Rights the registered tools can use; the consent page offers only these.</summary>
    public IReadOnlyList<string> OfferedRights
        => McpToolSurface.RightsFor(_registry.Tools.Select(t => t.Name), _options.ExperimentalTools);

    private void Alias(string alias, string canonical)
        => _aliases[alias] = canonical;

    /// <summary>An old name that maps onto the preview step of a two-step tool: arguments are translated, planId is removed.</summary>
    private void PreviewAlias(string alias, string canonical, Func<JsonElement, JsonElement> map)
        => _previewAliases[alias] = (canonical, args => map(args));

    /// <summary>0.4 kol_preview_purchase arguments (itemId as string, unitPrice) as a kol_mall_buy preview.</summary>
    private JsonElement PurchasePreviewArgs(JsonElement args)
    {
        var itemId = NullableIntProperty(args, "itemId") ?? throw new McpInvalidParamsException("itemId is required.");
        var itemName = OptionalString(args, "itemName") ?? _accounts.Client.Data.Item(itemId)?.Name
            ?? throw new McpInvalidParamsException("itemName is required (item unknown in the data).");
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["itemId"] = itemId,
            ["itemName"] = itemName,
            ["quantity"] = NullableIntProperty(args, "quantity") ?? 0,
            ["maxPricePerUnit"] = LongProperty(args, "maxPricePerUnit", LongProperty(args, "unitPrice")),
        };
        if (OptionalString(args, "role") is { } role) node["role"] = role;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    /// <summary>0.4 kol_preview_transfer arguments (from/to, itemId as string) as a kol_transfer preview.</summary>
    private static JsonElement TransferPreviewArgs(JsonElement args)
    {
        var from = OptionalString(args, "from") ?? OptionalString(args, "role");
        var to = OptionalString(args, "to");
        if (from is not null && to is not null && string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new McpInvalidParamsException("from and to must be different accounts.");
        var node = new System.Text.Json.Nodes.JsonObject();
        if (from is not null) node["role"] = from;
        if (NullableIntProperty(args, "itemId") is { } itemId) node["itemId"] = itemId;
        if (NullableIntProperty(args, "quantity") is { } quantity) node["quantity"] = quantity;
        if (NullableIntProperty(args, "meat") is { } meat) node["meat"] = meat;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private async Task<object?> StatusAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        var status = await session.Character.GetStatusAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        var equipment = await session.Inventory.GetEquipmentAsync(false, cancellationToken).ConfigureAwait(false);
        return McpDocuments.Status(status, equipment, session.Account.PlayerId);
    }

    private async Task<object?> InventoryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        return McpDocuments.Inventory(await session.Inventory.GetAsync(forceRefresh, cancellationToken).ConfigureAwait(false));
    }

    private async Task<object?> PriceSummaryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var itemId = RequiredString(arguments, "itemId");
        if (!int.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new McpInvalidParamsException("itemId must be a numeric KoL item id.");
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        await using var session = await _accounts.OpenAsync(role, cancellationToken).ConfigureAwait(false);
        var offers = await session.Mall.GetOffersAsync(id, forceRefresh, cancellationToken).ConfigureAwait(false);
        return McpDocuments.PriceSummary(itemId, offers);
    }

    /// <summary>The message of the tools that move onto the new library in a follow-up PR. Nothing is sent.</summary>
    internal const string NotYetMessage = "not available in 0.5-dev, moves in a follow-up PR, nothing was sent";

    /// <summary>Handler of a registered tool that is not on the 0.5 library yet (same gate, same consent rights).</summary>
    internal static Func<JsonElement, CancellationToken, Task<object?>> NotYet(string tool)
        => (_, _) => throw new McpInvalidParamsException($"{tool}: {NotYetMessage}");


    private static T Deserialize<T>(JsonElement element, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(element, McpJson.Options)
                ?? throw new McpInvalidParamsException($"{label} cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new McpInvalidParamsException($"Invalid {label} object: {exception.Message}");
        }
    }

    private static JsonElement? ArrayProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

    private static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value)) throw new McpInvalidParamsException($"{name} is required.");
        return value!;
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool BoolProperty(JsonElement element, string name, bool fallback = false)
        => element.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };

    private static int IntProperty(JsonElement element, string name, int fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return fallback;
    }

    private static int? NullableIntProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return IntProperty(element, name);
    }

    private static long LongProperty(JsonElement element, string name, long fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return fallback;
    }

    private static long? NullableLongProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return LongProperty(element, name);
    }

    private static decimal? DecimalProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number;
        throw new McpInvalidParamsException($"{name} must be a number.");
    }

    private static DateTimeOffset? DateProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        throw new McpInvalidParamsException($"{name} must be an ISO timestamp.");
    }

    private static TimeSpan? ParseDuration(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var hours))
        {
            if (hours <= 0 || double.IsNaN(hours) || double.IsInfinity(hours))
                throw new McpInvalidParamsException($"{name} must be positive.");
            return TimeSpan.FromHours(hours);
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
                return parsed;
            try
            {
                parsed = XmlConvert.ToTimeSpan(text ?? string.Empty);
                if (parsed > TimeSpan.Zero) return parsed;
            }
            catch (FormatException)
            {
                // Fall through to a stable MCP validation error.
            }
        }
        throw new McpInvalidParamsException($"{name} must be a positive duration (for example PT6H).");
    }

    private static object ToolResult(object value)
    {
        var serialized = JsonSerializer.Serialize(value, McpJson.Options);
        using var document = JsonDocument.Parse(serialized);
        return new
        {
            content = new[] { new { type = "text", text = serialized } },
            structuredContent = document.RootElement.Clone(),
            isError = false
        };
    }

    private static JsonElement ForcePreview(JsonElement arguments)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
        node["execute"] = false;
        node.Remove("planId");
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private AccountRole RoleFor(JsonElement arguments, AccountRole fallback)
        => McpArgs.Role(arguments, fallback);

    private static string StringProperty(JsonElement element, string name)
        => OptionalString(element, name) ?? string.Empty;

    private static JsonElement SuccessResponse(JsonElement id, object result)
    {
        var response = new { jsonrpc = "2.0", id, result };
        return ParseObject(response);
    }

    private static JsonElement ErrorResponse(JsonElement? id, int code, string message)
    {
        var actualId = id is { } value ? value : JsonDocument.Parse("null").RootElement.Clone();
        return ParseObject(new { jsonrpc = "2.0", id = actualId, error = new { code, message } });
    }

    private static JsonElement ToolErrorResponse(JsonElement id, string message)
    {
        var result = new
        {
            content = new[] { new { type = "text", text = message } },
            isError = true
        };
        return SuccessResponse(id, result);
    }

    private static JsonElement ParseObject(object value)
    {
        var json = JsonSerializer.Serialize(value, McpJson.Options);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SanitizeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 500) message = message[..500];
        if (message.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("session", StringComparison.OrdinalIgnoreCase) && message.Contains("token", StringComparison.OrdinalIgnoreCase))
            return "Read-only request failed.";
        return string.IsNullOrWhiteSpace(message) ? "Read-only request failed." : message;
    }

    private static JsonElement RefreshSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"forceRefresh\":{\"type\":\"boolean\",\"default\":false},\"role\":{\"enum\":[\"Main\",\"Mallbot\"]}},\"additionalProperties\":false}");

    private static JsonElement ItemQuerySchema()
        => Schema("{\"type\":\"object\",\"required\":[\"itemId\"],\"properties\":{\"itemId\":{\"type\":\"string\",\"minLength\":1},\"forceRefresh\":{\"type\":\"boolean\",\"default\":false},\"role\":{\"enum\":[\"Main\",\"Mallbot\"]}},\"additionalProperties\":false}");

    private static JsonElement Schema(string json) => McpJson.ParseElement(json);
}

/// <summary>Alias with the spelling used by a few hosts and integration tests.</summary>
public sealed class KoLibriMcpServer : KolibriMcpServer
{
    public KoLibriMcpServer(McpAccounts? accounts = null, KolibriMcpServerOptions? options = null, TimeProvider? clock = null)
        : base(accounts, options, clock) { }
}

/// <summary>Short name for hosts that do not use the KoLibri prefix.</summary>
public sealed class McpServer : KolibriMcpServer
{
    public McpServer(McpAccounts? accounts = null, KolibriMcpServerOptions? options = null, TimeProvider? clock = null)
        : base(accounts, options, clock) { }
}

public sealed class McpInvalidParamsException : Exception
{
    public McpInvalidParamsException(string message) : base(message) { }
}
