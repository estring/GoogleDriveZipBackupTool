using Google.Apis.Drive.v3;
using Serilog;
using System.Collections.Concurrent; // Added for potential future use, though not strictly needed here now
using System.Collections.Generic;    // Added for List
using System.Diagnostics;
using System.IO;                   // Added for File, Path, Directory etc.
using System.IO.Compression;
using System.Linq;                 // Added for Linq methods
using System.Text.Json;
using System.Threading;            // Added for Interlocked and SemaphoreSlim
using System.Threading.Tasks;      // Added for Task related methods

namespace GoogleDriveBackup.Core
{
    public class RepairManager
    {
        private readonly DriveService _driveService;
        private readonly AppSettings _settings;
        private readonly BackupManager _backupManagerInstance; // Required for calling DownloadFileAsync

        // Public Manifest types needed by BackupManager for incremental check
        public record FileManifestEntry(string GoogleDrivePath, string ArchivePath, long SizeBytes, DateTimeOffset? GoogleDriveModifiedTime);
        public class DiscManifest
        {
            public string? BackupToolVersion
            {
                get; set;
            }
            public List<FileManifestEntry> Files { get; set; } = new List<FileManifestEntry>();
        }

        public RepairManager(DriveService driveService, AppSettings settings, BackupManager backupManager)
        {
            _driveService = driveService;
            _settings = settings;
            _backupManagerInstance = backupManager ?? throw new ArgumentNullException(nameof(backupManager), "BackupManager instance is required for RepairManager.");
        }

        public async Task<RepairResult> RepairBackupAsync(
            string damagedBackupArchivePath, IProgress<BackupProgressReport>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(damagedBackupArchivePath))
            {
                Log.Error("Repair failed: Input archive path not found: {Path}", damagedBackupArchivePath);
                // Return a failed result indicating the file wasn't found
                // OverallSuccess is calculated, no need to set it here.
                return new RepairResult { RepairAttempted = false /*, OverallSuccess = false */ }; // REMOVED OverallSuccess assignment
            }

            string tempWorkPath = Path.GetFullPath(_settings.LocalTempWorkPath ?? AppSettings.DefaultTempPath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string tempExtractDir = Path.Combine(tempWorkPath, $"repair_extract_{Path.GetFileNameWithoutExtension(damagedBackupArchivePath)}_{timestamp}");
            string originalFileName = Path.GetFileNameWithoutExtension(damagedBackupArchivePath);
            string repairedFileName = $"{originalFileName}_REPAIRED_{timestamp}.zip";
            string repairedArchivePath = Path.Combine(Path.GetDirectoryName(damagedBackupArchivePath) ?? _settings.LocalBackupArchivePath ?? AppSettings.DefaultArchivePath, repairedFileName);

            // Use Interlocked counters for thread safety during parallel downloads
            long manifestEntryCount = 0, filesChecked = 0, filesFoundOk = 0, filesMissing = 0, repairsSkippedNoId = 0;
            long downloadsAttempted = 0, downloadsSucceeded = 0, downloadsFailed = 0;
            long totalBytesRepaired = 0;

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool repairActuallyAttempted = false; // Track if we identified missing files to attempt download

            try
            {
                Log.Information("Starting repair process for: {Path}", damagedBackupArchivePath);
                progress?.Report(new BackupProgressReport("Starting Repair", 0, 1, damagedBackupArchivePath));

                cancellationToken.ThrowIfCancellationRequested();
                Log.Debug("Creating temporary extraction directory: {Dir}", tempExtractDir);
                Directory.CreateDirectory(tempExtractDir);

                Log.Information("Extracting original backup archive...");
                progress?.Report(new BackupProgressReport("Extracting Original", 0, 1));
                await Task.Run(() => ZipFile.ExtractToDirectory(damagedBackupArchivePath, tempExtractDir), cancellationToken);
                Log.Information("Extraction complete.");
                progress?.Report(new BackupProgressReport("Extracting Original", 1, 1));

                cancellationToken.ThrowIfCancellationRequested();
                var manifest = await LoadManifestFromDirectoryAsync(tempExtractDir);
                if (manifest == null)
                {
                    // Manifest loading failed, cannot proceed with repair based on manifest
                    Log.Error("Failed to load manifest from extracted archive at {Dir}. Cannot perform repair.", tempExtractDir);
                    // Indicate failure early
                    // OverallSuccess is calculated.
                    return new RepairResult { RepairAttempted = false, /* OverallSuccess = false,*/ Duration = stopwatch.Elapsed }; // REMOVED OverallSuccess assignment
                }

                manifestEntryCount = manifest.Files.Count;
                Log.Information("Manifest loaded successfully: {Count} entries found.", manifestEntryCount);
                progress?.Report(new BackupProgressReport("Manifest Loaded", 1, 1, $"Found {manifestEntryCount} files listed"));

                // --- Stage 1: Identify missing files (Sequential Check) ---
                Log.Information("Checking files listed in manifest against extracted content...");
                progress?.Report(new BackupProgressReport("Checking Files", 0, (int)manifestEntryCount));
                var filesToRepair = new List<(FileManifestEntry Entry, string ExpectedPath, string FileId)>();

                foreach (var entry in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref filesChecked);
                    progress?.Report(new BackupProgressReport("Checking Files", (int)filesChecked, (int)manifestEntryCount, entry.GoogleDrivePath, entry.ArchivePath));

                    if (string.IsNullOrWhiteSpace(entry.ArchivePath))
                    {
                        Log.Warning("Skipping manifest entry with empty ArchivePath. DrivePath: {DrivePath}", entry.GoogleDrivePath ?? "<null>");
                        continue; // Cannot check or repair without ArchivePath
                    }

                    string expectedPath = Path.Combine(tempExtractDir, entry.ArchivePath);
                    if (File.Exists(expectedPath))
                    {
                        Interlocked.Increment(ref filesFoundOk);
                        Log.Verbose("OK: Found file '{Path}'", entry.ArchivePath);
                    }
                    else
                    {
                        Interlocked.Increment(ref filesMissing);
                        Log.Warning("MISSING: Expected file '{ArchiveFile}' (DrivePath: '{DrivePath}') not found in extracted archive.", entry.ArchivePath, entry.GoogleDrivePath ?? "?");
                        string? fileId = ExtractFileIdFromArchivePath(entry.ArchivePath);

                        if (string.IsNullOrWhiteSpace(fileId))
                        {
                            Log.Error("Cannot repair '{File}': No valid File ID could be extracted from ArchivePath.", entry.ArchivePath);
                            Interlocked.Increment(ref repairsSkippedNoId);
                        }
                        else
                        {
                            // Add to list of files needing repair download
                            filesToRepair.Add((entry, expectedPath, fileId));
                        }
                    }
                }
                progress?.Report(new BackupProgressReport("Checking Complete", (int)filesChecked, (int)manifestEntryCount));
                Log.Information("File check complete. Found OK: {OK}, Missing: {Missing}, Skipped (No ID): {Skipped}", filesFoundOk, filesMissing, repairsSkippedNoId);


                // --- Stage 2: Download missing files (Parallel) ---
                if (filesToRepair.Any())
                {
                    repairActuallyAttempted = true; // We will attempt downloads
                    int totalToRepair = filesToRepair.Count;
                    int maxParallel = _settings.GetEffectiveMaxParallelTasks(); // Get configured parallelism
                    Log.Information("Attempting to download {Count} missing files using up to {MaxTasks} parallel tasks...", totalToRepair, maxParallel);
                    progress?.Report(new BackupProgressReport("Downloading Missing", 0, totalToRepair));

                    using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var downloadTasks = new List<Task>(totalToRepair);
                    long repairProcessedCount = 0; // Counter for this stage

                    foreach (var repairInfo in filesToRepair)
                    {
                        // Wait async for a slot in the semaphore respecting the cancellation token
                        await semaphore.WaitAsync(cancellationToken);

                        downloadTasks.Add(Task.Run(async () =>
                        {
                            int? taskId = Task.CurrentId;
                            string taskInfo = taskId.HasValue ? $"Task {taskId.Value}" : "Task ?";
                            bool downloadSuccess = false;
                            try
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                long currentProcessed = Interlocked.Increment(ref repairProcessedCount);
                                progress?.Report(new BackupProgressReport("Downloading Missing", (int)currentProcessed, totalToRepair, repairInfo.Entry.GoogleDrivePath, repairInfo.Entry.ArchivePath));

                                Interlocked.Increment(ref downloadsAttempted);
                                Log.Information("{TaskInfo}: Repair download attempt for File ID: {FileId} ('{ArchiveName}') -> {Path}", taskInfo, repairInfo.FileId, repairInfo.Entry.ArchivePath, repairInfo.ExpectedPath);

                                // Get MimeType needed for BackupManager's DownloadFileAsync
                                // This involves an API call, could potentially be parallelized further, but let's keep it simple for now.
                                string? mime = await GetFileMimeTypeAsync(repairInfo.FileId, cancellationToken);
                                if (mime == null)
                                {
                                    Log.Error("{TaskInfo}: Cannot get MimeType for File ID {Id}. Skipping repair download for '{ArchiveName}'.", taskInfo, repairInfo.FileId, repairInfo.Entry.ArchivePath);
                                    Interlocked.Increment(ref downloadsFailed);
                                    return; // Exit task for this file
                                }

                                // Create DriveItemInfo needed for DownloadFileAsync
                                var dlInfo = new BackupManager.DriveItemInfo(
                                     repairInfo.FileId,
                                     Path.GetFileName(repairInfo.Entry.GoogleDrivePath) ?? repairInfo.Entry.ArchivePath, // Best guess for name
                                     repairInfo.Entry.GoogleDrivePath,
                                     false, // It's a file being repaired
                                     repairInfo.Entry.SizeBytes,
                                     mime,
                                     repairInfo.Entry.GoogleDriveModifiedTime);

                                // Use BackupManager's download logic (which includes retries)
                                downloadSuccess = await _backupManagerInstance.DownloadFileAsync(dlInfo, repairInfo.ExpectedPath, cancellationToken);

                                if (downloadSuccess)
                                {
                                    Interlocked.Increment(ref downloadsSucceeded);
                                    try
                                    {
                                        Interlocked.Add(ref totalBytesRepaired, new FileInfo(repairInfo.ExpectedPath).Length);
                                    }
                                    catch { /* Ignore size error */ }
                                    Log.Information("{TaskInfo}: Repair download SUCCESS: {FileId} -> '{ArchiveFile}'", taskInfo, repairInfo.FileId, repairInfo.Entry.ArchivePath);
                                }
                                else
                                {
                                    Interlocked.Increment(ref downloadsFailed);
                                    Log.Error("{TaskInfo}: Repair download FAILED for: {FileId} -> '{ArchiveFile}'", taskInfo, repairInfo.FileId, repairInfo.Entry.ArchivePath);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                Log.Warning("{TaskInfo}: Repair download cancelled for {FileId} ('{ArchiveName}')", taskInfo, repairInfo.FileId, repairInfo.Entry.ArchivePath);
                                Interlocked.Increment(ref downloadsFailed); // Count cancellation as failure for repair summary
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "{TaskInfo}: Unexpected error during repair download for {FileId} ('{ArchiveName}')", taskInfo, repairInfo.FileId, repairInfo.Entry.ArchivePath);
                                Interlocked.Increment(ref downloadsFailed);
                            }
                            finally
                            {
                                semaphore.Release(); // Crucial: Release the semaphore slot whether success or failure
                            }
                        }, cancellationToken)); // Pass CancellationToken to Task.Run
                    } // End foreach creating download tasks

                    // Wait for all repair downloads to finish
                    await Task.WhenAll(downloadTasks);
                    cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation during WhenAll
                    Log.Information("Repair download phase complete. Succeeded: {Success}, Failed: {Failed}", downloadsSucceeded, downloadsFailed);
                    progress?.Report(new BackupProgressReport("Downloading Complete", totalToRepair, totalToRepair));

                }
                else if (filesMissing > 0 && repairsSkippedNoId == filesMissing)
                {
                    // Files were missing, but none had usable IDs
                    repairActuallyAttempted = true; // We identified missing files, so an attempt was made conceptually
                    Log.Warning("Found {MissingCount} missing files, but none could be repaired due to missing File IDs in archive paths.", filesMissing);
                }
                else
                {
                    Log.Information("No missing files found that require repair downloads.");
                }


                // --- Stage 3: Create Repaired Archive (Sequential) ---
                stopwatch.Stop(); // Stop main timer after checks/downloads
                string? finalResultPath = null;
                bool wasPerfect = downloadsFailed == 0 && repairsSkippedNoId == 0;

                // Create archive only if repair was attempted AND successful, OR if no repair was needed initially
                if ((repairActuallyAttempted && wasPerfect) || (!repairActuallyAttempted && filesMissing == 0))
                {
                    if (downloadsSucceeded > 0)
                    {
                        Log.Information("All required repairs succeeded. Creating new archive.");
                    }
                    else if (filesMissing == 0)
                    {
                        Log.Information("No missing files found. Re-creating archive.");
                    }
                    else
                    {
                        Log.Information("Repair attempted but no files were successfully downloaded (e.g., all skipped due to no ID). Archive will reflect initial state.");
                    } // Case where missing > 0, skipped = missing, downloads = 0

                    cancellationToken.ThrowIfCancellationRequested();
                    Log.Information("Creating repaired archive: {Path}", repairedArchivePath);
                    progress?.Report(new BackupProgressReport("Creating Repaired Archive", 0, 1));
                    if (File.Exists(repairedArchivePath))
                    {
                        Log.Warning("Overwriting existing repaired archive: {Path}", repairedArchivePath);
                        File.Delete(repairedArchivePath);
                    }
                    await Task.Run(() => ZipFile.CreateFromDirectory(tempExtractDir, repairedArchivePath, CompressionLevel.Optimal, includeBaseDirectory: false), cancellationToken);
                    finalResultPath = repairedArchivePath;
                    Log.Information("Repaired archive created: {Path}", finalResultPath);
                    progress?.Report(new BackupProgressReport("Creating Repaired Archive", 1, 1));
                }
                else if (repairActuallyAttempted) // Repair was attempted but failed for some files
                {
                    Log.Warning("Repaired archive will NOT be created because some files failed to download ({Failures}) or were skipped due to missing IDs ({Skipped}). Check logs.", downloadsFailed, repairsSkippedNoId);
                    progress?.Report(new BackupProgressReport("Repair Incomplete", 1, 1));
                }
                else
                {
                    Log.Information("Repair not attempted or not needed, and no files were missing. No new archive created.");
                    progress?.Report(new BackupProgressReport("Repair Not Needed", 1, 1));
                }


                // Populate final result using Interlocked counters
                // OverallSuccess is calculated automatically by the record's getter
                return new RepairResult
                {
                    RepairAttempted = repairActuallyAttempted,
                    Cancelled = false, // Handled by catch block
                    RepairedArchivePath = finalResultPath, // Null if not created
                    Duration = stopwatch.Elapsed,
                    ManifestEntries = (int)manifestEntryCount,
                    FilesChecked = (int)filesChecked,
                    FilesFoundOk = (int)filesFoundOk,
                    FilesInitiallyMissing = (int)filesMissing,
                    RepairsSkippedNoId = (int)repairsSkippedNoId,
                    DownloadsAttempted = (int)downloadsAttempted,
                    DownloadsSucceeded = (int)downloadsSucceeded,
                    FailedDownloads = (int)downloadsFailed,
                    TotalBytesRepaired = totalBytesRepaired
                };
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Repair cancelled.");
                // Return result reflecting cancellation and current state
                return new RepairResult
                {
                    Cancelled = true,
                    RepairAttempted = repairActuallyAttempted,
                    Duration = stopwatch.Elapsed,
                    ManifestEntries = (int)manifestEntryCount,
                    FilesChecked = (int)filesChecked,
                    FilesFoundOk = (int)filesFoundOk,
                    FilesInitiallyMissing = (int)filesMissing,
                    RepairsSkippedNoId = (int)repairsSkippedNoId,
                    DownloadsAttempted = (int)downloadsAttempted,
                    DownloadsSucceeded = (int)downloadsSucceeded,
                    FailedDownloads = (int)downloadsFailed,
                    TotalBytesRepaired = totalBytesRepaired
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Error(ex, "CRITICAL ERROR during repair for archive: {Path}", damagedBackupArchivePath);
                // Return result reflecting failure and current state
                return new RepairResult
                {
                    Cancelled = false,
                    RepairAttempted = repairActuallyAttempted,
                    Duration = stopwatch.Elapsed,
                    ManifestEntries = (int)manifestEntryCount,
                    FilesChecked = (int)filesChecked,
                    FilesFoundOk = (int)filesFoundOk,
                    FilesInitiallyMissing = (int)filesMissing,
                    RepairsSkippedNoId = (int)repairsSkippedNoId,
                    DownloadsAttempted = (int)downloadsAttempted,
                    DownloadsSucceeded = (int)downloadsSucceeded,
                    FailedDownloads = (int)downloadsFailed,
                    TotalBytesRepaired = totalBytesRepaired
                };
            }
            finally
            {
                // --- Cleanup ---
                Log.Debug("Cleaning repair temporary directory: {Dir}", tempExtractDir);
                try
                {
                    if (Directory.Exists(tempExtractDir))
                        Directory.Delete(tempExtractDir, true);
                }
                catch (Exception ex) { Log.Warning(ex, "Cleanup failed for directory: {Dir}", tempExtractDir); }
            }
        }

        // Loads manifest from specified directory (public for BackupManager)
        public async Task<DiscManifest?> LoadManifestFromDirectoryAsync(string directoryPath)
        {
            string manifestPath = Path.Combine(directoryPath, "_manifest.json");
            if (!File.Exists(manifestPath))
            {
                Log.Error("Manifest file not found: {Path}", manifestPath);
                return null;
            }
            Log.Information("Reading manifest file: {Path}", manifestPath);
            try
            {
                string json = await File.ReadAllTextAsync(manifestPath);
                var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var m = JsonSerializer.Deserialize<DiscManifest>(json, opt);
                if (m?.Files == null)
                {
                    Log.Error("Failed parsing manifest content or 'Files' array is null: {Path}", manifestPath);
                    return null;
                }
                return m;
            }
            catch (JsonException jsonEx) { Log.Error(jsonEx, "JSON Error reading/parsing manifest: {Path}", manifestPath); return null; }
            catch (Exception ex) { Log.Error(ex, "General Error reading/parsing manifest: {Path}", manifestPath); return null; }
        }

        // Gets current MimeType using Drive API (needed for DownloadFileAsync)
        private async Task<string?> GetFileMimeTypeAsync(string fileId, CancellationToken token)
        {
            if (string.IsNullOrEmpty(fileId))
                return null;
            try
            {
                var req = _driveService.Files.Get(fileId);
                req.Fields = "mimeType";
                req.SupportsAllDrives = true;
                var f = await req.ExecuteAsync(token);
                Log.Debug("Retrieved MimeType '{Type}' for File ID {Id}", f?.MimeType ?? "<null>", fileId);
                return f?.MimeType;
            }
            catch (OperationCanceledException) { throw; } // Let cancellation bubble up
            catch (Google.GoogleApiException apiEx)
            {
                Log.Error(apiEx, "Google API Error getting MimeType for File ID {Id}. Status: {Status}", fileId, apiEx.HttpStatusCode);
                return null; // Cannot proceed without MimeType potentially
            }
            catch (Exception ex) { Log.Error(ex, "Failed getting MimeType for File ID {Id}", fileId); return null; }
        }

        // Extracts File ID from archive path (public for BackupManager too)
        // Assumes filename *without extension* is the Google Drive File ID.
        public string? ExtractFileIdFromArchivePath(string? archivePath)
        {
            if (string.IsNullOrEmpty(archivePath))
                return null;
            // Use Path.GetFileNameWithoutExtension for robustness
            string nameWithoutExt = Path.GetFileNameWithoutExtension(archivePath);
            // Basic validation: Check if it looks somewhat like a Drive ID (alphanumeric, -, _)
            // This is not foolproof but prevents using clearly invalid names like "." or ".."
            if (string.IsNullOrWhiteSpace(nameWithoutExt) || nameWithoutExt.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
            {
                Log.Warning("Extracted name '{Name}' from path '{Path}' does not appear to be a valid Google Drive File ID.", nameWithoutExt, archivePath);
                return null;
            }
            return nameWithoutExt;
        }
    }
}