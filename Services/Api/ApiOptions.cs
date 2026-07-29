namespace Reports_Sanity_Check.Services.Api;

/// <summary>
/// Configuration for the sanity-check job's in-process host, bound from the "Api" section of
/// appsettings.json. Tells the headless browser which loopback origin serves the shared check page.
/// </summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Optional absolute base URL the headless browser should navigate to in order to load the
    /// shared check page (e.g. <c>http://localhost:8080/</c>). When blank, the job's own loopback
    /// address is used. Set this when the in-process loopback address needs to be pinned explicitly.
    /// </summary>
    public string? HostBaseUrl { get; set; }
}
