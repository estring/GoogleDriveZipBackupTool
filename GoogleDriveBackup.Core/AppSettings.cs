
using System;
using System.Collections.Generic; // For List<T>

namespace GoogleDriveBackup.Core
{
    public class AppSettings
    {
        // --- Maximum allowed parallel tasks ---
        public const int MaxAllowedParallelTasks = 10; // Set a reasonable upper limit
        public const int DefaultMaxParallelTasks = 1;  // Default to sequential for safety

        public string? GoogleDriveFolderId
        {
            get; set;
        }
        public string? LocalBackupArchivePath
        {
            get; set;
        }
        public string? LocalTempWorkPath
        {
            get; set;
        }
        public string? GoogleDriveRestoreParentId
        {
            get; set;
        }
        public int? BackupCycleHours
        {
            get; set;
        }
        public bool? ShowVerboseProgress
        {
            get; set;
        }
        public List<string> ExcludedRelativePaths { get; set; } = new List<string>(); // Initialize

        // --- ADDED TIMESTAMP FOR LAST SUCCESSFUL BACKUP ---
        public DateTime? LastSuccessfulBackupUtc
        {
            get; set;
        }

        // --- ADDED Max Parallel Tasks ---
        private int? _maxParallelTasks;
        public int? MaxParallelTasks
        {
            get => _maxParallelTasks;
            set
            {
                // Ensure value is within reasonable bounds if set
                if (value.HasValue)
                {
                    _maxParallelTasks = Math.Clamp(value.Value, 1, MaxAllowedParallelTasks);
                }
                else
                {
                    _maxParallelTasks = null; // Allow null to signify default
                }
            }
        }


        // Defaults
        public const string DefaultArchivePath = @"C:\GoogleDriveBackups";
        public const string DefaultTempPath = @"C:\GoogleDriveTemp";
        public const string DefaultRestoreParent = "root";
        public const int DefaultBackupCycle = 168; // 7 days
        public const bool DefaultVerboseProgress = true;
        // DefaultMaxParallelTasks defined above

        // Helper checks
        public bool IsBackupConfigured()
        {
            return !string.IsNullOrWhiteSpace(GoogleDriveFolderId) &&
                   !string.IsNullOrWhiteSpace(LocalBackupArchivePath) &&
                   !string.IsNullOrWhiteSpace(LocalTempWorkPath);
        }

        public bool IsRestoreConfigured()
        {
            return !string.IsNullOrWhiteSpace(LocalTempWorkPath) &&
                   !string.IsNullOrWhiteSpace(GoogleDriveRestoreParentId);
        }

        // Repair needs Temp path
        public bool IsRepairConfigured()
        {
            return !string.IsNullOrWhiteSpace(LocalTempWorkPath);
        }

        // --- Helper to get effective parallel tasks ---
        public int GetEffectiveMaxParallelTasks()
        {
            return Math.Clamp(MaxParallelTasks ?? DefaultMaxParallelTasks, 1, MaxAllowedParallelTasks);
        }
    }
}