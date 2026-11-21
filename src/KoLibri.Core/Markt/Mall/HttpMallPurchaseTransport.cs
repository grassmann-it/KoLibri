using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLibri.Core.Spiel;

namespace KoLibri.Core.Markt;

/// <summary>
/// How an HTTP response at the Mall mutation boundary was classified.  A
/// response which does not prove that KoL accepted the order is never treated
/// as an accepted purchase.
/// </summary>
public enum MallPurchaseHttpResponseClassification
{
    Accepted,
    Rejected,
    LoginRequired,
    RateLimited,
    ServerError,
    Redirected,
    Indeterminate,
    NetworkFailure,
    InvalidResponse
}

/// <summary>
/// A safe summary of an HTTP response.  The body is intentionally omitted so
/// cookies, account data and a server generated page cannot leak into audit or
/// UI messages.
/// </summary>
public sealed record MallPurchaseHttpResponse(
    MallPurchaseHttpResponseClassification Classification,
    HttpStatusCode? StatusCode,
    Uri? RequestUri,
    string? Message = null)
{
    public bool IsAccepted => Classification == MallPurchaseHttpResponseClassification.Accepted;
}

/// <summary>Options for the explicitly invoked HTTP Mall transport.</summary>
public sealed record HttpMallPurchaseTransportOptions
{
    public Uri BaseUri { get; init; } = new("https://www.kingdomofloathing.com/", UriKind.Absolute);

    /// <summary>
    /// Exact hosts accepted for requests.  The default contains only the
    /// official KoL hosts.  A test host must be added explicitly; it is never
    /// accepted merely because it is the injected HttpClient's BaseAddress.
    /// </summary>
    public IReadOnlyCollection<string> AllowedHosts { get; init; } =
        new[] { "www.kingdomofloathing.com", "kingdomofloathing.com" };

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MinimumRequestInterval { get; init; } = TimeSpan.Zero;
    public string PurchasePath { get; init; } = KoLRoutes.MallStore;
    public string ListingPath { get; init; } = KoLRoutes.MallStore;
    public string StatusPath { get; init; } = KoLRoutes.Api;

    /// <summary>
    /// KoL's mall page identifies a store in the URL.  Some mirrors also
    /// render a seller id.  Requiring it can be enabled by hosts that have a
    /// parser with that information; the default keeps the official page
    /// compatible while still pinning the requested seller in every request.
    /// </summary>
    public bool RequireSellerIdOnRead { get; init; } = true;

    public void Validate()
    {
        if (BaseUri is null || !BaseUri.IsAbsoluteUri)
            throw new ArgumentException("Mall base URI must be absolute.", nameof(BaseUri));
        if (!string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Live Mall requests require HTTPS.", nameof(BaseUri));
        if (string.IsNullOrWhiteSpace(BaseUri.Host))
            throw new ArgumentException("Mall base URI must contain a host.", nameof(BaseUri));
        if (AllowedHosts is null || AllowedHosts.Count == 0)
            throw new ArgumentException("At least one allowed Mall host is required.", nameof(AllowedHosts));
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MinimumRequestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinimumRequestInterval));
        if (string.IsNullOrWhiteSpace(PurchasePath))
            throw new ArgumentException("Purchase path is required.", nameof(PurchasePath));
        if (string.IsNullOrWhiteSpace(ListingPath))
            throw new ArgumentException("Listing path is required.", nameof(ListingPath));
        if (string.IsNullOrWhiteSpace(StatusPath))
            throw new ArgumentException("Status path is required.", nameof(StatusPath));
    }
}

/// <summary>
/// Optional read hook supplied by an account host.  It must bypass stale
/// caches and perform a fresh, consistent offer/account read on every call.
/// </summary>
public interface IMallPurchaseFreshReadHook
{
    Task<MallPurchaseObservation> ReadFreshAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional post-commit verification hook supplied by an account host.  It is
/// called only after KoL has acknowledged a mutation (or when a response is
/// indeterminate and reconciliation is required).
/// </summary>
public interface IMallPurchaseVerificationHook
{
    Task<MallPurchaseObservation> ReadAfterCommitAsync(
        MallPurchaseTransportCommit commit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit live HTTP implementation of <see cref="IMallPurchaseTransport"/>.
/// Construction is side-effect free.  No request is made until RefreshAsync
/// or CommitAsync is called by an approval-bound gateway.
///
/// The transport deliberately does not retry POST.  KoL may have applied a
/// purchase even when the client sees a timeout, so an ambiguous POST is
/// reconciled with one fresh read instead of being submitted again.
/// </summary>
public sealed class HttpMallPurchaseTransport : IMallPurchaseTransport, IDisposable
{
    private static readonly Regex InputTag = new(
        "<input\\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex NameAttribute = new(
        "\\bname\\s*=\\s*[\\\"'](?<name>[^\\\"']+)[\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValueAttribute = new(
        "\\bvalue\\s*=\\s*[\\\"'](?<value>[^\\\"']*)[\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DataAttribute = new(
        "\\bdata-(?<name>[a-z0-9_-]+)\\s*=\\s*[\\\"'](?<value>[^\\\"']*)[\\\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StockRow = new(
        "stock_(?<store>\\d+)_(?<item>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SellerLink = new(
        "(?:showplayer\\.php\\?[^\\\"' >]*?who=|\\b(?:seller|player)(?:id)?\\s*[=:]\\s*)(?<seller>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
    private static readonly Regex NumberWithMeat = new(
        "(?<value>[0-9][0-9,]*)\\s*(?:&nbsp;)?(?:meat|meat\\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuantityText = new(
        "(?:stock|quantity|available|in stock)[^0-9]{0,32}(?<value>[0-9][0-9,]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LoginMarker = new(
        "(?:name\\s*=\\s*[\\\"']loginname[\\\"']|action\\s*=\\s*[\\\"']login\\.php[\\\"']|you must log in|please log in)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly HttpMallPurchaseTransportOptions _options;
    private readonly HashSet<string> _allowedHosts;
    private readonly TimeProvider _clock;
    private readonly IMallPurchaseFreshReadHook? _freshReadHook;
    private readonly IMallPurchaseVerificationHook? _verificationHook;
    private readonly Func<MallPurchaseRequest, CancellationToken, Task<MallPurchaseObservation>>? _freshRead;
    private readonly Func<MallPurchaseTransportCommit, CancellationToken, Task<MallPurchaseObservation>>? _verify;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset? _lastRequest;
    private bool _disposed;

    public HttpMallPurchaseTransport(
        HttpClient httpClient,
        HttpMallPurchaseTransportOptions? options = null,
        IMallPurchaseFreshReadHook? freshReadHook = null,
        IMallPurchaseVerificationHook? verificationHook = null,
        TimeProvider? clock = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new HttpMallPurchaseTransportOptions();
        _options.Validate();
        _allowedHosts = NormalizeHosts(_options.AllowedHosts);
        EnsureAllowedHost(_options.BaseUri);
        _freshReadHook = freshReadHook;
        _verificationHook = verificationHook;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Delegate-based overload convenient for small hosts and tests.</summary>
    public HttpMallPurchaseTransport(
        HttpClient httpClient,
        HttpMallPurchaseTransportOptions? options,
        Func<MallPurchaseRequest, CancellationToken, Task<MallPurchaseObservation>>? freshRead,
        Func<MallPurchaseTransportCommit, CancellationToken, Task<MallPurchaseObservation>>? verify,
        TimeProvider? clock = null)
        : this(httpClient, options,
            (IMallPurchaseFreshReadHook?)null,
            (IMallPurchaseVerificationHook?)null,
            clock)
    {
        _freshRead = freshRead;
        _verify = verify;
    }

    public HttpMallPurchaseTransport(
        HttpClient httpClient,
        Func<MallPurchaseRequest, CancellationToken, Task<MallPurchaseObservation>>? freshRead,
        Func<MallPurchaseTransportCommit, CancellationToken, Task<MallPurchaseObservation>>? verify,
        TimeProvider? clock = null)
        : this(httpClient, (HttpMallPurchaseTransportOptions?)null, freshRead, verify, clock)
    {
    }

    /// <summary>Base-uri convenience overload matching the read adapters.</summary>
    public HttpMallPurchaseTransport(
        HttpClient httpClient,
        Uri baseUri,
        HttpMallPurchaseTransportOptions? options = null,
        IMallPurchaseFreshReadHook? freshReadHook = null,
        IMallPurchaseVerificationHook? verificationHook = null,
        TimeProvider? clock = null)
        : this(httpClient,
            (options ?? new HttpMallPurchaseTransportOptions()) with { BaseUri = baseUri },
            freshReadHook,
            verificationHook,
            clock)
    {
    }

    /// <summary>Delegate-based base-uri overload.</summary>
    public HttpMallPurchaseTransport(
        HttpClient httpClient,
        Uri baseUri,
        HttpMallPurchaseTransportOptions? options,
        Func<MallPurchaseRequest, CancellationToken, Task<MallPurchaseObservation>>? freshRead,
        Func<MallPurchaseTransportCommit, CancellationToken, Task<MallPurchaseObservation>>? verify,
        TimeProvider? clock = null)
        : this(httpClient,
            (options ?? new HttpMallPurchaseTransportOptions()) with { BaseUri = baseUri },
            freshRead,
            verify,
            clock)
    {
    }

    public HttpMallPurchaseTransport(
        HttpClient httpClient,
        Uri baseUri,
        Func<MallPurchaseRequest, CancellationToken, Task<MallPurchaseObservation>>? freshRead,
        Func<MallPurchaseTransportCommit, CancellationToken, Task<MallPurchaseObservation>>? verify,
        TimeProvider? clock = null)
        : this(httpClient, baseUri, null, freshRead, verify, clock)
    {
    }

    public HttpMallPurchaseTransportOptions Options => _options;
    public MallPurchaseHttpResponse? LastResponse { get; private set; }

    /// <summary>Explicit fresh-read entry point for host verification code.</summary>
    public Task<MallPurchaseObservation> FreshReadAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default)
        => RefreshAsync(request, cancellationToken);

    /// <summary>Explicit post-commit verification entry point for hosts.</summary>
    public Task<MallPurchaseObservation> VerifyAfterCommitAsync(
        MallPurchaseTransportCommit request,
        CancellationToken cancellationToken = default)
        => ReadAfterCommitAsync(request, cancellationToken);

    public async Task<MallPurchaseObservation> RefreshAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateExactNumerics(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_freshReadHook is not null)
            return await ReadHookAsync(request, cancellationToken).ConfigureAwait(false);
        if (_freshRead is not null)
            return await _freshRead(request, cancellationToken).ConfigureAwait(false);
        return await ReadHttpAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MallPurchaseTransportReceipt> CommitAsync(
        MallPurchaseTransportCommit request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Preview);
        ArgumentNullException.ThrowIfNull(request.Before);
        var purchase = request.Preview.Request;
        purchase.Validate();
        request.Before.Validate();
        ValidateExactNumerics(purchase);
        EnsureExactBefore(purchase, request.Before);
        cancellationToken.ThrowIfCancellationRequested();

        var before = request.Before;
        var uri = BuildPurchaseUri(purchase);
        var values = BuildPurchaseForm(purchase);
        MallPurchaseHttpResponse response;
        string body = string.Empty;
        try
        {
            // This is the one and only mutation attempt.  Do not wrap it in a
            // retry policy: the server may have applied the order already.
            (response, body) = await PostOnceAsync(uri, values, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = Remember(new MallPurchaseHttpResponse(
                MallPurchaseHttpResponseClassification.Indeterminate, null, uri,
                "Mall POST timed out; outcome is indeterminate."));
            return await ReconcileAsync(request, before, response.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            response = Remember(new MallPurchaseHttpResponse(
                MallPurchaseHttpResponseClassification.Indeterminate, null, uri,
                "Mall POST failed before an acknowledgement; outcome is indeterminate."));
            return await ReconcileAsync(request, before,
                $"Mall POST failed; outcome is indeterminate. {SafeMessage(exception)}", cancellationToken)
                .ConfigureAwait(false);
        }

        if (response.Classification == MallPurchaseHttpResponseClassification.Accepted)
        {
            return await BuildAcceptedReceiptAsync(request, before, response, cancellationToken).ConfigureAwait(false);
        }

        if (response.Classification is MallPurchaseHttpResponseClassification.Indeterminate
            or MallPurchaseHttpResponseClassification.ServerError
            or MallPurchaseHttpResponseClassification.RateLimited)
        {
            return await ReconcileAsync(request, before, response.Message, cancellationToken).ConfigureAwait(false);
        }

        return new MallPurchaseTransportReceipt(
            accepted: false,
            before,
            before,
            response.Message ?? response.Classification.ToString(),
            _clock.GetUtcNow());
    }

    private async Task<MallPurchaseObservation> ReadHookAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _freshReadHook!.ReadFreshAsync(request, cancellationToken).ConfigureAwait(false);
        result.Validate();
        EnsureRequestReadMatches(request, result);
        return result;
    }

    private async Task<MallPurchaseTransportReceipt> BuildAcceptedReceiptAsync(
        MallPurchaseTransportCommit commit,
        MallPurchaseObservation before,
        MallPurchaseHttpResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            var after = await ReadAfterCommitAsync(commit, cancellationToken).ConfigureAwait(false);
            return new MallPurchaseTransportReceipt(
                accepted: true, before, after,
                response.Message, _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // KoL acknowledged the mutation.  Preserve that fact while
            // returning the before snapshot so the gateway's verifier marks
            // the result unverified instead of pretending a successful read.
            return new MallPurchaseTransportReceipt(
                accepted: true, before, before,
                $"{response.Message ?? "Mall purchase accepted"} Fresh verification failed: {SafeMessage(exception)}",
                _clock.GetUtcNow());
        }
    }

    private async Task<MallPurchaseTransportReceipt> ReconcileAsync(
        MallPurchaseTransportCommit commit,
        MallPurchaseObservation before,
        string? message,
        CancellationToken cancellationToken)
    {
        try
        {
            var after = await ReadAfterCommitAsync(commit, cancellationToken).ConfigureAwait(false);
            var applied = PurchaseDeltaMatches(commit.Preview.Request, before, after);
            return new MallPurchaseTransportReceipt(
                accepted: applied,
                before,
                after,
                applied
                    ? $"{message ?? "Mall POST outcome was indeterminate."} Fresh read shows the purchase applied."
                    : $"{message ?? "Mall POST outcome was indeterminate."} Fresh read did not show the exact purchase delta.",
                _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MallPurchaseTransportReceipt(
                accepted: false,
                before,
                before,
                $"{message ?? "Mall POST outcome was indeterminate."} Fresh reconciliation failed: {SafeMessage(exception)}",
                _clock.GetUtcNow());
        }
    }

    private async Task<MallPurchaseObservation> ReadAfterCommitAsync(
        MallPurchaseTransportCommit commit,
        CancellationToken cancellationToken)
    {
        if (_verificationHook is not null)
        {
            var result = await _verificationHook.ReadAfterCommitAsync(commit, cancellationToken).ConfigureAwait(false);
            result.Validate();
            EnsureRequestReadMatches(commit.Preview.Request, result);
            return result;
        }
        if (_verify is not null)
        {
            var result = await _verify(commit, cancellationToken).ConfigureAwait(false);
            result.Validate();
            EnsureRequestReadMatches(commit.Preview.Request, result);
            return result;
        }
        return await RefreshAsync(commit.Preview.Request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MallPurchaseObservation> ReadHttpAsync(
        MallPurchaseRequest request,
        CancellationToken cancellationToken)
    {
        var listingUri = BuildListingUri(request);
        var listingBody = await GetOnceAsync(listingUri, cancellationToken).ConfigureAwait(false);
        if (LooksLikeLogin(listingBody))
            throw new MallPurchaseHttpException(
                MallPurchaseHttpResponseClassification.LoginRequired,
                "KoL requires a fresh login before the Mall can be read.", listingUri);

        var offer = ParseOffer(listingBody, request, _clock.GetUtcNow());
        var statusUri = BuildStatusUri("status");
        var statusBody = await GetOnceAsync(statusUri, cancellationToken).ConfigureAwait(false);
        var inventoryUri = BuildStatusUri("inventory");
        var inventoryBody = await GetOnceAsync(inventoryUri, cancellationToken).ConfigureAwait(false);
        var account = ParseAccount(request.AccountId, statusBody, inventoryBody, request.ExpectedDailyLimit, _clock.GetUtcNow());
        var observation = new MallPurchaseObservation(offer, account, _clock.GetUtcNow());
        observation.Validate();
        EnsureRequestReadMatches(request, observation);
        return observation;
    }

    private async Task<(MallPurchaseHttpResponse Response, string Body)> PostOnceAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(values);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        using var response = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var classification = ClassifyResponse(response, body, mutation: true, uri);
        return (Remember(classification), body);
    }

    private async Task<string> GetOnceAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var classification = ClassifyResponse(response, body, mutation: false, uri);
        Remember(classification);
        if (classification.Classification != MallPurchaseHttpResponseClassification.Accepted)
            throw new MallPurchaseHttpException(
                classification.Classification,
                classification.Message ?? "KoL returned an unusable response.", uri, response.StatusCode);
        return body;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAllowedUri(request.RequestUri!);
            if (_lastRequest is { } last)
            {
                var wait = _options.MinimumRequestInterval - (_clock.GetUtcNow() - last);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            // Apply a bounded per-request timeout without introducing a
            // retrying policy; in particular POST is sent exactly once.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            _lastRequest = _clock.GetUtcNow();
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private MallPurchaseHttpResponse ClassifyResponse(
        HttpResponseMessage response,
        string body,
        bool mutation,
        Uri expectedUri)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (uri is not null && (!IsAllowedHost(uri.Host) || !UrisMatchEndpoint(expectedUri, uri)))
            return new(MallPurchaseHttpResponseClassification.Redirected, response.StatusCode, uri,
                "KoL response was redirected away from the exact allowed Mall endpoint.");
        if ((int)response.StatusCode is >= 300 and < 400)
            return new(MallPurchaseHttpResponseClassification.Redirected, response.StatusCode, uri,
                "KoL returned a redirect; mutation was not accepted.");
        if (response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden
            || LooksLikeLogin(body))
            return new(MallPurchaseHttpResponseClassification.LoginRequired, response.StatusCode, uri,
                "KoL requires a fresh login.");
        if (response.StatusCode == (HttpStatusCode)429)
            return new(MallPurchaseHttpResponseClassification.RateLimited, response.StatusCode, uri,
                "KoL rate limited the Mall request.");
        if ((int)response.StatusCode >= 500)
            return new(MallPurchaseHttpResponseClassification.ServerError,
                response.StatusCode, uri,
                mutation ? "KoL returned a server error after the Mall POST; outcome is indeterminate." : "KoL returned a server error.");
        if (!response.IsSuccessStatusCode)
            return new(MallPurchaseHttpResponseClassification.Rejected, response.StatusCode, uri,
                $"KoL rejected the Mall request (HTTP {(int)response.StatusCode}).");
        if (!mutation)
            return new(MallPurchaseHttpResponseClassification.Accepted, response.StatusCode, uri);

        if (ContainsAny(body,
                "you bought", "you purchase", "purchase successful", "successfully purchased",
                "item added to your inventory", "you acquire", "you got"))
            return new(MallPurchaseHttpResponseClassification.Accepted, response.StatusCode, uri,
                "KoL acknowledged the Mall purchase.");
        if (ContainsAny(body,
                "not enough meat", "insufficient meat", "sold out", "no longer available",
                "cannot purchase", "can't purchase", "could not purchase", "purchase limit",
                "not enough of that item"))
            return new(MallPurchaseHttpResponseClassification.Rejected, response.StatusCode, uri,
                "KoL rejected the exact Mall purchase.");
        return new(MallPurchaseHttpResponseClassification.Indeterminate, response.StatusCode, uri,
            "KoL returned no unambiguous Mall purchase acknowledgement.");
    }

    private Uri BuildListingUri(MallPurchaseRequest request)
    {
        var path = new Uri(_options.BaseUri, _options.ListingPath);
        var builder = new UriBuilder(path)
        {
            Query = $"buying=1&whichitem={Uri.EscapeDataString(request.ItemId)}&whichstore={Uri.EscapeDataString(request.StoreId)}&quantity={request.Quantity.ToString(CultureInfo.InvariantCulture)}&sellerid={Uri.EscapeDataString(request.SellerId)}"
        };
        EnsureAllowedUri(builder.Uri);
        return builder.Uri;
    }

    private Uri BuildPurchaseUri(MallPurchaseRequest request)
    {
        var path = new Uri(_options.BaseUri, _options.PurchasePath);
        var builder = new UriBuilder(path)
        {
            Query = $"buying=1&whichitem={Uri.EscapeDataString(request.ItemId)}&whichstore={Uri.EscapeDataString(request.StoreId)}&quantity={request.Quantity.ToString(CultureInfo.InvariantCulture)}&sellerid={Uri.EscapeDataString(request.SellerId)}"
        };
        EnsureAllowedUri(builder.Uri);
        return builder.Uri;
    }

    private Uri BuildStatusUri(string what)
    {
        var path = new Uri(_options.BaseUri, _options.StatusPath);
        var builder = new UriBuilder(path)
        {
            Query = $"what={Uri.EscapeDataString(what)}&for=KoLibri"
        };
        EnsureAllowedUri(builder.Uri);
        return builder.Uri;
    }

    private static IReadOnlyDictionary<string, string> BuildPurchaseForm(MallPurchaseRequest request)
    {
        var price = request.ExpectedUnitPrice.ToString(CultureInfo.InvariantCulture);
        var quantity = request.Quantity.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["buying"] = "1",
            ["whichitem"] = request.ItemId,
            ["whichstore"] = request.StoreId,
            ["quantity"] = quantity,
            // These binding fields are ignored by older KoL pages but make
            // the expected seller and observed price explicit for mirrors and
            // test servers.  ApprovalToken is intentionally never included.
            ["sellerid"] = request.SellerId,
            ["price"] = price
        };
    }

    private MallPurchaseOffer ParseOffer(
        string html,
        MallPurchaseRequest request,
        DateTimeOffset observedAt)
    {
        var storeId = request.StoreId;
        var itemId = request.ItemId;
        foreach (Match match in StockRow.Matches(html))
        {
            if (string.Equals(match.Groups["store"].Value, request.StoreId, StringComparison.Ordinal)
                && string.Equals(match.Groups["item"].Value, request.ItemId, StringComparison.Ordinal))
                break;
        }

        var values = ParseNamedInputs(html);
        storeId = FirstNumeric(values, string.Empty, "whichstore", "storeid", "store", "store_id");
        itemId = FirstNumeric(values, string.Empty, "whichitem", "itemid", "item", "item_id");
        if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(itemId))
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.InvalidResponse,
                "KoL Mall page did not expose the exact requested item and store ids.", null);
        var sellerId = FirstNumeric(values, request.SellerId, "sellerid", "seller", "whichplayer", "playerid", "who");

        var price = FirstLong(values, -1, "price", "unitprice", "unit_price", "cost", "meat");
        if (price < 0)
        {
            var priceMatch = NumberWithMeat.Match(WebUtility.HtmlDecode(html));
            if (priceMatch.Success) price = ParseLong(priceMatch.Groups["value"].Value, -1);
        }
        if (price < 0)
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.InvalidResponse,
                "KoL Mall page did not expose a numeric unit price.", null);

        var quantity = FirstInt(values, -1, "quantity", "qty", "stock", "available");
        var quantityFound = quantity >= 0;
        if (quantity < 0)
        {
            var quantityMatch = QuantityText.Match(WebUtility.HtmlDecode(html));
            quantityFound = quantityMatch.Success;
            quantity = quantityMatch.Success ? ParseInt(quantityMatch.Groups["value"].Value, 0) : 0;
        }
        var soldOut = html.Contains("sold out", StringComparison.OrdinalIgnoreCase);
        if (quantity == 0 && soldOut)
            quantity = 0;
        if (!quantityFound && !soldOut)
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.InvalidResponse,
                "KoL Mall page did not expose a numeric available quantity.", null);
        if (quantity < 0) quantity = 0;

        var limit = FirstNullableInt(values, "limit", "purchaselimit", "purchase_limit", "sellerlimit");
        if (_options.RequireSellerIdOnRead && string.IsNullOrWhiteSpace(sellerId))
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.InvalidResponse,
                "KoL Mall page did not expose a numeric seller id.", null);
        if (!IsNumeric(sellerId))
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.InvalidResponse,
                "KoL Mall page exposed a non-numeric seller id.", null);

        // The route identifies the exact store/item even when the page does
        // not echo hidden fields.  Never substitute a different requested id.
        if (!string.Equals(storeId, request.StoreId, StringComparison.Ordinal)
            || !string.Equals(itemId, request.ItemId, StringComparison.Ordinal))
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected,
                "KoL Mall page did not match the exact requested item and store.", null);

        return new MallPurchaseOffer(sellerId, storeId, itemId, price, quantity, observedAt, limit);
    }

    private static MallPurchaseAccountState ParseAccount(
        string accountId,
        string statusBody,
        string inventoryBody,
        long? expectedDailyLimit,
        DateTimeOffset observedAt)
    {
        long meat = 0;
        long dailySpent = 0;
        long dailyLimit = expectedDailyLimit ?? 0;
        var sessionValid = true;
        try
        {
            using var document = JsonDocument.Parse(statusBody);
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Object)
                root = nested;
            meat = ReadLong(root, "meat", "currency");
            dailySpent = ReadLong(root, "dailySpent", "daily_spent", "meatSpent", "meat_spent", "todaySpent");
            dailyLimit = ReadLong(root, "dailyLimit", "daily_limit", "meatLimit", "meat_limit", "dailyMallLimit", "dailyMallSpendLimit", "limit");
            if (dailyLimit == 0 && expectedDailyLimit is { } fallback) dailyLimit = fallback;
            sessionValid = !ReadBool(root, "loggedOut", "loginRequired", "invalidSession");
        }
        catch (JsonException)
        {
            if (LooksLikeLogin(statusBody)) sessionValid = false;
            else throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.InvalidResponse,
                "KoL status response was not valid JSON.", null);
        }

        var inventory = ParseInventory(inventoryBody);
        return new MallPurchaseAccountState(accountId, Math.Max(0, meat), Math.Max(0, dailySpent), Math.Max(0, dailyLimit), inventory, observedAt, sessionValid);
    }

    private static IReadOnlyDictionary<string, int> ParseInventory(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("inventory", out var nested)) root = nested;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    var id = ReadString(property.Value, "itemid", "itemId", "id") ?? property.Name;
                    var count = ReadInt(property.Value, "quantity", "count", "amount");
                    if (IsNumeric(id)) result[id] = Math.Max(0, count);
                }
                else if (property.Value.ValueKind == JsonValueKind.Number
                    && IsNumeric(property.Name))
                    result[property.Name] = Math.Max(0, ReadInt(property.Value));
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(element, "itemid", "itemId", "id");
                if (!IsNumeric(id)) continue;
                result[id!] = Math.Max(0, ReadInt(element, "quantity", "count", "amount"));
            }
        }
        return result;
    }

    private static Dictionary<string, string> ParseNamedInputs(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in InputTag.Matches(html))
        {
            var name = NameAttribute.Match(tag.Value);
            var value = ValueAttribute.Match(tag.Value);
            if (name.Success && value.Success)
                result[name.Groups["name"].Value] = WebUtility.HtmlDecode(value.Groups["value"].Value);
        }
        foreach (Match tag in DataAttribute.Matches(html))
            result[tag.Groups["name"].Value] = WebUtility.HtmlDecode(tag.Groups["value"].Value);
        foreach (Match stock in StockRow.Matches(html))
        {
            result.TryAdd("storeid", stock.Groups["store"].Value);
            result.TryAdd("itemid", stock.Groups["item"].Value);
        }
        return result;
    }

    private static string FirstNumeric(IReadOnlyDictionary<string, string> values, string fallback, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var value) && IsNumeric(value)) return value;
        return fallback;
    }

    private static long FirstLong(IReadOnlyDictionary<string, string> values, long fallback, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var value) && TryParseLong(value, out var parsed)) return parsed;
        return fallback;
    }

    private static int FirstInt(IReadOnlyDictionary<string, string> values, int fallback, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    private static int? FirstNullableInt(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        var parsed = FirstInt(values, -1, keys);
        return parsed < 0 ? null : parsed;
    }

    private static bool PurchaseDeltaMatches(MallPurchaseRequest request, MallPurchaseObservation before, MallPurchaseObservation after)
    {
        if (!ExactOfferMatches(request, after.Offer)) return false;
        var expectedMeat = before.Account.Meat - request.TotalCost;
        var expectedSpent = before.Account.DailySpent + request.TotalCost;
        var beforeQty = before.Account.InventoryQuantity(request.ItemId);
        var afterQty = after.Account.InventoryQuantity(request.ItemId);
        return after.Account.Meat == expectedMeat
            && after.Account.DailySpent == expectedSpent
            && afterQty - beforeQty == request.Quantity
            && after.Offer.Quantity == before.Offer.Quantity - request.Quantity;
    }

    private static void EnsureExactBefore(MallPurchaseRequest request, MallPurchaseObservation before)
    {
        EnsureRequestReadMatches(request, before);
        if (before.Offer.Quantity < request.Quantity)
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected, "Mall offer is sold out.", null);
        if (before.Offer.PurchaseLimit is { } limit && limit < request.Quantity)
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected, "Mall seller purchase limit is lower than requested quantity.", null);
        if (before.Account.Meat < request.TotalCost)
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected, "Account does not have enough Meat.", null);
        if (before.Account.DailySpent + request.TotalCost > before.Account.DailyLimit)
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected, "Mall daily Meat limit would be exceeded.", null);
    }

    private static void EnsureRequestReadMatches(MallPurchaseRequest request, MallPurchaseObservation observation)
    {
        if (!ExactOfferMatches(request, observation.Offer))
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected,
                "Fresh Mall read did not match the exact seller, store, item or unit price.", null);
        if (!string.Equals(request.AccountId, observation.Account.AccountId, StringComparison.OrdinalIgnoreCase))
            throw new MallPurchaseHttpException(MallPurchaseHttpResponseClassification.Rejected,
                "Fresh account read did not match the requested account.", null);
    }

    private static bool ExactOfferMatches(MallPurchaseRequest request, MallPurchaseOffer offer)
        => string.Equals(request.SellerId, offer.SellerId, StringComparison.Ordinal)
           && string.Equals(request.StoreId, offer.StoreId, StringComparison.Ordinal)
           && string.Equals(request.ItemId, offer.ItemId, StringComparison.Ordinal)
           && request.ExpectedUnitPrice == offer.UnitPrice;

    private static void ValidateExactNumerics(MallPurchaseRequest request)
    {
        if (!IsNumeric(request.SellerId))
            throw new ArgumentException("SellerId must be a numeric KoL id.", nameof(request));
        if (!IsNumeric(request.StoreId))
            throw new ArgumentException("StoreId must be a numeric KoL id.", nameof(request));
        if (!IsNumeric(request.ItemId))
            throw new ArgumentException("ItemId must be a numeric KoL id.", nameof(request));
        if (request.ExpectedUnitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedUnitPrice));
        if (request.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Quantity));
    }

    private void EnsureAllowedHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Mall requests require an absolute HTTPS URI.", nameof(uri));
        if (!IsAllowedHost(uri.Host))
            throw new ArgumentException($"Mall host '{uri.Host}' is not in the KoL host allowlist.", nameof(uri));
    }

    private void EnsureAllowedUri(Uri uri)
    {
        EnsureAllowedHost(uri);
        if (!string.Equals(uri.Scheme, _options.BaseUri.Scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mall URI scheme changed unexpectedly.");
    }

    private bool IsAllowedHost(string host) => _allowedHosts.Contains(host.TrimEnd('.').ToLowerInvariant());

    private static bool UrisMatchEndpoint(Uri expected, Uri actual)
        => string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(expected.Host.TrimEnd('.'), actual.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
           && expected.Port == actual.Port
           && string.Equals(expected.AbsolutePath, actual.AbsolutePath, StringComparison.Ordinal)
           && string.Equals(expected.Query, actual.Query, StringComparison.Ordinal);

    private static HashSet<string> NormalizeHosts(IEnumerable<string> hosts)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            var normalized = host?.Trim().TrimEnd('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalized)) result.Add(normalized);
        }
        return result;
    }

    private MallPurchaseHttpResponse Remember(MallPurchaseHttpResponse response)
    {
        LastResponse = response;
        return response;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    private static bool LooksLikeLogin(string body) => LoginMarker.IsMatch(body);

    private static bool ContainsAny(string value, params string[] markers)
        => markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsNumeric(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.All(char.IsDigit)
           && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static long ParseLong(string value, long fallback)
        => TryParseLong(value, out var parsed) ? parsed : fallback;

    private static bool TryParseLong(string value, out long parsed)
        => long.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static long ReadLong(JsonElement element, params string[] names)
        => TryParseLong(ReadString(element, names) ?? string.Empty, out var value) ? value : 0;

    private static int ReadInt(JsonElement element, params string[] names)
        => int.TryParse(ReadString(element, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int ReadInt(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : 0;

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
        }
        return false;
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return message.Length <= 240 ? message : message[..240];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requestGate.Dispose();
    }
}

/// <summary>Structured transport failure without exposing an HTTP body.</summary>
public sealed class MallPurchaseHttpException : Exception
{
    public MallPurchaseHttpResponseClassification Classification { get; }
    public HttpStatusCode? StatusCode { get; }
    public Uri? RequestUri { get; }

    public MallPurchaseHttpException(
        MallPurchaseHttpResponseClassification classification,
        string message,
        Uri? requestUri,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Classification = classification;
        RequestUri = requestUri;
        StatusCode = statusCode;
    }
}

/// <summary>Compatibility alias for hosts that name adapters after KoL.</summary>
public sealed class KoLMallPurchaseTransport : IMallPurchaseTransport, IDisposable
{
    private readonly HttpMallPurchaseTransport _inner;

    public KoLMallPurchaseTransport(HttpClient httpClient, HttpMallPurchaseTransportOptions? options = null)
        => _inner = new HttpMallPurchaseTransport(httpClient, options);

    public MallPurchaseHttpResponse? LastResponse => _inner.LastResponse;
    public Task<MallPurchaseObservation> RefreshAsync(MallPurchaseRequest request, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(request, cancellationToken);
    public Task<MallPurchaseTransportReceipt> CommitAsync(MallPurchaseTransportCommit request, CancellationToken cancellationToken = default)
        => _inner.CommitAsync(request, cancellationToken);
    public void Dispose() => _inner.Dispose();
}
