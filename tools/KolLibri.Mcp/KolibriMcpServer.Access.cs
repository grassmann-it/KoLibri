namespace KolLibri.Mcp;

public partial class KolibriMcpServer
{
    /// <summary>When set, every tool call is checked first; a refusal reaches the AI as a tool error.</summary>
    public ToolAuthorizer? Authorizer { get; set; }
}
