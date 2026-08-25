using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Recall.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddHttpClient<RecallApiClient>(client =>
{
    client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("RECALL_API_URL") ?? "http://127.0.0.1:5278");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IRecallCredentialProvider, EnvironmentRecallCredentialProvider>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<MemoryTools>();
await builder.Build().RunAsync();
