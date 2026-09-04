namespace Reports_Sanity_Check.Services.Notifications;

/// <summary>
/// Strongly-typed configuration for the SendGrid email notification feature, bound from the
/// "SendGrid" section of appsettings.json. The <see cref="ApiKey"/> is a secret and should be
/// supplied via App Service Application Settings (SendGrid__ApiKey) or Key Vault, never committed
/// to appsettings.json.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "SendGrid";

    /// <summary>
    /// SendGrid API key. Leave blank in appsettings.json; provide it at runtime via the App Service
    /// Application Setting <c>SendGrid__ApiKey</c> (or a Key Vault reference). When blank, email
    /// notifications are skipped.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>The verified SendGrid sender address that the summary email is sent from.</summary>
    public string FromEmail { get; set; } = string.Empty;

    /// <summary>Friendly display name for the sender.</summary>
    public string FromName { get; set; } = "Reports Sanity Check";

    /// <summary>One or more recipients that receive the run summary.</summary>
    public List<string> ToEmails { get; set; } = new();

    /// <summary>True when an API key, sender, and at least one recipient are configured.</summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(FromEmail)
        && ToEmails.Any(static address => !string.IsNullOrWhiteSpace(address));
}
