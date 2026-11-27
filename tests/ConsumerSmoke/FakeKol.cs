using System.Net;
using System.Text;

/// <summary>A tiny offline KoL: answers by "METHOD page" (api.php by "what"); everything else gets an empty page.</summary>
internal sealed class FakeKol : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, string, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = new();

    public FakeKol()
    {
        On("GET api.php?what=status", (_, _) => Html(
            """{"name":"Tester","playerid":"1001","level":7,"meat":10000,"adventures":50,"hp":100,"maxhp":100,"mp":40,"maxmp":50,"full":0,"fullnessmax":15,"drunk":0,"drunkmax":14,"spleen":0,"spleenmax":15,"pwd":"0123456789abcdef","familiar":"0","equipment":{"hat":"3","weapon":"0"},"effects":{}}"""));
        On("GET api.php?what=inventory", (_, _) => Html("""{"2":3,"3":1}"""));
        On("GET login.php", (_, _) => Html("<html><body><form action=\"login.php\"><input name=loginname></form>Welcome</body></html>"));
        On("POST login.php", (_, form) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            response.Headers.Location = new Uri("https://www.kingdomofloathing.com/main.php");
            response.Headers.Add("Set-Cookie", "PHPSESSID=fresh; path=/");
            return response;
        });
        On("GET main.php", (_, _) => Html("<html><body>The Kingdom</body></html>"));
    }

    public FakeKol On(string route, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _routes[route] = respond;
        return this;
    }

    public static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        Requests.Add($"{request.Method.Method} {uri.PathAndQuery}");
        var key = $"{request.Method.Method} {uri.AbsolutePath.TrimStart('/')}";
        if (uri.AbsolutePath.EndsWith("api.php", StringComparison.Ordinal))
            key += "?what=" + System.Web.HttpUtility.ParseQueryString(uri.Query)["what"];
        var response = _routes.TryGetValue(key, out var respond)
            ? respond(request, form)
            : Html("<html><body>nothing here</body></html>");
        response.RequestMessage = request;
        return response;
    }
}
