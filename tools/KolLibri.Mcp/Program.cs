using KolLibri.Mcp;

var runtime = await McpRuntimeBootstrap.CreateConfiguredAsync();
return await KolibriMcpServer.RunStdioAsync(runtime);
