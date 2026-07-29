using Reports_Sanity_Check.Services.Api;
using Reports_Sanity_Check.Services.Notifications;
using Reports_Sanity_Check.Services.PowerBi;

var builder = WebApplication.CreateBuilder(args);

// Power BI report sanity-check feature: bind the "PowerBi" section. The service-principal
// credentials (TenantId/ClientId/ClientSecret) live here; the secret is expected to be supplied at
// runtime via PowerBi__ClientSecret (env var / Key Vault) rather than appsettings.json.
builder.Services.AddOptions<PowerBiOptions>()
    .Bind(builder.Configuration.GetSection(PowerBiOptions.SectionName));

builder.Services.AddScoped<IPowerBiEmbedService, PowerBiEmbedService>();
builder.Services.AddScoped<IReportSanityCheckService, ReportSanityCheckService>();
builder.Services.AddSingleton<ISanityResultStore, JsonFileSanityResultStore>();
builder.Services.AddHttpClient();

// Host base URL (the "Api" section, HostBaseUrl) tells the headless browser which loopback origin
// serves the shared check page, and register the headless (Playwright/Chromium) checker that runs
// the browser-side visual check server-side so the job can run it without a UI.
builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName));

builder.Services.AddSingleton<IHeadlessReportChecker, HeadlessReportChecker>();

// Email notifications: bind the "SendGrid" section. The API key is a secret and is expected to be
// supplied at runtime via the SendGrid__ApiKey Application Setting (or Key Vault), not from
// appsettings.json. When unconfigured, the service no-ops.
builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName));

builder.Services.AddScoped<IEmailNotificationService, SendGridEmailNotificationService>();

var app = builder.Build();

// Headless browser bootstrap. The Microsoft.Playwright package ships automation code but not the
// Chromium binaries, so hosts that don't use the Playwright container image must download them.
// Point the cache at a persisted folder so it survives restarts, then optionally install at startup.
// Both are opt-in via "Headless:InstallBrowserOnStartup" so the Linux Playwright image and local dev
// (where Chromium is already present) skip the download.
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PlaywrightInstaller");
    PlaywrightInstaller.ConfigureBrowsersPath(app.Configuration, app.Environment, startupLogger);

    if (app.Configuration.GetValue("Headless:InstallBrowserOnStartup", false))
    {
        PlaywrightInstaller.EnsureChromiumInstalled(startupLogger);
    }
}

// Serve the static headless host page (wwwroot/headless-check.html) plus its powerbi client/module
// so the in-process Chromium can load them from the app's own loopback origin during the run.
app.UseStaticFiles();

// Azure Container Apps Job: the app has a single mode. SanityJobRunner briefly starts Kestrel so the
// headless browser can reach headless-check.html on localhost, runs the sanity check to completion,
// then stops the host and returns an exit code (0 = ran, 1 = failures). Target workspace/recipients
// come from env vars (WorkspaceId/Environment/ToEmails) with appsettings.json fallbacks.
return await SanityJobRunner.RunAsync(app);
