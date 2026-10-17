using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ModelContextProtocol.Protocol;
using PocketCake.Core.Agent;
using PocketCake.ViewModels;

namespace PocketCake.Services.Agent;

/// <summary>
/// Hosts the MCP endpoint AI agents use to drive the editor: Streamable HTTP on
/// <c>http://127.0.0.1:{port}/mcp</c>. Tools run against the live <see cref="AppState"/>, so every
/// agent edit shows up in the UI. Loopback-only, requests from a non-local Host / Origin are
/// refused (DNS-rebinding guard), and every request must carry the bearer token from settings.
/// </summary>
public sealed class AgentServer(AppState state, DispatcherQueue dispatcher, AppSettings settings)
{
    private const string TokenRequired = "token required — copy the setup command from PocketCake Settings";

    private WebApplication? _app;

    public int Port { get; } = settings.AgentPort;

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    public bool IsRunning => _app is not null;

    /// <summary>The agent guide (the bundled SKILL.md without its front matter), sent as the server instructions.</summary>
    public static string Guide { get; } = LoadGuide();

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.ListenLocalhost(Port));
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "pocketcake", Version = AppVersion };
                o.ServerInstructions = Guide;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(new AgentTools(state, dispatcher))
            .WithPrompts<AgentPrompts>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var origin = request.Headers.Origin.ToString();

            if (!AgentAuth.IsLocal(request.Host.Host, origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Read per request, so a token reset takes effect without a restart.
            if (!AgentAuth.IsAuthorized(request.Host.Host, origin, request.Headers.Authorization.ToString(), settings.AgentToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync(TokenRequired);
                return;
            }

            await next(context);
        });
        app.MapMcp("/mcp");

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static string LoadGuide()
    {
        using var stream = typeof(AgentServer).Assembly.GetManifestResourceStream("PocketCake.SKILL.md");

        if (stream is null)
        {
            return "";
        }

        var text = new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n");

        // Drop the YAML front matter; it only matters to skill loaders.
        if (text.StartsWith("---\n") && text.IndexOf("\n---\n", 4, StringComparison.Ordinal) is var end and > 0)
        {
            text = text[(end + 5)..];
        }

        return text.Trim();
    }
}
