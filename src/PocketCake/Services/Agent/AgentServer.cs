using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ModelContextProtocol.Protocol;
using PocketCake.ViewModels;

namespace PocketCake.Services.Agent;

/// <summary>
/// Hosts the MCP endpoint AI agents use to drive the editor: Streamable HTTP on
/// <c>http://127.0.0.1:{port}/mcp</c>. Tools run against the live <see cref="AppState"/>, so every
/// agent edit shows up in the UI. Loopback-only, and requests from a non-local Host / Origin are
/// refused (DNS-rebinding guard).
/// </summary>
public sealed class AgentServer(AppState state, DispatcherQueue dispatcher, int port)
{
    private WebApplication? _app;

    public int Port { get; } = port;

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
            if (!IsLocal(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
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

    private static bool IsLocal(HttpRequest request)
    {
        static bool IsLoopbackHost(string host) => host is "127.0.0.1" or "localhost" or "[::1]";

        if (!IsLoopbackHost(request.Host.Host))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();

        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host));
    }

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
