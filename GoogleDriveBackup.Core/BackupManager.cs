using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Serilog;
using System.Collections.Concurrent; // For thread-safe collections if needed
using System.Collections.Generic; // Added for List
using System.Diagnostics;
using System.IO; // Added for Path, File, FileInfo, FileMode, FileAccess
using System.IO.Compression;
using System.Linq; // Added for Linq methods
using System.Text.Json;
using System.Threading; // Added for Interlocked and SemaphoreSlim
using System.Threading.Tasks; // Added for Task related methods
using GFile = Google.Apis.Drive.v3.Data.File; // Alias for Google File

namespace GoogleDriveBackup.Core
{
    public class BackupManager
    {
        private readonly DriveService _driveService;
        private readonly AppSettings _settings;

        // Public record for external use
        public record DriveItemInfo(string Id, string Name, string Path, bool IsFolder, long? Size, string? MimeType, DateTimeOffset? ModifiedTime);
        // Internal record for manifest data
        private record FileManifestEntry(string GoogleDrivePath, string ArchivePath, long SizeBytes, DateTimeOffset? GoogleDriveModifiedTime);
        // Internal helpers
        private enum FileAction
        {
            SkipUnsupported, Download, Copy
        }
        private record FileProcessingInfo(DriveItemInfo CurrentInfo, string NewArchivePath, FileAction Action, string? OldArchivePath = null);

        // Static dictionaries for MIME type handling (thread-safe population)
        private static readonly ConcurrentDictionary<string, string> GoogleToExportMimeType = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, string> ExportMimeTypeToExtension = new ConcurrentDictionary<string, string>();
        private static readonly object MimeLock = new object(); // Lock for initializing dictionaries

        public BackupManager(DriveService driveService, AppSettings settings)
        {
            _driveService = driveService;
            _settings = settings;
            // Ensure MIME dictionaries are populated only once
            EnsureMimeDictionariesPopulated();
        }

        private static void EnsureMimeDictionariesPopulated()
        {
            // Quick check without lock
            if (GoogleToExportMimeType.IsEmpty)
            {
                lock (MimeLock)
                {
                    // Double-check inside lock
                    if (GoogleToExportMimeType.IsEmpty)
                    {
                        // Populate Google -> Export MIME Types
                        GoogleToExportMimeType.TryAdd("application/vnd.google-apps.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
                        GoogleToExportMimeType.TryAdd("application/vnd.google-apps.spreadsheet", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                        GoogleToExportMimeType.TryAdd("application/vnd.google-apps.presentation", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
                        GoogleToExportMimeType.TryAdd("application/vnd.google-apps.drawing", "image/png");
                        // Add other common exportable types if needed (e.g., Google Apps Script -> JSON)
                        // GoogleToExportMimeType.TryAdd("application/vnd.google-apps.script", "application/vnd.google-apps.script+json");
                    }
                }
            }
            // Similar pattern for Export -> Extension
            if (ExportMimeTypeToExtension.IsEmpty)
            {
                lock (MimeLock)
                {
                    if (ExportMimeTypeToExtension.IsEmpty)
                    {
                        // Populate Export MIME Types -> File Extensions
                        ExportMimeTypeToExtension.TryAdd("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");
                        ExportMimeTypeToExtension.TryAdd("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx");
                        ExportMimeTypeToExtension.TryAdd("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx");
                        ExportMimeTypeToExtension.TryAdd("image/png", ".png");
                        ExportMimeTypeToExtension.TryAdd("image/jpeg", ".jpg"); // Common non-Google type often included
                        ExportMimeTypeToExtension.TryAdd("image/svg+xml", ".svg");
                        ExportMimeTypeToExtension.TryAdd("application/pdf", ".pdf"); // Common non-Google type often included
                                                                                     // ExportMimeTypeToExtension.TryAdd("application/vnd.google-apps.script+json", ".json");
                    }
                }
            }
        }


        public async Task<BackupResult> StartBackupAsync(
            string googleDriveFolderId, string? previousBackupArchivePath = null,
            IProgress<BackupProgressReport>? progress = null, CancellationToken cancellationToken = default)
        {
            string archiveStoragePath = Path.GetFullPath(_settings.LocalBackupArchivePath ?? AppSettings.DefaultArchivePath);
            string tempWorkPath = Path.GetFullPath(_settings.LocalTempWorkPath ?? AppSettings.DefaultTempPath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string newTempDownloadDir = Path.Combine(tempWorkPath, $"download_{timestamp}");
            string oldTempExtractDir = Path.Combine(tempWorkPath, $"oldextract_{timestamp}");
            string finalArchivePath = Path.Combine(archiveStoragePath, $"GDriveBackup_{timestamp}.zip");
            var newManifestEntries = new ConcurrentBag<FileManifestEntry>(); // Use ConcurrentBag for thread-safe adds
            var filesToProcess = new List<FileProcessingInfo>();

            // --- Use Interlocked for thread-safe counter updates ---
            long filesListed = 0, unsupportedSkipped = 0, filesCopied = 0, copyErrors = 0;
            long downloadAttempts = 0, successfulDownloads = 0, failedDownloads = 0;
            long totalBytesDownloaded = 0, totalBytesCopied = 0;
            long filesAnalyzed = 0; // Counter for analysis loop progress

            Stopwatch stopwatch = Stopwatch.StartNew();
            // Use ConcurrentDictionary for thread-safe access if needed, though it's populated sequentially here
            Dictionary<string, RepairManager.FileManifestEntry>? oldManifestLookup = null;

            try
            {
                // --- Stage 1: Handle Previous Backup (Sequential is fine here) ---
                if (!string.IsNullOrWhiteSpace(previousBackupArchivePath) && System.IO.File.Exists(previousBackupArchivePath))
                {
                    Log.Information("Previous backup provided: {Path}. Preparing...", previousBackupArchivePath);
                    progress?.Report(new BackupProgressReport("Preparing Incremental", 0, 1, previousBackupArchivePath));
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Log.Debug("Creating old temp dir: {Dir}", oldTempExtractDir);
                        Directory.CreateDirectory(oldTempExtractDir);
                        Log.Information("Extracting previous backup...");
                        progress?.Report(new BackupProgressReport("Extracting Previous", 0, 1));
                        await Task.Run(() => ZipFile.ExtractToDirectory(previousBackupArchivePath, oldTempExtractDir), cancellationToken);
                        Log.Information("Previous backup extracted.");
                        cancellationToken.ThrowIfCancellationRequested();
                        // Assuming RepairManager is accessible or LoadManifestFromDirectoryAsync is replicated/shared
                        var repairManagerHelper = new RepairManager(_driveService, _settings, this);
                        var oldManifest = await repairManagerHelper.LoadManifestFromDirectoryAsync(oldTempExtractDir);
                        if (oldManifest != null)
                        {
                            // Create lookup dictionary (File ID -> Manifest Entry)
                            oldManifestLookup = oldManifest.Files
                               .Where(f => !string.IsNullOrEmpty(f.ArchivePath)) // Ensure entry has an archive path
                               .Select(f => new { FileId = ExtractFileIdFromArchivePath(f.ArchivePath), Entry = f }) // Extract ID
                               .Where(x => x.FileId != null) // Ensure ID extraction was successful
                               .GroupBy(x => x.FileId!) // Group by File ID
                               .ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.OrdinalIgnoreCase); // Use First entry for duplicates, case-insensitive ID
                            Log.Information("Loaded {Count} previous entries from manifest.", oldManifestLookup.Count);
                        }
                        else
                        {
                            Log.Warning("Failed to load previous manifest.");
                            progress?.Report(new BackupProgressReport("Incremental Prep Failed", 1, 1));
                        }
                    }
                    catch (OperationCanceledException) { Log.Warning("Old backup processing cancelled."); throw; }
                    catch (Exception ex) { Log.Error(ex, "Error processing previous backup {Path}", previousBackupArchivePath); oldManifestLookup = null; /* Cleanup handled in finally*/ }
                    progress?.Report(new BackupProgressReport("Preparation Complete", 1, 1));
                    Log.Information("Previous backup processing took {Sec:F2}s.", stopwatch.Elapsed.TotalSeconds);
                }
                else if (!string.IsNullOrWhiteSpace(previousBackupArchivePath))
                {
                    Log.Warning("Previous backup not found: {Path}", previousBackupArchivePath);
                }

                // --- Stage 2: List Current Google Drive Files (Sequential is likely fine/necessary) ---
                cancellationToken.ThrowIfCancellationRequested();
                Log.Debug("Creating new temp dir: {Dir}", newTempDownloadDir);
                Directory.CreateDirectory(newTempDownloadDir);
                Log.Information("Listing files from GDrive folder {Id}...", googleDriveFolderId);
                progress?.Report(new BackupProgressReport("Listing Drive Files", 0, 1));
                stopwatch.Restart();
                var allCurrentItems = await ListAllFilesAndFoldersAsync(googleDriveFolderId, cancellationToken);
                var currentFilesToBackup = allCurrentItems.Where(item => !item.IsFolder).ToList();
                filesListed = currentFilesToBackup.Count; // Total files found *before* analysis/skipping
                stopwatch.Stop();
                Log.Information("Found {Count} files listed in {Sec:F2}s.", filesListed, stopwatch.Elapsed.TotalSeconds);
                progress?.Report(new BackupProgressReport("Listing Complete", 1, 1, $"Found {filesListed} files"));


                // --- Stage 3: Decision Loop (Sequential Analysis) ---
                // If no files to backup, handle early exit
                if (filesListed == 0) // Check if the initial listing found any files
                {
                    if (!allCurrentItems.Any(i => i.IsFolder))
                    {
                        Log.Information("No files or folders found in the target Drive location. Creating empty archive.");
                    }
                    else
                    {
                        Log.Information("No files found in the target Drive location (only folders). Creating empty archive.");
                    }
                    // Jump to archive creation (which will be empty)
                }
                else
                {
                    Log.Information("Analyzing {Count} files to determine actions...", filesListed);
                    progress?.Report(new BackupProgressReport("Analyzing Files", 0, (int)filesListed));
                    stopwatch.Restart();
                    foreach (var currentInfo in currentFilesToBackup)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref filesAnalyzed); // Track progress of analysis

                        string fileId = currentInfo.Id;
                        string origExt = Path.GetExtension(currentInfo.Name);
                        string targetExt = origExt;
                        FileAction action = FileAction.Download; // Assume download initially
                        string? oldArchivePath = null;
                        bool isGType = false;

                        // Check if it's a Google Doc type that needs export
                        if (currentInfo.MimeType != null && GoogleToExportMimeType.TryGetValue(currentInfo.MimeType, out var exportMime))
                        {
                            isGType = true;
                            if (!ExportMimeTypeToExtension.TryGetValue(exportMime, out targetExt!))
                                targetExt = origExt; // Fallback extension if needed
                            action = FileAction.Download; // Google types always need download/export
                            Log.Verbose("Analysis: GDoc {Id} ('{Name}'). Action: Download (Export)", fileId, currentInfo.Name);
                        }
                        // Check for other unsupported Google types (shortcuts, forms, sites etc.)
                        else if (currentInfo.MimeType?.StartsWith("application/vnd.google-apps") ?? false)
                        {
                            action = FileAction.SkipUnsupported;
                            Interlocked.Increment(ref unsupportedSkipped);
                            Log.Information("Analysis: Unsupported Google type {Id} ('{Name}', Mime: {Mime}). Action: Skip", fileId, currentInfo.Name, currentInfo.MimeType);
                        }
                        // If not a Google type, check against previous manifest for potential copy
                        else if (oldManifestLookup != null && oldManifestLookup.TryGetValue(fileId, out var oldEntry))
                        {
                            // Compare modification times (use helper for robustness)
                            if (oldEntry.GoogleDriveModifiedTime.HasValue && currentInfo.ModifiedTime.HasValue &&
                                AreDateTimesEquivalent(oldEntry.GoogleDriveModifiedTime.Value, currentInfo.ModifiedTime.Value))
                            {
                                action = FileAction.Copy;
                                oldArchivePath = oldEntry.ArchivePath; // Store path from old archive
                                Log.Verbose("Analysis: Found match {Id} ('{Name}'). Action: Copy from '{OldPath}'", fileId, currentInfo.Name, oldArchivePath);
                            }
                            else
                            {
                                action = FileAction.Download; // Time mismatch or missing time, re-download
                                Log.Information("Analysis: Time mismatch/missing for {Id} ('{Name}'). Action: Download", fileId, currentInfo.Name);
                            }
                        }
                        else
                        {
                            action = FileAction.Download; // New file or no previous manifest
                            Log.Verbose("Analysis: New/Unknown file {Id} ('{Name}'). Action: Download", fileId, currentInfo.Name);
                        }

                        // If action is not skip, prepare processing info and manifest entry
                        if (action != FileAction.SkipUnsupported)
                        {
                            string newArchivePath = fileId + targetExt; // Use File ID + Export/Original Extension
                            filesToProcess.Add(new FileProcessingInfo(currentInfo, newArchivePath, action, oldArchivePath));
                            // Add to manifest bag (thread-safe add)
                            newManifestEntries.Add(new FileManifestEntry(currentInfo.Path, newArchivePath, currentInfo.Size ?? 0, currentInfo.ModifiedTime));
                        }
                        progress?.Report(new BackupProgressReport("Analyzing Files", (int)filesAnalyzed, (int)filesListed, currentInfo.Path));
                    }
                    stopwatch.Stop();
                    Log.Information("Analysis complete in {Sec:F2}s. Files to process: {ProcessCount}", stopwatch.Elapsed.TotalSeconds, filesToProcess.Count);


                    // --- Stage 4: Execution Loop (Parallel) ---
                    int totalToExecute = filesToProcess.Count;
                    if (totalToExecute > 0)
                    {
                        int maxParallel = _settings.GetEffectiveMaxParallelTasks();
                        Log.Information("Processing {Count} files using up to {MaxTasks} parallel tasks...", totalToExecute, maxParallel);
                        progress?.Report(new BackupProgressReport("Processing Files", 0, totalToExecute));
                        stopwatch.Restart();

                        using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                        var tasks = new List<Task>(totalToExecute);
                        long processedCount = 0; // Use Interlocked for this counter

                        foreach (var procInfo in filesToProcess)
                        {
                            await semaphore.WaitAsync(cancellationToken); // Wait for an execution slot

                            tasks.Add(Task.Run(async () => // Use Task.Run to ensure it runs on thread pool
                            {
                                int? taskId = Task.CurrentId;
                                string taskInfo = taskId.HasValue ? $"Task {taskId.Value}" : "Task ?";
                                try
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    long currentProcessed = Interlocked.Increment(ref processedCount);
                                    string actionVerb = procInfo.Action == FileAction.Copy ? "Copying" : "Downloading";
                                    progress?.Report(new BackupProgressReport(actionVerb, (int)currentProcessed, totalToExecute, procInfo.CurrentInfo.Path, procInfo.NewArchivePath));

                                    string targetPath = Path.Combine(newTempDownloadDir, procInfo.NewArchivePath);

                                    if (procInfo.Action == FileAction.Copy)
                                    {
                                        string sourcePath = Path.Combine(oldTempExtractDir, procInfo.OldArchivePath!);
                                        Log.Information("{TaskInfo}: Copying: {Src} -> {Dst}", taskInfo, Path.GetFileName(sourcePath), Path.GetFileName(targetPath));
                                        try
                                        {
                                            if (System.IO.File.Exists(sourcePath))
                                            {
                                                System.IO.File.Copy(sourcePath, targetPath, true);
                                                Interlocked.Increment(ref filesCopied);
                                                try
                                                {
                                                    Interlocked.Add(ref totalBytesCopied, new FileInfo(targetPath).Length);
                                                }
                                                catch { /* Ignore size error */ }
                                                Log.Information("{TaskInfo}: Copy OK: {Id}", taskInfo, procInfo.CurrentInfo.Id);
                                            }
                                            else
                                            {
                                                Log.Warning("{TaskInfo}: Copy source missing: {Src}. Falling back to download.", taskInfo, sourcePath);
                                                Interlocked.Increment(ref copyErrors);
                                                Interlocked.Increment(ref downloadAttempts);
                                                bool downloadOk = await DownloadFileAsync(procInfo.CurrentInfo, targetPath, cancellationToken);
                                                if (downloadOk)
                                                {
                                                    Interlocked.Increment(ref successfulDownloads);
                                                    try
                                                    {
                                                        Interlocked.Add(ref totalBytesDownloaded, new FileInfo(targetPath).Length);
                                                    }
                                                    catch { /* Ignore size error */ }
                                                }
                                                else
                                                {
                                                    Interlocked.Increment(ref failedDownloads);
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Error(ex, "{TaskInfo}: Copy error for {Src}. Falling back to download.", taskInfo, sourcePath);
                                            Interlocked.Increment(ref copyErrors);
                                            Interlocked.Increment(ref downloadAttempts);
                                            bool downloadOk = await DownloadFileAsync(procInfo.CurrentInfo, targetPath, cancellationToken);
                                            if (downloadOk)
                                            {
                                                Interlocked.Increment(ref successfulDownloads);
                                                try
                                                {
                                                    Interlocked.Add(ref totalBytesDownloaded, new FileInfo(targetPath).Length);
                                                }
                                                catch { /* Ignore size error */ }
                                            }
                                            else
                                            {
                                                Interlocked.Increment(ref failedDownloads);
                                            }
                                        }
                                    }
                                    else if (procInfo.Action == FileAction.Download)
                                    {
                                        Log.Information("{TaskInfo}: Downloading: {Id} ('{Name}') -> {Dst}", taskInfo, procInfo.CurrentInfo.Id, procInfo.CurrentInfo.Name, Path.GetFileName(targetPath));
                                        Interlocked.Increment(ref downloadAttempts);
                                        bool downloadOk = await DownloadFileAsync(procInfo.CurrentInfo, targetPath, cancellationToken);
                                        if (downloadOk)
                                        {
                                            Interlocked.Increment(ref successfulDownloads);
                                            try
                                            {
                                                Interlocked.Add(ref totalBytesDownloaded, new FileInfo(targetPath).Length);
                                            }
                                            catch { /* Ignore size error */ }
                                            Log.Information("{TaskInfo}: Download OK: {Id}", taskInfo, procInfo.CurrentInfo.Id);
                                        }
                                        else
                                        {
                                            Interlocked.Increment(ref failedDownloads);
                                            Log.Error("{TaskInfo}: Download FAILED: {Id}", taskInfo, procInfo.CurrentInfo.Id);
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    Log.Warning("{TaskInfo}: Processing cancelled for {FileId}", taskInfo, procInfo.CurrentInfo.Id);
                                    // Don't re-throw here, let Task.WhenAll catch it if needed
                                    // Mark as failed for summary purposes
                                    if (procInfo.Action == FileAction.Download)
                                        Interlocked.Increment(ref failedDownloads);
                                    if (procInfo.Action == FileAction.Copy)
                                        Interlocked.Increment(ref copyErrors); // Treat cancelled copy as error
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "{TaskInfo}: Unexpected error processing file {FileId}", taskInfo, procInfo.CurrentInfo.Id);
                                    // Increment appropriate error counter if possible
                                    if (procInfo.Action == FileAction.Download)
                                        Interlocked.Increment(ref failedDownloads);
                                    if (procInfo.Action == FileAction.Copy)
                                        Interlocked.Increment(ref copyErrors);
                                }
                                finally
                                {
                                    semaphore.Release(); // Release the slot IMPORTANTLY in a finally block
                                }
                            }, cancellationToken));
                        }

                        // Wait for all tasks to complete
                        await Task.WhenAll(tasks);
                        cancellationToken.ThrowIfCancellationRequested(); // Check if cancellation occurred during WhenAll

                        stopwatch.Stop();
                        Log.Information("File processing complete in {Sec:F2}s.", stopwatch.Elapsed.TotalSeconds);
                        progress?.Report(new BackupProgressReport("Processing Complete", totalToExecute, totalToExecute));

                        if (failedDownloads > 0 || copyErrors > 0)
                        {
                            Log.Warning("Backup processing finished with {Failures} download failures and {CopyErrors} copy errors.", failedDownloads, copyErrors);
                        }
                        else
                        {
                            Log.Information("Backup processing finished with no reported errors.");
                        }
                    }
                    else
                    {
                        Log.Information("No files determined to need processing (copy/download).");
                    }
                } // End if filesListed > 0

                // --- Stage 5: Finalize (Sequential) ---
                cancellationToken.ThrowIfCancellationRequested();
                Log.Information("Writing manifest...");
                progress?.Report(new BackupProgressReport("Writing Manifest", 0, 1));
                // Convert ConcurrentBag to List for sorting before writing
                var sortedManifest = newManifestEntries.OrderBy(e => e.GoogleDrivePath).ToList();
                await WriteManifestAsync(newTempDownloadDir, sortedManifest); // Pass the sorted list
                progress?.Report(new BackupProgressReport("Writing Manifest", 1, 1));

                cancellationToken.ThrowIfCancellationRequested();
                Log.Information("Creating archive: {Path}", finalArchivePath);
                progress?.Report(new BackupProgressReport("Creating Archive", 0, 1));
                if (System.IO.File.Exists(finalArchivePath))
                {
                    Log.Warning("Overwriting existing archive at: {Path}", finalArchivePath);
                    System.IO.File.Delete(finalArchivePath);
                }
                await Task.Run(() => ZipFile.CreateFromDirectory(newTempDownloadDir, finalArchivePath, CompressionLevel.Optimal, includeBaseDirectory: false), cancellationToken);
                Log.Information("Archive created: {Path}", finalArchivePath);
                progress?.Report(new BackupProgressReport("Creating Archive", 1, 1));

                // Use final Interlocked values for result
                return new BackupResult
                {
                    Success = failedDownloads == 0 && copyErrors == 0, // Success only if no errors during processing
                    Cancelled = false, // Handled by catch block
                    FinalArchivePath = finalArchivePath,
                    Duration = stopwatch.Elapsed, // Note: Stopwatch was reset at various stages, this reflects only the last stage's duration + finalization
                    FilesListed = (int)filesListed,
                    UnsupportedSkipped = (int)unsupportedSkipped,
                    FilesCopied = (int)filesCopied,
                    CopyErrors = (int)copyErrors,
                    DownloadAttempts = (int)downloadAttempts,
                    SuccessfulDownloads = (int)successfulDownloads,
                    FailedDownloads = (int)failedDownloads,
                    TotalBytesDownloaded = totalBytesDownloaded,
                    TotalBytesCopied = totalBytesCopied
                };
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Backup cancelled.");
                // Return result reflecting cancellation and current state
                return new BackupResult
                {
                    Success = false,
                    Cancelled = true,
                    Duration = stopwatch.Elapsed,
                    FilesListed = (int)filesListed,
                    UnsupportedSkipped = (int)unsupportedSkipped,
                    FilesCopied = (int)filesCopied,
                    CopyErrors = (int)copyErrors,
                    DownloadAttempts = (int)downloadAttempts,
                    SuccessfulDownloads = (int)successfulDownloads,
                    FailedDownloads = (int)failedDownloads,
                    TotalBytesDownloaded = totalBytesDownloaded,
                    TotalBytesCopied = totalBytesCopied
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Error(ex, "CRITICAL ERROR during backup for {Id}.", googleDriveFolderId);
                return new BackupResult
                {
                    Success = false,
                    Cancelled = false,
                    Duration = stopwatch.Elapsed,
                    FilesListed = (int)filesListed,
                    UnsupportedSkipped = (int)unsupportedSkipped,
                    FilesCopied = (int)filesCopied,
                    CopyErrors = (int)copyErrors,
                    DownloadAttempts = (int)downloadAttempts,
                    SuccessfulDownloads = (int)successfulDownloads,
                    FailedDownloads = (int)failedDownloads,
                    TotalBytesDownloaded = totalBytesDownloaded,
                    TotalBytesCopied = totalBytesCopied
                };
            }
            finally
            {
                // --- Stage 6: Cleanup ---
                Log.Debug("Cleaning up temp dirs...");
                try
                {
                    if (Directory.Exists(newTempDownloadDir))
                        Directory.Delete(newTempDownloadDir, true);
                }
                catch (Exception ex) { Log.Warning(ex, "Cleanup failed: {Dir}", newTempDownloadDir); }

                if (!string.IsNullOrWhiteSpace(previousBackupArchivePath) && Directory.Exists(oldTempExtractDir))
                {
                    try
                    {
                        Directory.Delete(oldTempExtractDir, true);
                    }
                    catch (Exception ex) { Log.Warning(ex, "Cleanup failed: {Dir}", oldTempExtractDir); }
                }
            }
        }

        // Modified to accept List (already sorted)
        private async Task WriteManifestAsync(string dir, List<FileManifestEntry> entries)
        {
            var data = new
            {
                BackupToolVersion = "2.8-parallel", // Update version string
                BackupTimestampUtc = DateTime.UtcNow,
                Files = entries
            };
            string manifestPath = Path.Combine(dir, "_manifest.json");
            try
            {
                // Use WriteAllTextAsync for async file I/O
                // Serialize options can remain simple unless specific needs arise
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(manifestPath, json);
                Log.Information("Manifest written: {Path}", manifestPath);
            }
            catch (Exception ex) { Log.Error(ex, "Manifest write FAIL: {Path}", manifestPath); throw; } // Re-throw to indicate failure
        }

        // --- ListAllFilesAndFoldersAsync: No changes needed for parallelism itself ---
        private async Task<List<DriveItemInfo>> ListAllFilesAndFoldersAsync(string folderId, CancellationToken token)
        {
            var allItems = new List<DriveItemInfo>();
            var foldersToProcess = new Queue<(string Id, string RelativePath)>();
            foldersToProcess.Enqueue((folderId, "")); // Start with empty relative path

            var normalizedExclusions = _settings.ExcludedRelativePaths?.Select(p => NormalizeExclusionPath(p)).Where(p => p != "/").ToList() ?? new List<string>();
            if (normalizedExclusions.Any())
            {
                Log.Information("Applying {Count} exclusion rules: {Rules}", normalizedExclusions.Count, string.Join(", ", normalizedExclusions));
            }

            GFile? rootFolder = null;
            try
            {
                var req = _driveService.Files.Get(folderId);
                req.Fields = "id, name, mimeType, modifiedTime";
                req.SupportsAllDrives = true;
                rootFolder = await req.ExecuteAsync(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Google.GoogleApiException apiEx) when (apiEx.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log.Error("Root folder ID '{Id}' not found or inaccessible.", folderId);
                return allItems; // Return empty list if root folder is invalid
            }
            catch (Exception ex) { Log.Error(ex, "Failed getting root folder details: {Id}", folderId); return allItems; }

            string rootFolderName = rootFolder?.Name ?? "UnknownRoot";
            Log.Information("Starting listing under root folder: '{RootName}' ({Id})", rootFolderName, folderId);

            while (foldersToProcess.Any())
            {
                token.ThrowIfCancellationRequested();
                var (currId, currentRelativePath) = foldersToProcess.Dequeue();
                string displayPath = string.IsNullOrEmpty(currentRelativePath) ? "/" : currentRelativePath;
                Log.Verbose("Processing Drive folder: ID={Id}, RelativePath='{RelPath}'", currId, displayPath);

                if (IsExcluded(currentRelativePath, normalizedExclusions))
                {
                    Log.Information("Skipping excluded folder and contents: {Path}", displayPath);
                    continue; // Skip processing this folder and its children
                }

                string? pageToken = null;
                do
                {
                    token.ThrowIfCancellationRequested();
                    var req = _driveService.Files.List();
                    req.Q = $"'{currId}' in parents and trashed=false";
                    // Add size only for non-folders if needed, but manifest uses it later anyway. Get needed fields.
                    req.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
                    req.PageSize = 1000; // Max allowed page size
                    req.PageToken = pageToken;
                    req.SupportsAllDrives = true;
                    FileList? result = null;
                    try
                    {
                        result = await req.ExecuteAsync(token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Google.GoogleApiException apiEx) when (apiEx.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Log.Warning(apiEx, "Permission denied listing folder contents: {Id} ({RelPath}). Skipping this folder.", currId, displayPath);
                        break; // Stop processing this folder if we can't list it
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed listing folder contents: {Id} ({RelPath}). Skipping this folder.", currId, displayPath);
                        break; // Stop processing this folder on other errors
                    }

                    if (result?.Files != null)
                    {
                        foreach (GFile f in result.Files)
                        {
                            // Normalize the path relative to the backup root
                            string itemRelativePath = NormalizeExclusionPath(string.IsNullOrEmpty(currentRelativePath) ? $"/{f.Name}" : $"{currentRelativePath}/{f.Name}");

                            if (IsExcluded(itemRelativePath, normalizedExclusions))
                            {
                                Log.Information("Skipping excluded item: {Path}", itemRelativePath);
                                continue; // Skip this specific item
                            }

                            // Construct full path including root folder name for clarity in logs/manifest
                            string fullPath = $"/{rootFolderName}{itemRelativePath}";
                            bool isFolder = f.MimeType == "application/vnd.google-apps.folder";
                            Log.Verbose("Found item: Name='{Name}', ID={Id}, FullPath='{FullPath}', IsFolder={IsFolder}", f.Name, f.Id, fullPath, isFolder);

                            // Add item to the list
                            allItems.Add(new DriveItemInfo(
                                f.Id,
                                f.Name,
                                fullPath, // Store the constructed full path
                                isFolder,
                                (isFolder ? 0 : (f.Size ?? 0)), // Folders have size 0
                                f.MimeType,
                                f.ModifiedTimeDateTimeOffset));

                            if (isFolder)
                            {
                                // Add subfolder to the queue for processing, using its relative path
                                foldersToProcess.Enqueue((f.Id, itemRelativePath));
                            }
                        }
                    }
                    pageToken = result?.NextPageToken;
                } while (pageToken != null);
            }
            Log.Information("Listing complete. Found {Count} items after applying exclusions.", allItems.Count);
            return allItems;
        }

        // --- IsExcluded, NormalizeExclusionPath: No changes needed ---
        private bool IsExcluded(string relativeItemPath, List<string> normalizedExclusions)
        {
            if (string.IsNullOrEmpty(relativeItemPath) || relativeItemPath == "/")
                return false;
            foreach (var exclusion in normalizedExclusions)
            {
                if (relativeItemPath.Equals(exclusion, StringComparison.OrdinalIgnoreCase) ||
                    relativeItemPath.StartsWith(exclusion + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        private string NormalizeExclusionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "/";
            string norm = path.Replace('\\', '/').Trim();
            if (!norm.StartsWith("/"))
                norm = "/" + norm;
            if (norm.Length > 1 && norm.EndsWith("/"))
                norm = norm[..^1];
            return string.IsNullOrWhiteSpace(norm) ? "/" : norm;
        }

        // --- DownloadFileAsync: Logic remains the same, handles one file ---
        internal async Task<bool> DownloadFileAsync(DriveItemInfo fileInfo, string savePath, CancellationToken token)
        {
            const int maxAttempts = 3;
            const int delaySeconds = 7;
            string? exportMimeType = null;
            bool requiresExport = fileInfo.MimeType != null && GoogleToExportMimeType.TryGetValue(fileInfo.MimeType, out exportMimeType);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                string pfx = $"[DL Attempt {attempt}/{maxAttempts}]";
                int? taskId = Task.CurrentId;
                string taskInfo = taskId.HasValue ? $" Task {taskId.Value}" : "";
                Log.Debug("{Pfx}{TaskInfo}: Starting DL/Export: {Id} ('{Name}') -> {SavePath}", pfx, taskInfo, fileInfo.Id, fileInfo.Name, Path.GetFileName(savePath));
                try
                {
                    Google.Apis.Download.IDownloadProgress dlProgress;
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                    using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                    {
                        if (requiresExport)
                        {
                            Log.Information("{Pfx}{TaskInfo}: Exporting: {Name} ({Id}) as {Type}", pfx, taskInfo, fileInfo.Name, fileInfo.Id, exportMimeType);
                            var req = _driveService.Files.Export(fileInfo.Id, exportMimeType!);
                            dlProgress = await req.DownloadAsync(fs, token);
                        }
                        else if (fileInfo.MimeType?.StartsWith("application/vnd.google-apps") ?? false)
                        {
                            Log.Warning("{Pfx}{TaskInfo}: Skipping unsupported Google type: {Name} ({Id}) Mime: {Mime}", pfx, taskInfo, fileInfo.Name, fileInfo.Id, fileInfo.MimeType);
                            fs.Close();
                            try
                            {
                                System.IO.File.Delete(savePath);
                            }
                            catch { }
                            return false;
                        }
                        else
                        {
                            Log.Information("{Pfx}{TaskInfo}: Downloading: {Name} ({Id})", pfx, taskInfo, fileInfo.Name, fileInfo.Id);
                            var req = _driveService.Files.Get(fileInfo.Id);
                            req.SupportsAllDrives = true;
                            dlProgress = await req.DownloadAsync(fs, token);
                        }
                    }
                    if (dlProgress.Status == Google.Apis.Download.DownloadStatus.Completed)
                    {
                        Log.Information("{TaskInfo}: Success DL/Export: {Id} ('{Name}') on attempt {Attempt}", taskInfo, fileInfo.Id, fileInfo.Name, attempt);
                        return true;
                    }
                    else
                    {
                        Log.Warning("{Pfx}{TaskInfo}: DL/Export attempt failed: {Id} ('{Name}'), Status: {Status}, Err: {Err}", pfx, taskInfo, fileInfo.Id, fileInfo.Name, dlProgress.Status, dlProgress.Exception?.Message ?? "N/A");
                        if (attempt == maxAttempts)
                            break;
                    }
                }
                catch (OperationCanceledException) { Log.Warning("{TaskInfo}: DL/Export cancelled: {Id} ('{Name}')", taskInfo, fileInfo.Id, fileInfo.Name); throw; }
                catch (IOException ioEx) when (ioEx.Message.Contains("space")) { Log.Error(ioEx, "{Pfx}{TaskInfo}: Disk space error on DL/Export: {Id} ('{Name}'). Cannot retry.", pfx, taskInfo, fileInfo.Id, fileInfo.Name); return false; }
                catch (IOException ioEx) { Log.Warning(ioEx, "{Pfx}{TaskInfo}: IOException on DL/Export: {Id} ('{Name}')", pfx, taskInfo, fileInfo.Id, fileInfo.Name); if (attempt == maxAttempts) break; }
                catch (Google.GoogleApiException apiEx) { Log.Warning(apiEx, "{Pfx}{TaskInfo}: API Exception on DL/Export: {Id} ('{Name}'), Code: {Code}", pfx, taskInfo, fileInfo.Id, fileInfo.Name, apiEx.HttpStatusCode); bool retry = apiEx.HttpStatusCode == System.Net.HttpStatusCode.InternalServerError || apiEx.HttpStatusCode == System.Net.HttpStatusCode.BadGateway || apiEx.HttpStatusCode == System.Net.HttpStatusCode.ServiceUnavailable || apiEx.Error.Message.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase); if (!retry || attempt == maxAttempts) { Log.Error(apiEx, "{TaskInfo}: Unretryable API Exception or final attempt failed for {Id} ('{Name}')", taskInfo, fileInfo.Id, fileInfo.Name); return false; } }
                catch (Exception ex) { Log.Error(ex, "{Pfx}{TaskInfo}: Unexpected error on DL/Export: {Id} ('{Name}'). No retry.", pfx, taskInfo, fileInfo.Id, fileInfo.Name); return false; }
                try
                {
                    if (System.IO.File.Exists(savePath))
                        System.IO.File.Delete(savePath);
                }
                catch { } // Cleanup failed attempt
                if (attempt < maxAttempts)
                {
                    Log.Information("{Pfx}{TaskInfo}: Waiting {Sec}s before retry {Next} for {Id} ('{Name}')...", pfx, taskInfo, delaySeconds, attempt + 1, fileInfo.Id, fileInfo.Name);
                    try
                    {
                        await Task.Delay(delaySeconds * 1000, token);
                    }
                    catch (OperationCanceledException) { Log.Warning("{TaskInfo}: Delay cancelled for retry on {Id}.", taskInfo, fileInfo.Id); throw; }
                }
            }
            Log.Error("{TaskInfo}: DL/Export ultimately failed after {Max} attempts for {Id} ({Name})", Task.CurrentId.HasValue ? $"Task {Task.CurrentId.Value}" : "Task ?", maxAttempts, fileInfo.Id, fileInfo.Name);
            return false;
        }

        // --- AreDateTimesEquivalent: No changes needed ---
        private bool AreDateTimesEquivalent(DateTimeOffset d1, DateTimeOffset d2)
        {
            var u1 = d1.UtcDateTime;
            var u2 = d2.UtcDateTime;
            return Math.Abs((u1 - u2).TotalSeconds) < 1.0;
        }

        // --- ExtractFileIdFromArchivePath: No changes needed ---
        private string? ExtractFileIdFromArchivePath(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }
}