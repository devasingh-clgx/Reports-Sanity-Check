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

    /// <summary>
    /// Operational alert recipients. Because Log Analytics is disabled for the Container Apps Job,
    /// these addresses receive an email whenever the job <em>workflow</em> itself fails (bad input,
    /// auth/discovery error, headless browser failure, or persistence failure) so failures can be
    /// tracked. Individual Power BI reports breaking are NOT sent here — they appear in the normal
    /// run summary. Falls back to <see cref="ToEmails"/> when left empty.
    /// </summary>
    public List<string> AdminEmails { get; set; } = new();

    /// <summary>
    /// When true, an email is only sent when a run has at least one failing report. When false, a
    /// summary is sent after every run regardless of outcome.
    /// </summary>
    public bool SendOnlyOnFailure { get; set; }

    /// <summary>True when an API key, sender, and at least one recipient are configured.</summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(FromEmail)
        && ToEmails.Any(static address => !string.IsNullOrWhiteSpace(address));

    /// <summary>
    /// The operational alert recipients: the configured <see cref="AdminEmails"/>, or <see cref="ToEmails"/>
    /// when no admin addresses are set. Never returns duplicates or blank entries.
    /// </summary>
    public IReadOnlyList<string> GetAdminRecipients()
    {
        var admins = AdminEmails
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(static a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (admins.Count > 0)
        {
            return admins;
        }

        return ToEmails
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(static a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when a job-failure alert can actually be delivered: an API key and sender are set and at
    /// least one admin recipient resolves. Used to log a clear, actionable reason at startup when a
    /// workflow failure would otherwise fail silently (because Log Analytics is disabled for the Job).
    /// </summary>
    public bool CanSendAdminAlerts =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(FromEmail)
        && GetAdminRecipients().Count > 0;

    /// <summary>
    /// A human-readable reason describing why <see cref="CanSendAdminAlerts"/> is false (missing API
    /// key, sender, or recipients), or <c>null</c> when alerts can be delivered.
    /// </summary>
    public string? DescribeAdminAlertGap()
    {
        var gaps = new List<string>();
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            gaps.Add("SendGrid:ApiKey (set the SendGrid__ApiKey env var / Key Vault reference)");
        }

        if (string.IsNullOrWhiteSpace(FromEmail))
        {
            gaps.Add("SendGrid:FromEmail");
        }

        if (GetAdminRecipients().Count == 0)
        {
            gaps.Add("SendGrid:AdminEmails (or SendGrid:ToEmails as a fallback)");
        }

        return gaps.Count == 0 ? null : string.Join("; ", gaps);
    }
}
