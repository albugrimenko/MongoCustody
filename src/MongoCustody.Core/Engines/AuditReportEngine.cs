using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Assembles an <see cref="AuditReport"/> from metadata repository queries.
/// Does not write any files — the caller (CLI or library consumer) is
/// responsible for rendering and persisting the result.
/// </summary>
internal sealed class AuditReportEngine
{
    private readonly IMetadataRepository _metadata;

    public AuditReportEngine(IMetadataRepository metadata)
        => _metadata = metadata;

    /// <summary>
    /// Generates an audit report. Supports two modes controlled by
    /// <see cref="AuditReportRequest"/>:
    /// <list type="bullet">
    ///   <item>Range mode — <see cref="AuditReportRequest.From"/>/<see cref="AuditReportRequest.To"/> set.</item>
    ///   <item>Single-restore mode — <see cref="AuditReportRequest.RestoreId"/> set.</item>
    /// </list>
    /// </summary>
    public async Task<AuditReport> RunAsync(
        AuditReportRequest request,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.RestoreId))
            return await BuildSingleRestoreReportAsync(request, ct).ConfigureAwait(false);

        return await BuildRangeReportAsync(request, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //

    private async Task<AuditReport> BuildSingleRestoreReportAsync(
        AuditReportRequest request,
        CancellationToken ct)
    {
        var record = await _metadata
            .GetRestoreRecordAsync(request.RestoreId!, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Restore record '{request.RestoreId}' not found.");

        var entry = await BuildRestoreEntryAsync(record, ct).ConfigureAwait(false);

        var summary = new AuditSummary
        {
            RestoresCompleted = record.Status == BackupStatus.Completed ? 1 : 0,
            RestoresFailed    = record.Status == BackupStatus.Failed    ? 1 : 0,
        };

        return new AuditReport
        {
            From                = record.RequestedAtUtc,
            To                  = record.CompletedAtUtc ?? record.RequestedAtUtc,
            IsSingleRestoreMode = true,
            GeneratedAtUtc      = DateTimeOffset.UtcNow,
            GeneratedBy         = request.GeneratedBy,
            Summary             = summary,
            Backups             = [],
            Restores            = [entry],
        };
    }

    private async Task<AuditReport> BuildRangeReportAsync(
        AuditReportRequest request,
        CancellationToken ct)
    {
        var from = request.From!.Value;
        var to   = request.To!.Value;

        var backups  = await _metadata
            .ListFullBackupsAsync(request.DatabaseName, from, to, ct)
            .ConfigureAwait(false);

        var restoreRecords = await _metadata
            .ListRestoreRecordsAsync(from, to, ct)
            .ConfigureAwait(false);

        // Apply database filter to restore records if requested.
        if (!string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            restoreRecords = restoreRecords
                .Where(r => string.Equals(
                    r.DatabaseName, request.DatabaseName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList()
                .AsReadOnly();
        }

        var restoreEntries = new List<RestoreAuditEntry>(restoreRecords.Count);
        foreach (var rec in restoreRecords)
        {
            restoreEntries.Add(await BuildRestoreEntryAsync(rec, ct).ConfigureAwait(false));
        }

        var summary = BuildSummary(backups, restoreRecords);

        return new AuditReport
        {
            From           = from,
            To             = to,
            DatabaseFilter = request.DatabaseName,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            GeneratedBy    = request.GeneratedBy,
            Summary        = summary,
            Backups        = backups,
            Restores       = restoreEntries.AsReadOnly(),
        };
    }

    // ------------------------------------------------------------------ //

    private async Task<RestoreAuditEntry> BuildRestoreEntryAsync(
        RestoreRecord record,
        CancellationToken ct)
    {
        if (record.RestoreChain.Count == 0)
            return new RestoreAuditEntry { Record = record, ChainSteps = [] };

        var steps = new List<RestoreChainStep>(record.RestoreChain.Count);
        for (var i = 0; i < record.RestoreChain.Count; i++)
        {
            var folder  = record.RestoreChain[i];
            var backup  = await _metadata
                .GetBackupRecordByFolderAsync(folder, ct)
                .ConfigureAwait(false);

            var docsWritten = backup?.Collections.Sum(c => (long)c.TotalDocuments) ?? 0;

            steps.Add(new RestoreChainStep
            {
                StepNumber       = i + 1,
                BackupType       = backup?.BackupType ?? BackupType.Full,
                BackupDate       = backup?.CompletedAtUtc ?? DateTimeOffset.MinValue,
                BackupFolder     = folder,
                BackupId         = backup?.BackupId,
                DocumentsWritten = docsWritten,
                IsBaselineInsert = i == 0,
            });
        }

        return new RestoreAuditEntry
        {
            Record     = record,
            ChainSteps = steps.AsReadOnly(),
        };
    }

    private static AuditSummary BuildSummary(
        IReadOnlyList<BackupRecord>  backups,
        IReadOnlyList<RestoreRecord> restores)
    {
        // BackupRecord has no Status field — ListFullBackupsAsync only returns
        // completed records, so all listed backups are implicitly completed.
        var totalCollections = backups.Sum(b => b.Collections.Count);
        var totalDocs        = backups.Sum(b => b.Collections.Sum(c => (long)c.TotalDocuments));

        return new AuditSummary
        {
            BackupsCompleted       = backups.Count,
            BackupsFailed          = 0,
            RestoresCompleted      = restores.Count(r => r.Status == BackupStatus.Completed),
            RestoresFailed         = restores.Count(r => r.Status == BackupStatus.Failed),
            TotalCollectionsBacked = totalCollections,
            TotalDocumentsBacked   = totalDocs,
        };
    }
}
