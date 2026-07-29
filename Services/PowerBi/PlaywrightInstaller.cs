namespace Reports_Sanity_Check.Services.PowerBi;

/// <summary>
/// Bootstraps the Playwright Chromium browser on hosts that don't already ship it (notably
/// <b>Azure App Service Windows</b>, where the Playwright container image isn't used). The
/// <c>Microsoft.Playwright</c> NuGet package contains the automation code but not the ~150&#160;MB
/// browser binaries, so they have to be downloaded with <c>playwright install chromium</c>.
///
/// Two pieces are handled here:
/// <list type="number">
///   <item><b>Browser cache location</b> — <c>PLAYWRIGHT_BROWSERS_PATH</c> is pointed at a persisted
///   folder (under <c>%HOME%</c> on App Service, i.e. <c>D:\home</c>) so the download survives app
///   restarts and deployments and only happens once.</item>
///   <item><b>Install</b> — optionally runs the Playwright CLI install programmatically at startup.
///   It is idempotent: once the browser exists the call returns quickly without re-downloading.</item>
/// </list>
///
/// Both are opt-in via config so local dev and the Linux Playwright container image (which already
/// has Chromium) skip the download.
/// </summary>
public static class PlaywrightInstaller
{
    /// <summary>
    /// Points <c>PLAYWRIGHT_BROWSERS_PATH</c> at a persisted folder so the browser is downloaded once
    /// and reused. Respects an explicit env var or <c>Headless:BrowsersPath</c> when provided; on
    /// Azure App Service defaults to a folder under <c>%HOME%</c> (persisted, shared across instances).
    /// Must run before the first <c>Playwright.CreateAsync()</c> and before <see cref="EnsureChromiumInstalled"/>.
    /// </summary>
    public static void ConfigureBrowsersPath(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        // An explicitly provided env var always wins (e.g. set as an App Service Application Setting).
        var existing = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            logger.LogInformation("PLAYWRIGHT_BROWSERS_PATH already set to {Path}; using it as-is.", existing);
            return;
        }

        var configured = configuration["Headless:BrowsersPath"];
        string path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            path = configured;
        }
        else
        {
            // On App Service, %HOME% maps to D:\home which is a persisted, instance-shared content
            // store; placing browsers outside wwwroot keeps them across deployments. Locally there is
            // no HOME, so fall back to a folder beside the app content.
            var home = Environment.GetEnvironmentVariable("HOME");
            path = !string.IsNullOrWhiteSpace(home)
                ? Path.Combine(home, "site", "playwright-browsers")
                : Path.Combine(environment.ContentRootPath, ".playwright-browsers");
        }

        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", path);
        logger.LogInformation("Playwright browsers path set to {Path}.", path);
    }

    /// <summary>
    /// Runs <c>playwright install chromium</c> in-process. Idempotent and safe to call on every
    /// startup; only the first call on a fresh <c>PLAYWRIGHT_BROWSERS_PATH</c> actually downloads.
    /// Never throws: a failure is logged so the app (and its UI) still starts.
    /// </summary>
    public static void EnsureChromiumInstalled(ILogger logger)
    {
        try
        {
            logger.LogInformation(
                "Ensuring Playwright Chromium is installed (first run downloads ~150 MB and can take a minute)...");

            // Official programmatic equivalent of the `playwright install chromium` CLI command.
            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

            if (exitCode == 0)
            {
                logger.LogInformation("Playwright Chromium is ready.");
            }
            else
            {
                logger.LogError(
                    "Playwright Chromium install exited with code {ExitCode}. The headless API will fail until this is resolved.",
                    exitCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to install Playwright Chromium. The headless API will be unavailable.");
        }
    }
}
