namespace GoogleDriveBackup.Core
{
    // Base class/record for common progress info
    public abstract record BaseProgressInfo(string CurrentAction, int ProcessedItems, int TotalItemsToProcess);

    // Specific progress for Backup/Restore/Repair
    public record BackupProgressReport : BaseProgressInfo
    {
        public string? CurrentFilePath { get; init; } // The original Drive path being processed
        public string? CurrentArchivePath { get; init; } // The ID/name used in archive
        public BackupProgressReport(string action, int processed, int total, string? filePath = null, string? archivePath = null)
            : base(action, processed, total)
        {
            CurrentFilePath = filePath;
            CurrentArchivePath = archivePath;
        }
    }

    // Result object for Backup operations
    public record BackupResult
    {
        public bool Success { get; init; } = false; // Overall success (archive created?)
        public bool Cancelled { get; init; } = false;
        public string? FinalArchivePath { get; init; }
        public TimeSpan Duration { get; init; }
        public int FilesListed { get; init; }
        public int UnsupportedSkipped { get; init; }
        public int FilesCopied { get; init; }
        public int CopyErrors { get; init; }
        public int DownloadAttempts { get; init; } // Could be copy fallbacks + direct downloads
        public int SuccessfulDownloads { get; init; } // Includes successful copy fallbacks
        public int FailedDownloads { get; init; } // Final failures after retries
        public long TotalBytesDownloaded { get; init; }
        public long TotalBytesCopied { get; init; }
    }

    // Result object for Repair operations
    public record RepairResult
    {
        public bool RepairAttempted { get; init; } = false;
        public bool Cancelled { get; init; } = false;
        // Derived success based on whether all necessary repairs succeeded
        public bool OverallSuccess => RepairAttempted && FailedDownloads == 0 && RepairsSkippedNoId == 0;
        public string? RepairedArchivePath { get; init; } // Path to the NEW archive if created
        public TimeSpan Duration { get; init; }
        public int ManifestEntries { get; init; }
        public int FilesChecked { get; init; }
        public int FilesFoundOk { get; init; }
        public int FilesInitiallyMissing { get; init; }
        public int RepairsSkippedNoId { get; init; }
        public int DownloadsAttempted { get; init; }
        public int DownloadsSucceeded { get; init; }
        public int FailedDownloads { get; init; } // Final failures after retries
        public long TotalBytesRepaired { get; init; }
    }

    // Result object for Restore operations
    public record RestoreResult
    {
        public bool Success { get; init; } = false;
        public bool Cancelled { get; init; } = false;
        public TimeSpan Duration { get; init; }
        public int FilesProcessed { get; init; }
        public int FilesUploaded { get; init; }
        public int FilesSkippedOrFailed { get; init; }
    }
}