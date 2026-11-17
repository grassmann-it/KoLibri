using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KolLibri.Mcp;

/// <summary>Shared JSON settings for the MCP transport and tool payloads.</summary>
public static class McpJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement EmptyObject { get; } = ParseElement("{}");

    public static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement CloneOrEmpty(JsonElement value)
        => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? EmptyObject
            : value.Clone();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>One inbound JSON-RPC message and the framing mode used by its peer.</summary>
internal sealed record McpInboundMessage(JsonDocument Document, bool ContentLengthFraming);

/// <summary>
/// Small MCP stdio framing implementation. MCP uses a UTF-8 JSON-RPC document
/// preceded by a Content-Length header. Newline-delimited JSON is accepted as a
/// convenience for smoke tests and simple shell hosts.
/// </summary>
internal static class McpStdioFraming
{
    public static async Task<McpInboundMessage?> ReadAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var firstLine = await ReadLineAsync(input, cancellationToken).ConfigureAwait(false);
        if (firstLine is null) return null;
        if (firstLine.Length == 0)
        {
            // Be liberal about an extra blank line between requests.
            do
            {
                firstLine = await ReadLineAsync(input, cancellationToken).ConfigureAwait(false);
                if (firstLine is null) return null;
            }
            while (firstLine.Length == 0);
        }

        var firstText = Encoding.UTF8.GetString(firstLine).Trim();
        if (firstText.StartsWith("{", StringComparison.Ordinal)
            || firstText.StartsWith("[", StringComparison.Ordinal))
        {
            return Parse(firstText, contentLengthFraming: false);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeader(headers, firstText);
        while (true)
        {
            var line = await ReadLineAsync(input, cancellationToken).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Length == 0) break;
            AddHeader(headers, Encoding.UTF8.GetString(line).Trim());
        }

        if (!headers.TryGetValue("Content-Length", out var lengthText)
            || !int.TryParse(lengthText, out var length)
            || length < 0)
        {
            throw new McpProtocolException("Missing or invalid Content-Length header.");
        }

        var payload = await ReadExactlyAsync(input, length, cancellationToken).ConfigureAwait(false);
        return Parse(Encoding.UTF8.GetString(payload), contentLengthFraming: true);
    }

    public static async Task WriteAsync(
        Stream output,
        JsonElement payload,
        bool contentLengthFraming,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writeGate);

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, McpJson.Options);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (contentLengthFraming)
            {
                var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            }
            await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            if (!contentLengthFraming)
                await output.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static McpInboundMessage Parse(string json, bool contentLengthFraming)
    {
        try
        {
            return new McpInboundMessage(JsonDocument.Parse(json), contentLengthFraming);
        }
        catch (JsonException exception)
        {
            throw new McpProtocolException("Invalid JSON-RPC payload.", exception);
        }
    }

    private static void AddHeader(IDictionary<string, string> headers, string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0) return;
        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }

    private static async Task<byte[]?> ReadLineAsync(Stream input, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await input.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return line.Count == 0 ? null : line.ToArray();
            if (one[0] == (byte)'\n')
                break;
            if (one[0] != (byte)'\r')
                line.Add(one[0]);
        }

        return line.ToArray();
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream input,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new McpProtocolException("Unexpected end of JSON-RPC payload.");
            offset += read;
        }
        return buffer;
    }
}

internal sealed class McpProtocolException : Exception
{
    public McpProtocolException(string message) : base(message) { }
    public McpProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
