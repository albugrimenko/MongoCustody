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
///   <item>Phase 4 — Validation engine ✅ (<see cref="ValidateBackupAsync"/>)</item>
///   <item>Phase 5 — Restore engine ✅ (<see cref="RestoreAsync"/>)</item>
///   <item>Phase 6 — Incremental backup engine ✅ (<see cref="RunIncrementalBackupAsync"/>)</item>
///   <item>Phase 7 — Incremental restore and validation ✅ (<see cref="ValidateRestoreChainAsync"/>)</item>
///   <item>Phase 8 — Checkpoint engine ✅ (<see cref="RunCheckpointAsync"/>)</item>
/// </list>
/// </summary>
public sealed class MongoCustodyService : IMongoCustodyService
{
    private readonly IBackupSourceConnector         _source;
    private readonly IStorageConnector              _storage;
    private readonly IMetadataRepository            _metadata;
    private readonly MongoCustodyConfiguration      _config;
    private readonly IRestoreTargetConnectorFactory? _restoreTargetFactory;

    public MongoCustodyService(
        IBackupSourceConnector           source,
        IStorageConnector                storage,
        IMetadataRepository              metadata,
        MongoCustodyConfiguration        config,
        IRestoreTargetConnectorFactory?  restoreTargetFactory = null)
    {
        _source               = source;
        _storage              = storage;
        _metadata             = metadata;
        _config               = config;
        _restoreTargetFactory = restoreTargetFactory;
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
        IProgress<Models.OperationProgress>? progress = null,
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
        return await engine.RunAsync(request, progress, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Phase 6 — Incremental backup (not yet implemented)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<BackupResult> RunIncrementalBackupAsync(
        BackupRequest request,
        IProgress<Models.OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var engine = new IncrementalBackupEngine(_source, _storage, _metadata, _config);
        return engine.RunAsync(request, progress, ct);
    }

    // ------------------------------------------------------------------ //
    //  Phase 8 — Checkpoint
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<BackupResult> RunCheckpointAsync(
        BackupRequest request,
        CancellationToken ct = default)
    {
        var engine = new CheckpointEngine(_source, _storage, _metadata, _config);
        return engine.RunAsync(request, ct);
    }

    // ------------------------------------------------------------------ //
    //  Phase 5 — Restore engine
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<Models.OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var startedAt     = DateTimeOffset.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString()
            : request.CorrelationId;

        static RestoreResult QuickFail(string reason, DateTimeOffset at, string cid,
            DateTimeOffset targetDate) => new()
        {
            Status         = BackupStatus.Failed,
            StartedAtUtc   = at,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            TargetDate     = targetDate,
            FailureReason  = reason,
            CorrelationId  = cid
        };

        if (string.IsNullOrWhiteSpace(request.DatabaseName))
            return QuickFail("DatabaseName is required.", startedAt, correlationId, request.TargetDate);

        if (string.IsNullOrWhiteSpace(request.TargetConnectionString))
            return QuickFail("TargetConnectionString is required.", startedAt, correlationId, request.TargetDate);

        if (string.IsNullOrWhiteSpace(request.TargetDatabaseName))
            return QuickFail("TargetDatabaseName is required.", startedAt, correlationId, request.TargetDate);

        if (_restoreTargetFactory is null)
            return QuickFail(
                "No IRestoreTargetConnectorFactory is registered. " +
                "Ensure MongoCustody.Connectors is wired into the DI container.",
                startedAt, correlationId, request.TargetDate);

        // Pre-flight: verify target database exists and is empty before writing the audit record.
        await using var target = _restoreTargetFactory.Create(request.TargetConnectionString);
        try
        {
            await target.EnsureEmptyDatabaseAsync(request.TargetDatabaseName, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return QuickFail(
                $"Target database pre-flight check failed: {ex.Message}",
                startedAt, correlationId, request.TargetDate);
        }

        // Write the pre-restore audit record BEFORE touching the target.
        var restoreRecord = new RestoreRecord
        {
            RestoreId          = Guid.NewGuid().ToString(),
            RequestedAtUtc     = startedAt,
            Status             = BackupStatus.Running,
            DatabaseName       = request.DatabaseName,
            TargetDatabaseName = request.TargetDatabaseName,
            TargetDate         = request.TargetDate,
            Collections        = request.Collections,
            RequestedBy        = request.RequestedBy,
            ApprovedBy         = request.ApprovedBy,
            Reason             = request.Reason,
            CorrelationId      = correlationId,
            IsDryRun           = request.IsDryRun
        };

        try
        {
            await _metadata.SaveRestoreRecordAsync(restoreRecord, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return QuickFail(
                $"Failed to write pre-restore audit record: {ex.Message}",
                startedAt, correlationId, request.TargetDate);
        }

        // Resolve the restore chain.
        IReadOnlyList<BackupRecord> chain;
        try
        {
            chain = await _metadata
                .ResolveRestoreChainAsync(request.DatabaseName, request.TargetDate, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinaliseAuditAndReturnAsync(
                restoreRecord,
                QuickFail("Restore was cancelled.", startedAt, correlationId, request.TargetDate),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            return await FinaliseAuditAndReturnAsync(
                restoreRecord,
                QuickFail($"Failed to resolve restore chain: {ex.Message}",
                    startedAt, correlationId, request.TargetDate),
                ct);
        }

        if (chain.Count == 0)
            return await FinaliseAuditAndReturnAsync(
                restoreRecord,
                QuickFail(
                    $"No backup chain found for database '{request.DatabaseName}' " +
                    $"at or before {request.TargetDate:O}.",
                    startedAt, correlationId, request.TargetDate),
                ct);

        // Run the restore engine (target connector was already created for the pre-flight check).
        var engine = new RestoreEngine(_storage, target, _config);

        RestoreResult result;
        try
        {
            result = await engine.RunAsync(request, chain, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinaliseAuditAndReturnAsync(
                restoreRecord,
                QuickFail("Restore was cancelled.", startedAt, correlationId, request.TargetDate),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            result = QuickFail($"Restore engine threw: {ex.Message}",
                startedAt, correlationId, request.TargetDate);
        }

        return await FinaliseAuditAndReturnAsync(restoreRecord, result, ct);
    }

    /// <inheritdoc />
    public async Task<RestoreResult> RunDryRunAsync(
        RestoreRequest request,
        IProgress<Models.OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Flag so RestoreAsync stamps IsDryRun on the RestoreRecord.
        request.IsDryRun = true;
        RestoreResult result;
        try
        {
            result = await RestoreAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort cleanup on cancellation — data may have been partially written.
            if (_restoreTargetFactory is not null &&
                !string.IsNullOrWhiteSpace(request.TargetConnectionString) &&
                !string.IsNullOrWhiteSpace(request.TargetDatabaseName))
            {
                try
                {
                    await using var t = _restoreTargetFactory.Create(request.TargetConnectionString);
                    await t.DropAllCollectionsAsync(request.TargetDatabaseName, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best-effort; original cancellation is re-thrown */ }
            }
            throw;
        }

        // Only clean up if the restore actually ran (RestoreId is set only after the
        // pre-flight check passes and data starts flowing). If the pre-flight failed
        // (e.g. "database not empty"), nothing was written — don't touch the target.
        if (_restoreTargetFactory is not null &&
            !string.IsNullOrWhiteSpace(result.RestoreId) &&
            !string.IsNullOrWhiteSpace(request.TargetConnectionString) &&
            !string.IsNullOrWhiteSpace(request.TargetDatabaseName))
        {
            await using var target = _restoreTargetFactory.Create(request.TargetConnectionString);
            var cleanupStatus = BackupStatus.Completed;
            DateTimeOffset? cleanedUpAt = null;
            string? cleanupFailure = null;

            try
            {
                await target
                    .DropAllCollectionsAsync(request.TargetDatabaseName, CancellationToken.None)
                    .ConfigureAwait(false);
                cleanedUpAt   = DateTimeOffset.UtcNow;
                cleanupStatus = BackupStatus.Completed;
            }
            catch (Exception ex)
            {
                cleanupStatus  = BackupStatus.Failed;
                cleanupFailure = $"Dry-run cleanup failed — ePHI may remain in " +
                                 $"'{request.TargetDatabaseName}': {ex.Message}";
            }

            // Retrieve the record written by RestoreAsync and patch cleanup fields.
            var record = await _metadata
                .GetRestoreRecordAsync(result.RestoreId, CancellationToken.None)
                .ConfigureAwait(false);

            if (record is not null)
            {
                record.CleanedUpAtUtc = cleanedUpAt;
                record.CleanupStatus  = cleanupStatus;
                if (cleanupFailure is not null)
                    record.FailureReason = string.IsNullOrEmpty(record.FailureReason)
                        ? cleanupFailure
                        : $"{record.FailureReason}; {cleanupFailure}";

                await _metadata
                    .UpdateRestoreRecordAsync(record, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (cleanupFailure is not null)
                result.FailureReason = string.IsNullOrEmpty(result.FailureReason)
                    ? cleanupFailure
                    : $"{result.FailureReason}; {cleanupFailure}";
        }

        return result;
    }

    private async Task<RestoreResult> FinaliseAuditAndReturnAsync(
        RestoreRecord     record,
        RestoreResult     result,
        CancellationToken ct)
    {
        record.Status         = result.Status;
        record.CompletedAtUtc = result.CompletedAtUtc;
        record.FailureReason  = result.FailureReason;
        record.RestoreChain   = result.RestoreChain;
        result.RestoreId      = record.RestoreId;

        try
        {
            await _metadata.UpdateRestoreRecordAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The restore data was written successfully; audit update failure is
            // non-fatal but should be surfaced via structured logging in production.
            // We append a note to the failure reason so the caller can surface it.
            result.FailureReason =
                string.IsNullOrEmpty(result.FailureReason)
                    ? $"[Audit update failed: {ex.Message}]"
                    : $"{result.FailureReason} [Audit update failed: {ex.Message}]";
        }

        return result;
    }

    // ------------------------------------------------------------------ //
    //  Phase 4 — Validation
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<ValidationResult> ValidateBackupAsync(
        ValidationRequest request,
        CancellationToken ct = default)
    {
        var engine = new ValidationEngine(_source, _storage, _metadata, _config);
        return engine.ValidateAsync(request, ct);
    }

    // ------------------------------------------------------------------ //
    //  Phase 7 — Chain validation
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<Models.ChainValidationResult> ValidateRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        ValidationLevel levels,
        CancellationToken ct = default)
    {
        var chain = await _metadata
            .ResolveRestoreChainAsync(databaseName, targetDate, ct)
            .ConfigureAwait(false);

        var engine = new ValidationEngine(_source, _storage, _metadata, _config);
        var steps  = new List<Models.ChainStepValidationResult>(chain.Count);

        for (int i = 0; i < chain.Count; i++)
        {
            var record     = chain[i];
            var validation = await engine
                .ValidateAsync(record, levels, ct)
                .ConfigureAwait(false);

            steps.Add(new Models.ChainStepValidationResult
            {
                StepIndex      = i + 1,
                BackupType     = record.BackupType,
                BackupId       = record.BackupId,
                BackupFolder   = record.BackupFolder,
                CompletedAtUtc = record.CompletedAtUtc,
                Validation     = validation
            });
        }

        return new Models.ChainValidationResult
        {
            DatabaseName  = databaseName,
            RequestedDate = targetDate,
            Steps         = steps
        };
    }

    // ------------------------------------------------------------------ //
    //  Backup listing
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupRecord>> ListFullBackupsAsync(
        string? databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
        => _metadata.ListFullBackupsAsync(databaseName, from, to, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupRecord>> ListAllBackupsAsync(
        string? databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<Models.BackupType>? types = null,
        CancellationToken ct = default)
        => _metadata.ListAllBackupsAsync(databaseName, from, to, types, ct);

    // ------------------------------------------------------------------ //
    //  Restore chain preview (used by Phase 5 and CLI)
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<IReadOnlyList<BackupRecord>> ResolveRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default)
        => _metadata.ResolveRestoreChainAsync(databaseName, targetDate, ct);

    /// <inheritdoc />
    public async Task<Models.RestoreChainPreview> PreviewRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default)
    {
        var chain = await _metadata
            .ResolveRestoreChainAsync(databaseName, targetDate, ct)
            .ConfigureAwait(false);

        var bestDate = chain.Count > 0
            ? DateTimeOffset.FromUnixTimeSeconds(chain[^1].AnchorTs)
            : DateTimeOffset.MinValue;

        var gap = targetDate - bestDate;
        string? warning = null;
        if (chain.Count == 0)
            warning = $"No backup found for '{databaseName}' on or before {targetDate:yyyy-MM-dd}.";
        else if (gap.TotalDays > 1)
            warning =
                $"Best available restore point is {bestDate:yyyy-MM-dd} " +
                $"({(int)gap.TotalDays} day(s) before the requested {targetDate:yyyy-MM-dd}). " +
                $"Data changes in that gap will not be restored.";

        return new Models.RestoreChainPreview
        {
            DatabaseName      = databaseName,
            RequestedDate     = targetDate,
            BestAvailableDate = bestDate,
            GapWarning        = warning,
            Chain             = chain
        };
    }

    // ------------------------------------------------------------------ //
    //  Audit report
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public Task<Models.AuditReport> GenerateAuditReportAsync(
        Models.AuditReportRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RestoreId))
        {
            if (request.From is null || request.To is null)
                throw new ArgumentException(
                    "Either RestoreId or both From and To must be set on AuditReportRequest.");
            if (request.From > request.To)
                throw new ArgumentException("From must not be later than To.");
        }

        var engine = new AuditReportEngine(_metadata);
        return engine.RunAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<Models.IndexValidationResult> ValidateIndexAsync(CancellationToken ct = default)
        => _metadata.ValidateIndexAsync(ct);

    /// <inheritdoc />
    public Task<Models.IndexRebuildResult> RebuildIndexAsync(
        bool dryRun = false,
        CancellationToken ct = default)
        => _metadata.RebuildIndexAsync(dryRun, ct);

    /// <inheritdoc />
    public Task<bool> IsSecondaryIndexPopulatedAsync(CancellationToken ct = default)
        => _metadata.IsSecondaryIndexPopulatedAsync(ct);
}
