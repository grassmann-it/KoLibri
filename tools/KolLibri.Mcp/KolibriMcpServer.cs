using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using LibKolibri;
using KoLibri.Core.Basis;
using KoLibri.Core.Markt;
using KoLibri.Core.Spiel;

namespace KolLibri.Mcp;

/// <summary>
/// Minimal read-only MCP host. The host composes the existing LibKolibri/Core
/// contracts and only registers queries, deterministic plans, and previews.
/// No handler in this class submits a KoL request that changes account state.
/// </summary>
public partial class KolibriMcpServer
{
    private readonly LibKolibriRuntime _runtime;
    private readonly KolibriMcpServerOptions _options;
    private readonly McpToolRegistry _registry;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly RoleScopedRuntime _roleRuntime;
    private readonly Dictionary<AccountRole, PricingEngine> _derivedPricing = [];
    private readonly AdventureMcpExecutionBridge? _executionBridge;

    public KolibriMcpServer(
        LibKolibriRuntime? runtime = null,
        KolibriMcpServerOptions? options = null,
        TimeProvider? clock = null,
        RoleScopedRuntime? roleRuntime = null,
        AdventureMcpExecutionBridge? executionBridge = null)
    {
        _runtime = runtime ?? new LibKolibriRuntime();
        _roleRuntime = roleRuntime ?? new RoleScopedRuntime(_runtime);
        _options = options ?? new KolibriMcpServerOptions();
        _options.Validate();
        _clock = clock ?? TimeProvider.System;
        _registry = new McpToolRegistry();
        _executionBridge = executionBridge;
        RegisterReadOnlyTools();
    }

    /// <summary>
    /// Creates a server over role-specific account adapters. The role facade
    /// can contain distinct authenticated clients for Main and Mallbot while
    /// keeping credentials out of every MCP request.
    /// </summary>
    public KolibriMcpServer(
        RoleScopedRuntime roleRuntime,
        KolibriMcpServerOptions? options = null,
        TimeProvider? clock = null,
        AdventureMcpExecutionBridge? executionBridge = null)
        : this(roleRuntime?.Fallback, options, clock, roleRuntime, executionBridge)
    {
    }

    /// <summary>Runtime used to resolve account, mall, pricing and planner contracts.</summary>
    public LibKolibriRuntime Runtime => _runtime;

    /// <summary>Role-aware runtime facade used for each tool invocation.</summary>
    public RoleScopedRuntime RoleRuntime => _roleRuntime;

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

    /// <summary>Convenience entry point for an executable host.</summary>
    public static Task<int> RunStdioAsync(
        LibKolibriRuntime? runtime = null,
        KolibriMcpServerOptions? options = null,
        CancellationToken cancellationToken = default)
        => RunStdioCoreAsync(runtime, options, cancellationToken);

    /// <summary>Runs stdio with role-specific read adapters.</summary>
    public static Task<int> RunStdioAsync(
        RoleScopedRuntime runtime,
        KolibriMcpServerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return RunStdioCoreAsync(runtime, options, cancellationToken);
    }

    private static async Task<int> RunStdioCoreAsync(
        LibKolibriRuntime? runtime,
        KolibriMcpServerOptions? options,
        CancellationToken cancellationToken)
    {
        await new KolibriMcpServer(runtime, options) { Authorizer = McpAccessPolicy.ReadOnly }.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunStdioCoreAsync(
        RoleScopedRuntime runtime,
        KolibriMcpServerOptions? options,
        CancellationToken cancellationToken)
    {
        await new KolibriMcpServer(runtime, options) { Authorizer = McpAccessPolicy.ReadOnly }.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

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
        if (!_registry.TryGet(name, out var tool))
            return ErrorResponse(id, -32602, $"Unknown MCP tool '{name}'.");

        var arguments = parameters.TryGetProperty("arguments", out var args)
            ? McpJson.CloneOrEmpty(args)
            : McpJson.EmptyObject;
        if (arguments.ValueKind != JsonValueKind.Object)
            return ErrorResponse(id, -32602, "Tool arguments must be an object.");

        var authorization = Authorizer?.Invoke(name, arguments) ?? ToolAuthorization.Ok();
        if (!authorization.Allowed)
            return ToolErrorResponse(id, authorization.Message ?? "Not allowed.");

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
            instructions = $"KoLibri {_options.ServerVersion} (early alpha): read tools for status, inventory, pricing, shop, arbitrage plans and workflow previews, plus write tools (mall buy, transfer, messages, item use, consume, rest, reprice, meme shop, adventures) that need a granted right. Every write is two-step: the first call only previews and returns a planId, the second call with the same arguments plus planId executes once. Preview and read tools send no purchase or action. Always show the user the preview and get their confirmation before executing; if a result has needsDecision, ask the user instead of guessing. Credentials stay in the configured account runtime."
        };

    private void RegisterReadOnlyTools()
    {
        Register("kol_status", "Read the configured KoL account status snapshot.", RefreshSchema(), StatusAsync);
        Register("kol_inventory", "Read the configured KoL inventory snapshot.", RefreshSchema(), InventoryAsync);
        Register("kol_price_summary", "Summarize cached/read-only buy and sell observations for an item.", ItemQuerySchema(), PriceSummaryAsync);
        Register("kol_price_recommendation", "Explain a read-only acquisition and resale recommendation.", RecommendationSchema(), RecommendationAsync);
        Register("kol_underprice_preview", "Build a bounded underpriced purchase plan; never submits a purchase.", UnderpriceSchema(), UnderpriceAsync);
        Register("kol_workflow_plan", "Build the deterministic two-account workflow plan in preview mode.", WorkflowSchema(), WorkflowAsync);
        Register("kol_plan_daily_adventures", "Build a daily adventure plan from a read-only state snapshot.", AdventureSchema(), AdventureAsync);
        Register("kol_preview_purchase", "Validate a proposed purchase and return a review object only.", PurchaseSchema(), PurchasePreviewAsync);
        Register("kol_preview_transfer", "Validate a proposed cross-account transfer and return a review object only.", TransferSchema(), TransferPreviewAsync);
        RegisterArbitrageTool();
        RegisterAdventurePreviewTools();
        if (_executionBridge is not null) RegisterAdventureExecutionTools();
        RegisterDailyPlannerTools();
        RegisterLoginTools();
        RegisterDailyAuditTool();
        RegisterDailyBridgeTool();
        if (_executionBridge is not null) RegisterDailyPlanExecutionTools();

        // Friendly command-shaped aliases are accepted by tools/call while the
        // advertised list stays the fixed, documented `kol_*` surface.
        Alias("status", "kol_status");
        Alias("inventory", "kol_inventory");
        Alias("price", "kol_price_summary");
        Alias("price-summary", "kol_price_summary");
        Alias("price-recommendation", "kol_price_recommendation");
        Alias("underprice", "kol_underprice_preview");
        Alias("underprice-preview", "kol_underprice_preview");
        Alias("workflow-plan", "kol_workflow_plan");
        Alias("workflow_plan", "kol_workflow_plan");
        Alias("preview-purchase", "kol_preview_purchase");
        Alias("preview-transfer", "kol_preview_transfer");
    }

    private void Register(
        string name,
        string description,
        JsonElement schema,
        Func<JsonElement, CancellationToken, Task<object?>> handler)
        => _registry.Register(new McpToolDefinition(name, description, schema, handler));

    private void Alias(string alias, string canonical)
        => _aliases[alias] = canonical;

    private async Task<object?> StatusAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        if (_roleRuntime.TryGet<IAccountReadClient>(role, out var readClient))
            return await readClient!.FetchStatusAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        if (_roleRuntime.TryGet<IAccountGateway>(role, out var gateway))
            return await gateway!.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("No account read gateway is registered.");
    }

    private async Task<object?> InventoryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        if (_roleRuntime.TryGet<IAccountReadClient>(role, out var readClient))
            return await readClient!.FetchInventoryAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        if (_roleRuntime.TryGet<IAccountGateway>(role, out var gateway))
            return await gateway!.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("No account read gateway is registered.");
    }

    private async Task<object?> PriceSummaryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var itemId = RequiredString(arguments, "itemId");
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        var pricing = GetPricingEngine(role);
        if (pricing is not null)
            return await pricing.SummarizeAsync(itemId, forceRefresh, cancellationToken).ConfigureAwait(false);

        var mall = await SearchMallAsync(itemId, forceRefresh, cancellationToken, role).ConfigureAwait(false);
        return BuildMallSummary(mall);
    }

    private async Task<object?> RecommendationAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var itemId = RequiredString(arguments, "itemId");
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var acquisitionCost = DecimalProperty(arguments, "acquisitionCost");
        var role = RoleFor(arguments, _options.DefaultRole);
        var pricing = GetPricingEngine(role);
        if (pricing is not null)
            return await pricing.RecommendAsync(itemId, acquisitionCost, forceRefresh, cancellationToken).ConfigureAwait(false);

        var summaryArguments = arguments;
        var summary = await PriceSummaryAsync(summaryArguments, cancellationToken).ConfigureAwait(false) as PriceSummary
            ?? throw new InvalidOperationException("Unable to build a price summary.");
        var reference = summary.WeightedMedianSellPrice ?? summary.LowestSellPrice ?? summary.WeightedMedianBuyPrice;
        var lowest = summary.LowestSellPrice ?? summary.LowestBuyPrice;
        var cost = acquisitionCost ?? summary.LowestBuyPrice;
        decimal? sell = null;
        decimal? profit = null;
        if (cost is not null)
        {
            var floor = cost.Value * 1.10m;
            sell = lowest is null ? floor : Math.Max(floor, lowest.Value - 1m);
            profit = sell - cost;
        }
        return new
        {
            itemId,
            acquisitionCost,
            recommendedBuyPrice = summary.LowestBuyPrice,
            recommendedSellPrice = sell,
            marketReferencePrice = reference,
            expectedProfit = profit,
            isUnderpriced = summary.LowestBuyPrice is not null && reference is not null && summary.LowestBuyPrice <= reference * 0.80m,
            isProfitable = profit is not null && profit >= (cost ?? 0m) * 0.10m,
            isStale = summary.IsStale,
            confidence = summary.QuoteCount == 0 ? 0m : 1m,
            supportingQuotes = Array.Empty<object>(),
            rationale = summary.QuoteCount == 0 ? "No read-only price observations were available." : "Recommendation derived from Mall read observations."
        };
    }

    private async Task<object?> UnderpriceAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var itemId = OptionalString(arguments, "itemId");
        var forceRefresh = BoolProperty(arguments, "forceRefresh");
        var role = RoleFor(arguments, _options.DefaultRole);
        var listingElements = ArrayProperty(arguments, "listings");
        var referenceElements = ArrayProperty(arguments, "references");
        var listings = listingElements is null
            ? new List<UnderpricedPurchaseListing>()
            : listingElements.Value.EnumerateArray().Select(ParseListing).ToList();
        var references = ParseReferences(arguments, referenceElements);
        object? estimate = null;

        if (listings.Count == 0 && !string.IsNullOrWhiteSpace(itemId))
        {
            var mall = await SearchMallAsync(itemId!, forceRefresh, cancellationToken, role).ConfigureAwait(false);
            listings.AddRange(mall.Listings.Select(x => new UnderpricedPurchaseListing(
                x.ItemId, x.StoreId, x.Price, x.Quantity, x.ObservedAt, x.PurchaseLimit, x.StoreName)));
            var market = new PriceSnapshot(mall).Estimate(new MarketPriceEstimatorOptions { TopN = 10 });
            estimate = market;
            references[itemId!] = new RobustPriceReference
            {
                ItemId = itemId,
                ConservativeSellPrice = market.ConservativeSellPrice,
                ReferencePrice = market.MallReferencePrice,
                WeightedMedian = market.MallWeightedMedian,
                AsOf = market.AsOf,
                IsStale = market.IsStale,
                Confidence = market.Confidence,
                ObservedQuantity = market.CumulativeQuantityTopN
                    > int.MaxValue ? int.MaxValue : (int)Math.Max(0, market.CumulativeQuantityTopN)
            };
        }

        if (listings.Count == 0)
            throw new McpInvalidParamsException("underprice preview requires listings or an itemId with a registered mall gateway.");
        if (references.Count == 0)
            throw new McpInvalidParamsException("underprice preview requires at least one independent price reference.");

        var tradeability = ParseTradeability(arguments);
        var limits = ParseLimits(arguments);
        var plan = new UnderpricedPurchasePlanner(limits).Plan(listings, references, tradeability, limits);
        return new
        {
            readOnly = true,
            purchaseExecuted = false,
            itemId,
            fetchedAt = _clock.GetUtcNow(),
            estimate,
            plan
        };
    }

    private Task<object?> WorkflowAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotElement = arguments.TryGetProperty("snapshot", out var nested)
            && nested.ValueKind == JsonValueKind.Object ? nested : arguments;
        var snapshot = ParseWorkflowSnapshot(snapshotElement);
        var plan = new KoLWorkflowPlanner().Build(snapshot, _clock.GetUtcNow());
        return Task.FromResult<object?>(new
        {
            readOnly = true,
            mutationsSupported = false,
            snapshot,
            plan
        });
    }

    private async Task<object?> AdventureAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var stateElement = arguments.TryGetProperty("state", out var nested)
            && nested.ValueKind == JsonValueKind.Object ? nested : arguments;
        var state = ParseAdventureState(stateElement);
        var role = RoleFor(arguments, _options.DefaultRole);
        var hasOptions = arguments.TryGetProperty("options", out var optionsElement)
            && optionsElement.ValueKind == JsonValueKind.Object;
        DailyAdventurePlan plan;
        if (!hasOptions && _roleRuntime.TryGet<IAdventureGateway>(role, out var gateway))
            plan = await gateway!.PlanAsync(state, cancellationToken).ConfigureAwait(false);
        else
            plan = new DailyAdventurePlanner(ParseAdventureOptions(hasOptions ? optionsElement : arguments))
                .Plan(state, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));
        return new
        {
            readOnly = true,
            state,
            plan,
            execution = "preview-only; fights and choices require a future approved adapter"
        };
    }

    private async Task<object?> PurchasePreviewAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = ParsePurchaseRequest(arguments);
        var role = RoleFor(arguments, _options.DefaultRole);
        var listing = ParseMallListing(arguments, request);
        if (listing is null)
        {
            var mall = await SearchMallAsync(request.ItemId, BoolProperty(arguments, "forceRefresh"), cancellationToken, role).ConfigureAwait(false);
            listing = mall.Listings.FirstOrDefault(x => string.Equals(x.StoreId, request.StoreId, StringComparison.Ordinal))
                ?? mall.Listings.FirstOrDefault();
            if (listing is null)
                throw new McpInvalidParamsException("purchase preview could not find a read-only Mall listing.");
        }

        var status = await ParseOrReadStatusAsync(arguments, cancellationToken, role).ConfigureAwait(false);
        var inventory = await ParseOrReadInventoryAsync(arguments, cancellationToken, role).ConfigureAwait(false);
        var budget = ParsePurchaseBudget(arguments, status.Meat);
        var policy = ParsePurchasePolicy(arguments);
        var preview = PurchaseApprovalService.Preview(request, listing, status, inventory, budget, policy, _clock.GetUtcNow());
        return new
        {
            readOnly = true,
            purchaseExecuted = false,
            commitSupported = false,
            preview
        };
    }

    private async Task<object?> TransferPreviewAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var from = RequiredString(arguments, "from");
        var to = RequiredString(arguments, "to");
        var itemId = RequiredString(arguments, "itemId");
        var quantity = IntProperty(arguments, "quantity", 1);
        if (quantity <= 0) throw new McpInvalidParamsException("quantity must be positive.");
        var reason = OptionalString(arguments, "reason") ?? "MCP read-only transfer preview";
        var request = new LibKolibri.TransferRequest(from, to,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [itemId] = quantity }, reason);
        var gateway = _runtime.TryGet<ITransferGateway>(out var configured)
            ? configured!
            : new PreviewOnlyTransferGateway(_clock);
        var preview = await gateway.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        return new { readOnly = true, transferExecuted = false, commitSupported = false, preview };
    }

    private PricingEngine? GetPricingEngine(AccountRole role)
    {
        if (_roleRuntime.TryGet<PricingEngine>(role, out var pricing)) return pricing;
        if (!_roleRuntime.TryGet<IPriceProvider>(role, out var provider)) return null;
        if (_derivedPricing.TryGetValue(role, out var derived)) return derived;
        return _derivedPricing[role] = new PricingEngine(provider!);
    }

    private async Task<MallSnapshot> SearchMallAsync(
        string itemId,
        bool forceRefresh,
        CancellationToken cancellationToken,
        AccountRole role = AccountRole.Mallbot)
    {
        if (_roleRuntime.TryGet<IMallReadClient>(role, out var readClient))
            return await readClient!.SearchAsync(itemId, forceRefresh, cancellationToken).ConfigureAwait(false);
        if (_roleRuntime.TryGet<IMallGateway>(role, out var gateway))
            return await gateway!.SearchAsync(itemId, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("No Mall read gateway is registered.");
    }

    private async Task<KoLStatusSnapshot> ParseOrReadStatusAsync(
        JsonElement arguments,
        CancellationToken cancellationToken,
        AccountRole role = AccountRole.Mallbot)
    {
        if (arguments.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
            return Deserialize<KoLStatusSnapshot>(status, "status");
        if (_roleRuntime.TryGet<IAccountReadClient>(role, out var readClient))
            return await readClient!.FetchStatusAsync(BoolProperty(arguments, "forceRefresh"), cancellationToken).ConfigureAwait(false);
        if (_roleRuntime.TryGet<IAccountGateway>(role, out var gateway))
            return await gateway!.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        throw new McpInvalidParamsException("purchase preview requires status or a registered account gateway.");
    }

    private async Task<KoLInventorySnapshot> ParseOrReadInventoryAsync(
        JsonElement arguments,
        CancellationToken cancellationToken,
        AccountRole role = AccountRole.Mallbot)
    {
        if (arguments.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object)
            return Deserialize<KoLInventorySnapshot>(inventory, "inventory");
        if (_roleRuntime.TryGet<IAccountReadClient>(role, out var readClient))
            return await readClient!.FetchInventoryAsync(BoolProperty(arguments, "forceRefresh"), cancellationToken).ConfigureAwait(false);
        if (_roleRuntime.TryGet<IAccountGateway>(role, out var gateway))
            return await gateway!.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        throw new McpInvalidParamsException("purchase preview requires inventory or a registered account gateway.");
    }

    private static PriceSummary BuildMallSummary(MallSnapshot snapshot)
    {
        var values = snapshot.Listings
            .Where(x => x.Price >= 0 && x.Quantity > 0)
            .OrderBy(x => x.Price)
            .ToArray();
        var weighted = WeightedMedian(values.Select(x => ((decimal)x.Price, (long)x.Quantity)).ToArray());
        return new PriceSummary(
            snapshot.ItemId,
            null,
            values.Length == 0 ? null : values[0].Price,
            weighted,
            null,
            values.Length,
            snapshot.FetchedAt,
            snapshot.IsStale);
    }

    private static decimal? WeightedMedian(IReadOnlyList<(decimal Value, long Weight)> values)
    {
        if (values.Count == 0) return null;
        var ordered = values.Where(x => x.Weight > 0).OrderBy(x => x.Value).ToArray();
        if (ordered.Length == 0) return null;
        var target = ordered.Sum(x => x.Weight) / 2m;
        long running = 0;
        foreach (var value in ordered)
        {
            running += value.Weight;
            if (running >= target) return value.Value;
        }
        return ordered[^1].Value;
    }

    private static UnderpricedPurchaseListing ParseListing(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new McpInvalidParamsException("Each underprice listing must be an object.");
        return new UnderpricedPurchaseListing
        {
            ItemId = RequiredString(element, "itemId"),
            SellerId = OptionalString(element, "sellerId") ?? OptionalString(element, "seller") ?? RequiredString(element, "storeId"),
            UnitPrice = DecimalProperty(element, "unitPrice") ?? DecimalProperty(element, "price") ?? throw new McpInvalidParamsException("Listing unitPrice is required."),
            Quantity = IntProperty(element, "quantity", 0),
            ObservedAt = DateProperty(element, "observedAt"),
            SellerPurchaseLimit = NullableIntProperty(element, "sellerPurchaseLimit") ?? NullableIntProperty(element, "purchaseLimit"),
            DisplayName = OptionalString(element, "displayName")
        };
    }

    private static Dictionary<string, RobustPriceReference> ParseReferences(JsonElement arguments, JsonElement? array)
    {
        var references = new Dictionary<string, RobustPriceReference>(StringComparer.OrdinalIgnoreCase);
        if (array is { } values)
        {
            foreach (var element in values.EnumerateArray())
            {
                var reference = Deserialize<RobustPriceReference>(element, "reference");
                var key = reference.ItemId ?? OptionalString(element, "itemId");
                if (!string.IsNullOrWhiteSpace(key)) references[key!] = reference with { ItemId = key };
            }
        }
        if (arguments.TryGetProperty("references", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in map.EnumerateObject())
            {
                var reference = property.Value.ValueKind == JsonValueKind.Number
                    ? new RobustPriceReference(DecimalProperty(map, property.Name) ?? 0m)
                    : Deserialize<RobustPriceReference>(property.Value, "reference");
                references[property.Name] = reference with { ItemId = property.Name };
            }
        }
        return references;
    }

    private static IReadOnlyDictionary<string, UnderpricedItemTradeability>? ParseTradeability(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("tradeability", out var element) || element.ValueKind != JsonValueKind.Object)
            return null;
        var values = new Dictionary<string, UnderpricedItemTradeability>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = Deserialize<UnderpricedItemTradeability>(property.Value, "tradeability");
        return values;
    }

    private static UnderpricedPurchaseLimits ParseLimits(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return new UnderpricedPurchaseLimits();
        var duration = ParseDuration(limits, "maximumReferenceAge");
        if (duration is null)
            return Deserialize<UnderpricedPurchaseLimits>(limits, "limits");

        // System.Text.Json has no built-in TimeSpan converter in this target;
        // remove the duration before deserializing the other limits, then put
        // the parsed value back into the typed contract.
        var node = JsonNode.Parse(limits.GetRawText())?.AsObject()
            ?? throw new McpInvalidParamsException("Invalid limits object.");
        node.Remove("maximumReferenceAge");
        var parsed = Deserialize<UnderpricedPurchaseLimits>(node.Deserialize<JsonElement>(), "limits");
        return parsed with { MaximumReferenceAge = duration.Value };
    }

    private static KoLWorkflowSnapshot ParseWorkflowSnapshot(JsonElement element)
        => new()
        {
            MainSessionValid = BoolProperty(element, "mainSessionValid"),
            MallbotSessionValid = BoolProperty(element, "mallbotSessionValid"),
            MallbotMeat = LongProperty(element, "mallbotMeat"),
            ForeignBribeBagPrice = NullableLongProperty(element, "foreignBribeBagPrice"),
            ForeignBribeBagStock = IntProperty(element, "foreignBribeBagStock"),
            PilotBagQuantity = IntProperty(element, "pilotBagQuantity", 40),
            MallbotMeatReserve = LongProperty(element, "mallbotMeatReserve", 1_000_000),
            MainCanOpenBags = BoolProperty(element, "mainCanOpenBags"),
            MainCanAdventure = BoolProperty(element, "mainCanAdventure"),
            MainCanUseHermit = BoolProperty(element, "mainCanUseHermit"),
            AdventurePlanReady = BoolProperty(element, "adventurePlanReady"),
            PriceSourcesFresh = BoolProperty(element, "priceSourcesFresh"),
            OwnShopScanned = BoolProperty(element, "ownShopScanned"),
            InventoryScanned = BoolProperty(element, "inventoryScanned")
        };

    private static AdventureState ParseAdventureState(JsonElement element)
        => new()
        {
            Knucklebones = IntProperty(element, "knucklebones"),
            AdventureTurnsRemaining = IntProperty(element, "adventureTurnsRemaining"),
            LuckySurpriseEggs = IntProperty(element, "luckySurpriseEggs"),
            ForeignBribeBags = IntProperty(element, "foreignBribeBags"),
            PirateEncryptionKeys = IntProperty(element, "pirateEncryptionKeys"),
            ElfGuardPresents = IntProperty(element, "elfGuardPresents"),
            CrimbucaneerBooty = IntProperty(element, "crimbucaneerBooty"),
            SkeletonOfCrimbosPastAvailable = BoolProperty(element, "skeletonOfCrimbosPastAvailable")
        };

    private static AdventurePlannerOptions ParseAdventureOptions(JsonElement element)
        => new()
        {
            DailyKnucklebonesTarget = IntProperty(element, "dailyKnucklebonesTarget", 100),
            ExpectedKnucklebonesPerAdventure = DecimalProperty(element, "expectedKnucklebonesPerAdventure") ?? 0.5m,
            ExpectedKnucklebonesPerLuckySurpriseEgg = DecimalProperty(element, "expectedKnucklebonesPerLuckySurpriseEgg") ?? 0m,
            ExpectedAdventureTurnsPerLuckySurpriseEgg = IntProperty(element, "expectedAdventureTurnsPerLuckySurpriseEgg", 1),
            ConsumeLuckySurpriseEggs = BoolProperty(element, "consumeLuckySurpriseEggs", true),
            ConsumeEggsWhenTargetAlreadyReached = BoolProperty(element, "consumeEggsWhenTargetAlreadyReached", true),
            MaxLuckySurpriseEggsPerPlan = IntProperty(element, "maxLuckySurpriseEggsPerPlan", 15),
            MaxAdventureRunsPerPlan = IntProperty(element, "maxAdventureRunsPerPlan", 500)
        };

    private static PurchaseRequest ParsePurchaseRequest(JsonElement arguments)
    {
        var source = arguments.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object
            ? request : arguments;
        var itemId = RequiredString(source, "itemId");
        var storeId = OptionalString(source, "storeId") ?? OptionalString(source, "sellerId") ?? "unknown";
        var unit = DecimalProperty(source, "unitPrice") ?? DecimalProperty(source, "expectedUnitPrice")
            ?? throw new McpInvalidParamsException("unitPrice is required.");
        if (unit < 0 || unit > long.MaxValue) throw new McpInvalidParamsException("unitPrice must be a non-negative integer-sized value.");
        var quantity = IntProperty(source, "quantity", 0);
        if (quantity <= 0) throw new McpInvalidParamsException("quantity must be positive.");
        return new PurchaseRequest(itemId, storeId, decimal.ToInt64(unit), quantity, OptionalString(source, "expectedStoreName"));
    }

    private static MallListing? ParseMallListing(JsonElement arguments, PurchaseRequest request)
    {
        if (!arguments.TryGetProperty("listing", out var element) || element.ValueKind != JsonValueKind.Object)
            return null;
        var itemId = OptionalString(element, "itemId") ?? request.ItemId;
        var storeId = OptionalString(element, "storeId") ?? request.StoreId;
        var price = DecimalProperty(element, "price") ?? DecimalProperty(element, "unitPrice") ?? request.ExpectedUnitPrice;
        var quantity = IntProperty(element, "quantity", 0);
        if (quantity <= 0) throw new McpInvalidParamsException("listing.quantity must be positive.");
        return new MallListing(
            itemId,
            storeId,
            OptionalString(element, "storeName"),
            decimal.ToInt64(price),
            quantity,
            NullableIntProperty(element, "purchaseLimit"),
            DateProperty(element, "observedAt") ?? DateTimeOffset.UtcNow,
            new Uri("https://www.kingdomofloathing.com/mall.php"));
    }

    private static PurchaseBudget ParsePurchaseBudget(JsonElement arguments, long availableMeat)
    {
        if (!arguments.TryGetProperty("budget", out var element) || element.ValueKind != JsonValueKind.Object)
            return new PurchaseBudget { AvailableMeat = availableMeat };
        var budget = Deserialize<PurchaseBudget>(element, "budget");
        return budget with { AvailableMeat = budget.AvailableMeat == 0 ? availableMeat : budget.AvailableMeat };
    }

    private static PurchaseApprovalPolicy ParsePurchasePolicy(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("policy", out var element) || element.ValueKind != JsonValueKind.Object)
            return new PurchaseApprovalPolicy();
        return Deserialize<PurchaseApprovalPolicy>(element, "policy");
    }

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

    private static JsonElement RecommendationSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"itemId\"],\"properties\":{\"itemId\":{\"type\":\"string\",\"minLength\":1},\"acquisitionCost\":{\"type\":[\"number\",\"null\"],\"minimum\":0},\"forceRefresh\":{\"type\":\"boolean\",\"default\":false},\"role\":{\"enum\":[\"Main\",\"Mallbot\"]}},\"additionalProperties\":false}");

    private static JsonElement UnderpriceSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"itemId\":{\"type\":\"string\"},\"forceRefresh\":{\"type\":\"boolean\"},\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},\"listings\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"references\":{\"type\":\"object\"},\"limits\":{\"type\":\"object\"},\"tradeability\":{\"type\":\"object\"}},\"additionalProperties\":true}");

    private static JsonElement WorkflowSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"snapshot\":{\"type\":\"object\"},\"mainSessionValid\":{\"type\":\"boolean\"},\"mallbotSessionValid\":{\"type\":\"boolean\"},\"mallbotMeat\":{\"type\":\"integer\"},\"foreignBribeBagPrice\":{\"type\":[\"integer\",\"null\"]},\"foreignBribeBagStock\":{\"type\":\"integer\"},\"pilotBagQuantity\":{\"type\":\"integer\"},\"mallbotMeatReserve\":{\"type\":\"integer\"}},\"additionalProperties\":true}");

    private static JsonElement AdventureSchema()
        => Schema("{\"type\":\"object\",\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},\"state\":{\"type\":\"object\"},\"options\":{\"type\":\"object\"},\"knucklebones\":{\"type\":\"integer\"},\"adventureTurnsRemaining\":{\"type\":\"integer\"}},\"additionalProperties\":true}");

    private static JsonElement PurchaseSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"itemId\",\"quantity\",\"unitPrice\"],\"properties\":{\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},\"itemId\":{\"type\":\"string\",\"minLength\":1},\"storeId\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\",\"minimum\":1},\"unitPrice\":{\"type\":\"number\",\"minimum\":0},\"approvalToken\":{\"type\":[\"string\",\"null\"]},\"listing\":{\"type\":\"object\"},\"status\":{\"type\":\"object\"},\"inventory\":{\"type\":\"object\"},\"budget\":{\"type\":\"object\"},\"policy\":{\"type\":\"object\"}},\"additionalProperties\":true}");

    private static JsonElement TransferSchema()
        => Schema("{\"type\":\"object\",\"required\":[\"itemId\",\"quantity\",\"from\",\"to\"],\"properties\":{\"itemId\":{\"type\":\"string\",\"minLength\":1},\"quantity\":{\"type\":\"integer\",\"minimum\":1},\"from\":{\"enum\":[\"Main\",\"Mallbot\"]},\"to\":{\"enum\":[\"Main\",\"Mallbot\"]},\"reason\":{\"type\":\"string\"},\"approvalToken\":{\"type\":[\"string\",\"null\"]}},\"additionalProperties\":false}");

    private static JsonElement Schema(string json) => McpJson.ParseElement(json);
}

/// <summary>Alias with the spelling used by a few hosts and integration tests.</summary>
public sealed class KoLibriMcpServer : KolibriMcpServer
{
    public KoLibriMcpServer(LibKolibriRuntime? runtime = null, KolibriMcpServerOptions? options = null, TimeProvider? clock = null)
        : base(runtime, options, clock) { }

    public KoLibriMcpServer(RoleScopedRuntime runtime, KolibriMcpServerOptions? options = null, TimeProvider? clock = null)
        : base(runtime, options, clock) { }
}

/// <summary>Generic MCP host alias for embedding applications.</summary>
public sealed class McpServer : KolibriMcpServer
{
    public McpServer(LibKolibriRuntime? runtime = null, KolibriMcpServerOptions? options = null, TimeProvider? clock = null)
        : base(runtime, options, clock) { }

    public McpServer(RoleScopedRuntime runtime, KolibriMcpServerOptions? options = null, TimeProvider? clock = null)
        : base(runtime, options, clock) { }
}

public sealed class McpInvalidParamsException : Exception
{
    public McpInvalidParamsException(string message) : base(message) { }
}
