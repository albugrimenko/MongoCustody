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
        var checkedAt  = DateTimeOffset.UtcNow;
        var components = new List<ComponentHealthResult>();

        var (sourceOk,  sourceMs,  sourceErr)  = await _source.CheckHealthAsync(ct).ConfigureAwait(false);
        var (storageOk, storageMs, storageErr) = await _storage.CheckHealthAsync(ct).ConfigureAwait(false);
        var (metaOk,    metaMs,    metaErr)    = await _metadata.CheckHealthAsync(ct).ConfigureAwait(false);

        components.Add(new ComponentHealthResult
        {
            ComponentName = "MongoDB",
            IsHealthy     = sourceOk,
            LatencyMs     = sourceMs,
            ErrorMessage  = sourceErr
        });
        components.Add(new ComponentHealthResult
        {
            ComponentName = "BlobStorage",
            IsHealthy     = storageOk,
            LatencyMs     = storageMs,
            ErrorMessage  = storageErr
        });
        components.Add(new ComponentHealthResult
        {
            ComponentName = "MetadataRepository",
            IsHealthy     = metaOk,
            LatencyMs     = metaMs,
            ErrorMessage  = metaErr
        });

        return new HealthCheckResult
        {
            CheckedAtUtc     = checkedAt,
            ComponentResults = components
        };
    }

    // ------------------------------------------------------------------ //
    //  Phase 3 — Full backup
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<BackupResult> RunFullBackupAsync(
        BackupRequest request,
        CancellationToken ct = default)
    {
        var engine = new FullBackupEngine(_source, _storage, _metadata, _config);
        return engine.RunAsync(request, ct);
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
