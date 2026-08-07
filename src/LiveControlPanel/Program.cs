using System.Runtime.Versioning;
using LiveControlPanel.Api;
using LiveControlPanel.Config;
using LiveControlPanel.Core;
using LiveControlPanel.Net;
using LiveControlPanel.Notify;
using LiveControlPanel.Obs;
using LiveControlPanel.Slides;
using LiveControlPanel.Youtube;
using Microsoft.Extensions.FileProviders;
using Serilog;

[assembly: SupportedOSPlatform("windows")]

namespace LiveControlPanel;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var paths = new AppPaths(Environment.GetEnvironmentVariable("LCP_DATA_DIR"));
        paths.EnsureCreated();

        // Serilog first: the service has no console, so anything that goes wrong during startup has
        // to land in a file or it is lost (FR 2).
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            // HttpClient logs full request URLs at Information, and the Telegram Bot API carries
            // its token in the URL — that must not sit in a plaintext log for 31 days.
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "panel-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            await RunAsync(args, paths);
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode
            is System.Net.Sockets.SocketError.AccessDenied
            or System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            // Two real causes on a church PC: another program already holds the port, or Windows has
            // reserved the range (Hyper-V/WSL grab blocks around 5000 — check
            // `netsh interface ipv4 show excludedportrange protocol=tcp`). Both are fixed by editing
            // the port in settings.json, so say that instead of printing a bind stack trace.
            Log.Fatal(ex,
                "Could not listen on port {Port}. Another program may be using it, or Windows has " +
                "reserved that port range. Change \"port\" in {SettingsFile} and restart the service.",
                new ConfigStore(paths).Settings.Port, paths.SettingsFile);
            throw;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Live Control Panel terminated unexpectedly");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task RunAsync(string[] args, AppPaths paths)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog();
        // FR 2: hosted as a Windows service so the panel is already listening before anyone logs in,
        // which is what lets OBS's browser dock load on the first try.
        builder.Host.UseWindowsService(options => options.ServiceName = "LiveControlPanel");

        var config = new ConfigStore(paths);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<AccessGate>();
        builder.Services.AddSingleton<StateHub>();
        builder.Services.AddSingleton<AccessInfoProvider>();
        builder.Services.AddSingleton<ISlideController, SlideController>();
        builder.Services.AddSingleton<StateManager>();
        builder.Services.AddSingleton<YouTubeAuth>();
        builder.Services.AddSingleton<IYouTubeClient, YouTubeClient>();
        builder.Services.AddSingleton<ObsClient>();
        builder.Services.AddSingleton<IObsClient>(sp => sp.GetRequiredService<ObsClient>());
        builder.Services.AddSingleton<Preflight>();
        builder.Services.AddSingleton<Orchestrator>();
        builder.Services.AddSingleton<NotificationService>();
        builder.Services.AddHttpClient<ITelegramClient, TelegramClient>(http =>
            http.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddHostedService<PanelBackgroundService>();

        // FR 2.2: plain HTTP only. The page opens a ws:// socket to this same origin, and an HTTPS
        // page would have that blocked as mixed content.
        builder.WebHost.UseUrls($"http://0.0.0.0:{config.Settings.Port}");

        var app = builder.Build();

        app.UseSerilogRequestLogging(options => options.GetLevel = (_, _, ex) =>
            ex is null ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Error);

        app.UseWebSockets();
        UseStaticAssets(app);
        app.UseAccessGate();

        app.MapPanelEndpoints();
        MapWebSocket(app);

        LogStartupBanner(app, config);
        await app.RunAsync();
    }

    /// <summary>
    /// wwwroot is embedded in the assembly so the published single-file exe needs no companion
    /// folder; a physical wwwroot next to the exe still wins, which keeps front-end edits instant
    /// during development.
    /// </summary>
    private static void UseStaticAssets(WebApplication app)
    {
        var providers = new List<IFileProvider>();

        var physical = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(physical)) providers.Add(new PhysicalFileProvider(physical));

        providers.Add(new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"));

        var fileProvider = new CompositeFileProvider(providers);

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            // Seven operators share one PC and iPads cache aggressively; a stale panel after an
            // update would be diagnosed at 04:40 by someone with no way to fix it.
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
        });
    }

    private static void MapWebSocket(WebApplication app)
    {
        app.Map("/ws", async (HttpContext context, StateHub hub, StateManager state) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunClientAsync(socket, state.Snapshot(), context.RequestAborted);
        });
    }

    private static void LogStartupBanner(WebApplication app, ConfigStore config)
    {
        var log = app.Services.GetRequiredService<ILogger<PanelBackgroundService>>();
        var access = app.Services.GetRequiredService<AccessInfoProvider>().Get();

        log.LogInformation("Live Control Panel listening on port {Port}", config.Settings.Port);
        log.LogInformation("Data directory: {Root}", config.Paths.Root);
        log.LogInformation("Local URL: {Url}", access.LocalUrl);

        foreach (var address in access.Addresses)
            log.LogInformation("LAN URL: {Url} ({Adapter})", address.Url, address.AdapterName);

        // The access code is random per installation, so it has to be discoverable somewhere; the log
        // and settings.json are the two places. Printed on every start, not only the first, because
        // whoever needs it is usually looking at a machine somebody else set up.
        log.LogInformation("Access code: {Code}   Settings PIN: {Pin}   (both in {File})",
            config.Settings.AccessCode, config.Settings.SettingsPin, config.Paths.SettingsFile);

        if (config.Settings.SettingsPin == Config.Seed.DefaultSettingsPin)
        {
            log.LogInformation(
                "Settings PIN is still the default {Pin}. Change it on the settings page if you want.",
                Config.Seed.DefaultSettingsPin);
        }

        WarnIfSessionIsolated(log);
    }

    /// <summary>
    /// Slide control reaches the presentation program through window handles and the COM
    /// running-object table, and both are per-session. Running in session 0 — which is what hosting
    /// this as a Windows service means — leaves paging permanently unable to find the WPS window,
    /// with no error anywhere. That is the one failure an operator alone at 04:40 cannot diagnose, so
    /// it gets stated loudly at startup instead.
    /// </summary>
    private static void WarnIfSessionIsolated(Microsoft.Extensions.Logging.ILogger log)
    {
        int sessionId;
        try { sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId; }
        catch (Exception) { return; }

        if (sessionId != 0)
        {
            log.LogInformation("Running in session {SessionId}; slide control can reach the desktop.", sessionId);
            return;
        }

        log.LogWarning(
            "Running in session 0 (the Windows service session). Slide paging, the page counter and " +
            "the next-slide preview CANNOT work from here: window handles and the COM running-object " +
            "table are per-session, so the WPS slide-show window is unreachable. Start this program " +
            "at user logon instead (see the README). OBS, YouTube and Telegram are unaffected.");
    }
}

/// <summary>
/// Starts the OBS connection loop and keeps derived state fresh. Slide availability and the
/// authorization countdown are polled here because nothing else would notice them changing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PanelBackgroundService : BackgroundService
{
    private static readonly TimeSpan SlidePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthPollInterval = TimeSpan.FromMinutes(30);

    private readonly ObsClient _obs;
    private readonly StateManager _state;
    private readonly IYouTubeClient _youtube;
    private readonly ILogger<PanelBackgroundService> _log;

    public PanelBackgroundService(
        ObsClient obs, StateManager state, IYouTubeClient youtube, ILogger<PanelBackgroundService> log)
    {
        _obs = obs;
        _state = state;
        _youtube = youtube;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _obs.StatusChanged += _state.ApplyObsStatus;
        _obs.Start();

        var lastAuthCheck = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            _state.RefreshSlides();

            if (DateTime.UtcNow - lastAuthCheck > AuthPollInterval)
            {
                lastAuthCheck = DateTime.UtcNow;
                await RefreshAuthAsync(stoppingToken);
            }

            try { await Task.Delay(SlidePollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshAuthAsync(CancellationToken ct)
    {
        try
        {
            var info = await _youtube.GetAuthInfoAsync(ct);
            _state.Mutate(s => s.Auth = new AuthState
            {
                Valid = info.Valid,
                ExpiresInDays = info.ExpiresInDays,
                AuthorizedAt = info.AuthorizedAt,
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Background authorization refresh failed");
        }
    }
}
