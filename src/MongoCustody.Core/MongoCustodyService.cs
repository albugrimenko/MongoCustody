using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;

namespace MongoCustody.Core;

/// <summary>
/// Concrete implementation of <see cref="IMongoCustodyService"/>.
///
/// Register via <c>services.AddMongoCustody(config)</c> (see
/// <see cref="Extensions.ServiceCollectionExtensions.AddMongoCustody"/>).
///
/// Phase implementation status:
/// <list type="bullet">
///   <item>Phase 1 — Models and abstractions ✅</item>
///   <item>Phase 2 — Connectors ✅</item>
///   <item>Phase 3 — Full backup engine ✅ (<see cref="RunFullBackupAsync"/>)</item>
///   <item>Phase 4 — Validation engine — Planned</item>
///   <item>Phase 5 — Full-backup restore — Planned</item>
///   <item>Phase 6 — Incremental backup engine — Planned</item>
///   <item>Phase 7 — Incremental restore and validation — Planned</item>
///   <item>Phase 8 — Checkpoint engine — Planned</item>
/// </list>
/// </summary>
public sealed class MongoCustodyService : IMongoCustodyService
{
    private readonly IBackupSourceConnector   _source;
    private readonly IStorageConnector        _storage;
    private readonly IMetadataRepository      _metadata;
    private readonly MongoCustodyConfiguration _config;

    public MongoCustodyService(
        IBackupSourceConnector    source,
        IStorageConnector         storage,
        IMetadataRepository       metadata,
        MongoCustodyConfiguration config)
    {
        _source   = source;
        _storage  = storage;
        _metadata = metadata;
        _config   = config;
    }

    // ------------------------------------------------------------------ //
    //  Health check
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;

        // Run all three probes in parallel — total latency is max, not sum.
        var sourceTask  = _source.CheckHealthAsync(ct);
        var storageTask = _storage.CheckHealthAsync(ct);
        var metaTask    = _metadata.CheckHealthAsync(ct);
        await Task.WhenAll(sourceTask, storageTask, metaTask).ConfigureAwait(false);

        var (sourceOk,  sourceMs,  sourceErr)  = await sourceTask;
        var (storageOk, storageMs, storageErr) = await storageTask;
        var (metaOk,    metaMs,    metaErr)    = await metaTask;

        return new HealthCheckResult
        {
            CheckedAtUtc = checkedAt,
            ComponentResults =
            [
                new ComponentHealthResult
                {
                    ComponentName = "MongoDB",
                    IsHealthy     = sourceOk,
                    LatencyMs     = sourceMs,
                    ErrorMessage  = sourceErr
                },
                new ComponentHealthResult
                {
                    ComponentName = "BlobStorage",
                    IsHealthy     = storageOk,
                    LatencyMs     = storageMs,
                    ErrorMessage  = storageErr
                },
                new ComponentHealthResult
                {
                    ComponentName = "MetadataRepository",
                    IsHealthy     = metaOk,
                    LatencyMs     = metaMs,
                    ErrorMessage  = metaErr
                }
            ]
        };
    }

    // ------------------------------------------------------------------ //
    //  Phase 3 — Full backup
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<BackupResult> RunFullBackupAsync(
        BackupRequest request,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // Pre-flight health check — fail fast before any blobs are created.
        var health = await CheckHealthAsync(ct).ConfigureAwait(false);
        if (!health.IsHealthy)
        {
            var unhealthyComponents = health.ComponentResults
                .Where(c => !c.IsHealthy)
                .Select(c => $"{c.ComponentName}: {c.ErrorMessage}");

            return new BackupResult
            {
                BackupId        = Guid.NewGuid().ToString(),
                BackupType      = BackupType.Full,
                Status          = BackupStatus.Failed,
                StartedAtUtc    = startedAt,
                CompletedAtUtc  = DateTimeOffset.UtcNow,
                FailureReason   = "Pre-flight health check failed — " +
                                  string.Join("; ", unhealthyComponents),
                DatabaseResults = [],
                CorrelationId   = string.IsNullOrWhiteSpace(request.CorrelationId)
                                  ? Guid.NewGuid().ToString()
                                  : request.CorrelationId
            };
        }

        var engine = new FullBackupEngine(_source, _storage, _metadata, _config);
        return await engine.RunAsync(request, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Phase 6 — Incremental backup (not yet implemented)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<BackupResult> RunIncrementalBackupAsync(
        BackupRequest request,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            "Incremental backup engine is planned for Phase 6.");

    // ------------------------------------------------------------------ //
    //  Phase 8 — Checkpoint (not yet implemented)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<BackupResult> RunCheckpointAsync(
        BackupRequest request,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            "Checkpoint engine is planned for Phase 8.");

    // ------------------------------------------------------------------ //
    //  Phase 5 / 7 — Restore (not yet implemented)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            "Restore engine is planned for Phase 5.");

    // ------------------------------------------------------------------ //
    //  Phase 4 — Validation (not yet implemented)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<ValidationResult> ValidateBackupAsync(
        ValidationRequest request,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            "Validation engine is planned for Phase 4.");

    // ------------------------------------------------------------------ //
    //  Restore chain preview (used by Phase 5 and CLI)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupRecord>> ResolveRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default)
        => _metadata.ResolveRestoreChainAsync(databaseName, targetDate, ct);
}
