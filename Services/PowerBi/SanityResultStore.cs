using System.Text.Json;
using Reports_Sanity_Check.Services.PowerBi.Models;

namespace Reports_Sanity_Check.Services.PowerBi;

/// <summary>
/// Persists sanity-check runs so results survive past the page session. This is the seam the
/// (future) notification/email module will read from — it is intentionally storage-only for now.
/// </summary>
public interface ISanityResultStore
{
    /// <summary>Saves a completed run and returns the location it was written to.</summary>
    Task<string> SaveAsync(SanityCheckRun run, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent runs (newest first), up to <paramref name="take"/>.</summary>
    Task<IReadOnlyList<SanityCheckRun>> GetRecentRunsAsync(int take = 10, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores each run as a timestamped JSON file under <c>{ContentRoot}/App_Data/SanityResults</c>.
/// Simple and dependency-free; can be swapped for a database implementation later without
/// touching callers.
/// </summary>
public sealed class JsonFileSanityResultStore : ISanityResultStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly ILogger<JsonFileSanityResultStore> _logger;

    public JsonFileSanityResultStore(IHostEnvironment environment, ILogger<JsonFileSanityResultStore> logger)
    {
        _directory = Path.Combine(environment.ContentRootPath, "App_Data", "SanityResults");
        _logger = logger;
    }

    public async Task<string> SaveAsync(SanityCheckRun run, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);

        var fileName = $"sanity-{run.StartedAtUtc:yyyyMMdd-HHmmss}-{run.RunId}.json";
        var path = Path.Combine(_directory, fileName);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, run, SerializerOptions, cancellationToken);

        _logger.LogInformation(
            "Saved sanity run {RunId} ({Passed}/{Total} passed) to {Path}.",
            run.RunId, run.PassedCount, run.TotalReports, path);

        return path;
    }

    public async Task<IReadOnlyList<SanityCheckRun>> GetRecentRunsAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<SanityCheckRun>();
        }

        var files = new DirectoryInfo(_directory)
            .GetFiles("sanity-*.json")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Take(take);

        var runs = new List<SanityCheckRun>();
        foreach (var file in files)
        {
            try
            {
                await using var stream = file.OpenRead();
                var run = await JsonSerializer.DeserializeAsync<SanityCheckRun>(stream, SerializerOptions, cancellationToken);
                if (run is not null)
                {
                    runs.Add(run);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read sanity result file {File}.", file.FullName);
            }
        }

        return runs;
    }
}
