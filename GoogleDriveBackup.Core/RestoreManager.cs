using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using Serilog;
using System;
using System.Collections.Concurrent; // Added for ConcurrentBag and ConcurrentDictionary
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading; // Added for Interlocked and SemaphoreSlim
using System.Threading.Tasks; // Added for Task related methods
using GFile = Google.Apis.Drive.v3.Data.File; // Alias to avoid confusion with System.IO.File

namespace GoogleDriveBackup.Core
{
    // --- RestoreState definition remains the same ---
    public class RestoreState
    {
        public AppSettings SettingsUsed { get; set; } = new AppSettings();
        public List<string> CompletedArchivePaths { get; set; } = new List<string>();
        public DateTimeOffset RestoreInitiatedTimestampUtc { get; set; } = DateTimeOffset.MinValue;
        public string OriginalBackupArchivePath { get; set; } = string.Empty;
    }

    public class RestoreManager
    {
        private readonly DriveService _driveService;
        private AppSettings _currentAppSettings;
        // Use ConcurrentDictionary for thread-safe folder cache access during parallel uploads
        private ConcurrentDictionary<string, string> _folderPathToIdCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Use IgnoreCase for paths
        private const string MANIFEST_FILENAME = "_manifest.json";
        private const string RESTORE_STATE_FILENAME = "_restore_state.json";
        // Lock object for critical sections like folder creation
        private readonly object _folderLock = new object();


        // Re-use the public definition from RepairManager for consistency
        // Assuming RepairManager.FileManifestEntry is accessible
        private class DiscManifest
        {
            public string? BackupToolVersion
            {
                get; set;
            }
            public List<RepairManager.FileManifestEntry> Files { get; set; } = new List<RepairManager.FileManifestEntry>();
        }

        public RestoreManager(DriveService driveService, AppSettings settings)
        {
            _driveService = driveService;
            _currentAppSettings = settings;
        }


        public async Task<RestoreResult> StartRestoreAsync(
            string? backupArchivePath, // Nullable if resuming
            string? resumeFolderPath, // Nullable if starting fresh
            IProgress<BackupProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // Use ConcurrentDictionary initialized here for thread safety during parallel operations
            _folderPathToIdCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            bool isResume = !string.IsNullOrWhiteSpace(resumeFolderPath);
            string tempExtractDir = string.Empty;
            string sourceDescription = isResume ? $"resume folder: {resumeFolderPath}" : $"archive: {backupArchivePath}";
            // Use Interlocked counters for thread safety
            long manifestEntriesCount = 0, filesProcessed = 0, filesUploaded = 0, filesSkipped = 0, filesAlreadyDone = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();
            RestoreState currentRestoreState = null!;
            DiscManifest? manifest = null;
            AppSettings settingsForThisRestore;
            // Use ConcurrentBag for thread-safe tracking of completed files during parallel execution
            ConcurrentBag<string> completedInThisRun = new ConcurrentBag<string>();


            try
            {
                Log.Information("Starting restore from {SourceDescription}", sourceDescription);
                progress?.Report(new BackupProgressReport($"Starting Restore ({(isResume ? "Resume" : "Fresh")})", 0, 1, isResume ? resumeFolderPath : backupArchivePath));

                // --- Setup (Fresh or Resume) ---
                if (isResume)
                {
                    // --- RESUME PATH ---
                    tempExtractDir = Path.GetFullPath(resumeFolderPath!);
                    if (!Directory.Exists(tempExtractDir))
                        throw new DirectoryNotFoundException($"Resume folder not found: {tempExtractDir}");
                    currentRestoreState = await LoadRestoreStateAsync(tempExtractDir, cancellationToken) ?? throw new InvalidOperationException($"Could not load restore state file ({RESTORE_STATE_FILENAME}) from the specified folder.");
                    settingsForThisRestore = currentRestoreState.SettingsUsed;
                    manifest = await LoadManifestFromDirectoryAsync(tempExtractDir) ?? throw new InvalidOperationException($"Failed loading manifest ({MANIFEST_FILENAME}) from resume folder.");
                    Log.Information("Resuming restore. State loaded. Completed Files in State: {Count}", currentRestoreState.CompletedArchivePaths.Count);
                    // Optional: Log setting differences
                    if (settingsForThisRestore.GoogleDriveRestoreParentId != _currentAppSettings.GoogleDriveRestoreParentId)
                        Log.Warning("Current global Restore Parent ID ('{C}') differs from state file ('{S}'). Using state file ID: {S}", _currentAppSettings.GoogleDriveRestoreParentId, settingsForThisRestore.GoogleDriveRestoreParentId, settingsForThisRestore.GoogleDriveRestoreParentId);
                    if (settingsForThisRestore.GetEffectiveMaxParallelTasks() != _currentAppSettings.GetEffectiveMaxParallelTasks())
                        Log.Warning("Current global MaxParallelTasks ({C}) differs from state file ({S}). Using state file value: {S}", _currentAppSettings.GetEffectiveMaxParallelTasks(), settingsForThisRestore.GetEffectiveMaxParallelTasks(), settingsForThisRestore.GetEffectiveMaxParallelTasks());
                }
                else
                {
                    // --- FRESH START PATH ---
                    if (string.IsNullOrWhiteSpace(backupArchivePath) || !File.Exists(backupArchivePath))
                        throw new FileNotFoundException("Backup archive path is required for a fresh restore.", backupArchivePath ?? "null");
                    settingsForThisRestore = _currentAppSettings;
                    string tempWorkPath = Path.GetFullPath(settingsForThisRestore.LocalTempWorkPath ?? AppSettings.DefaultTempPath);
                    Directory.CreateDirectory(tempWorkPath); // Ensure base temp path exists
                    tempExtractDir = Path.Combine(tempWorkPath, $"restore_extract_{Path.GetFileNameWithoutExtension(backupArchivePath)}_{Guid.NewGuid().ToString().Substring(0, 8)}");
                    Log.Information("Preparing fresh restore. Temp folder: {TempExtractDir}", tempExtractDir);
                    Console.WriteLine($"\nINFO: Using temporary directory: {tempExtractDir}\n      If restore is interrupted, use this path to resume.");
                    Directory.CreateDirectory(tempExtractDir);
                    Log.Information("Extracting archive...");
                    progress?.Report(new BackupProgressReport("Extracting Archive", 0, 1));
                    await Task.Run(() => ZipFile.ExtractToDirectory(backupArchivePath, tempExtractDir), cancellationToken);
                    Log.Information("Extraction complete.");
                    progress?.Report(new BackupProgressReport("Extracting Archive", 1, 1));
                    manifest = await LoadManifestFromDirectoryAsync(tempExtractDir) ?? throw new InvalidOperationException("Failed loading manifest after extraction.");
                    currentRestoreState = new RestoreState
                    {
                        SettingsUsed = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settingsForThisRestore)) ?? settingsForThisRestore, // Deep copy
                        CompletedArchivePaths = new List<string>(),
                        RestoreInitiatedTimestampUtc = DateTimeOffset.UtcNow,
                        OriginalBackupArchivePath = backupArchivePath
                    };
                    bool stateSaved = await SaveRestoreStateAsync(tempExtractDir, currentRestoreState, cancellationToken);
                    if (!stateSaved)
                        Log.Warning("Failed to save initial restore state to {TempExtractDir}. Resume might not work correctly if interrupted.", tempExtractDir);
                    else
                        Log.Information("Initial restore state saved.");
                }
                // --- End Setup Logic ---


                // --- COMMON RESTORE LOGIC ---
                string targetParentId = settingsForThisRestore.GoogleDriveRestoreParentId ?? AppSettings.DefaultRestoreParent;
                // Prime cache with root ID - ConcurrentDictionary handles thread safety
                _folderPathToIdCache.TryAdd("", targetParentId);

                manifestEntriesCount = manifest.Files.Count;
                Log.Information("Manifest loaded: {Count} entries.", manifestEntriesCount);
                progress?.Report(new BackupProgressReport("Manifest Loaded", 1, 1, $"Found {manifestEntriesCount} files"));

                // --- Stage 1: Ensure Parent Folders Exist (Sequential for safety/simplicity) ---
                Log.Information("Pre-processing folder structure...");
                progress?.Report(new BackupProgressReport("Creating Folders", 0, 1));
                var uniqueDirPaths = manifest.Files
                    .Select(f => Path.GetDirectoryName(f.GoogleDrivePath)?.Replace('\\', '/').Trim('/')) // Get relative dir path
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p.Length) // Process shorter paths first (e.g., /A before /A/B)
                    .ToList();

                int foldersProcessed = 0;
                int totalFolders = uniqueDirPaths.Count;
                Log.Information("Found {Count} unique directory paths to ensure exist.", totalFolders);
                progress?.Report(new BackupProgressReport("Creating Folders", 0, totalFolders));
                foreach (var relativeDirPath in uniqueDirPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgressReport("Creating Folders", foldersProcessed + 1, totalFolders, "/" + relativeDirPath)); // Report progress
                    string? folderId = await GetOrCreateParentFolderIdAsync(relativeDirPath!, targetParentId, cancellationToken); // Pass relative dir path
                    if (folderId == null)
                    {
                        Log.Error("Failed to ensure existence of folder path '{Path}'. Subsequent uploads within this path may fail.", relativeDirPath);
                        // Optionally, could add these files to a 'skipped' list immediately, but let's try uploading anyway for now.
                    }
                    else
                    {
                        Log.Verbose("Ensured folder path '{Path}' exists with ID {Id}", relativeDirPath, folderId);
                    }
                    foldersProcessed++;
                }
                Log.Information("Folder structure pre-processing complete.");
                progress?.Report(new BackupProgressReport("Creating Folders", totalFolders, totalFolders));


                // --- Stage 2: Upload Files (Parallel) ---
                int maxParallel = settingsForThisRestore.GetEffectiveMaxParallelTasks();
                Log.Information("Processing {Count} files for upload using up to {MaxTasks} parallel tasks (Target Parent: {TargetId})...",
                               manifestEntriesCount, maxParallel, targetParentId);
                progress?.Report(new BackupProgressReport("Uploading Files", 0, (int)manifestEntriesCount));

                using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                var uploadTasks = new List<Task>((int)manifestEntriesCount);
                // Use a hash set for faster checking of already completed files from the state
                var alreadyCompletedSet = new HashSet<string>(currentRestoreState.CompletedArchivePaths, StringComparer.OrdinalIgnoreCase);

                // Reset processed count for this stage
                filesProcessed = 0;

                foreach (var entry in manifest.Files) // Order doesn't strictly matter now for uploads
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long currentProcessed = Interlocked.Increment(ref filesProcessed); // Track how many loop iterations we've started

                    // --- RESUME CHECK ---
                    if (alreadyCompletedSet.Contains(entry.ArchivePath))
                    {
                        Log.Verbose("Skipping (already completed in state): Drive='{DrivePath}', Archive='{ArchiveFile}'", entry.GoogleDrivePath, entry.ArchivePath);
                        Interlocked.Increment(ref filesAlreadyDone);
                        // Report progress even for skipped items
                        progress?.Report(new BackupProgressReport("Skipping Completed", (int)currentProcessed, (int)manifestEntriesCount, entry.GoogleDrivePath));
                        continue; // Skip this file
                    }

                    // --- Wait for semaphore before starting task ---
                    await semaphore.WaitAsync(cancellationToken);

                    uploadTasks.Add(Task.Run(async () => // Use Task.Run to ensure it runs on thread pool
                    {
                        int? taskId = Task.CurrentId;
                        string taskInfo = taskId.HasValue ? $"Task {taskId.Value}" : "Task ?";
                        bool fileUploadedSuccessfully = false;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            progress?.Report(new BackupProgressReport("Uploading Files", (int)currentProcessed, (int)manifestEntriesCount, entry.GoogleDrivePath, entry.ArchivePath));
                            Log.Information("{TaskInfo}: Preparing: Drive='{DrivePath}', Archive='{ArchiveFile}'", taskInfo, entry.GoogleDrivePath, entry.ArchivePath);

                            string localPath = Path.Combine(tempExtractDir, entry.ArchivePath);
                            if (!File.Exists(localPath))
                            {
                                Log.Warning("{TaskInfo}: MISSING local file (from archive): {Local}. Skipping.", taskInfo, localPath);
                                Interlocked.Increment(ref filesSkipped);
                                return; // Exit this task
                            }

                            // --- Get Parent Folder ID (should be cached now) ---
                            string? relativeDirPath = Path.GetDirectoryName(entry.GoogleDrivePath)?.Replace('\\', '/').Trim('/');
                            string parentFolderDriveId;
                            if (string.IsNullOrEmpty(relativeDirPath))
                            {
                                parentFolderDriveId = targetParentId; // It's in the root
                            }
                            else if (!_folderPathToIdCache.TryGetValue(relativeDirPath, out parentFolderDriveId!))
                            {
                                // This *shouldn't* happen after pre-processing, but log error if it does.
                                Log.Error("{TaskInfo}: Could not find pre-processed folder ID for path '{Path}'. Upload for '{File}' will likely fail.", taskInfo, relativeDirPath, entry.ArchivePath);
                                // Attempting to create here might cause race conditions despite lock if called concurrently for same new path.
                                // Safer to rely on pre-processing. Mark as skipped.
                                Interlocked.Increment(ref filesSkipped);
                                return; // Exit task
                            }

                            string targetFileName = Path.GetFileName(entry.GoogleDrivePath);
                            if (string.IsNullOrEmpty(targetFileName))
                            {
                                targetFileName = Path.GetFileName(entry.ArchivePath);
                            } // Fallback
                            if (string.IsNullOrEmpty(targetFileName))
                            {
                                Log.Error("{TaskInfo}: Cannot determine target filename for archive entry '{Archive}'. Skipping.", taskInfo, entry.ArchivePath);
                                Interlocked.Increment(ref filesSkipped);
                                return; // Exit this task
                            }

                            Log.Information("{TaskInfo}: Uploading: Local='{Local}' -> Drive='{Target}' in Parent='{Parent}' ({ParentId})", taskInfo, Path.GetFileName(localPath), targetFileName, relativeDirPath ?? "/", parentFolderDriveId);
                            bool uploadSuccess = await UploadFileAsync(localPath, targetFileName, parentFolderDriveId, cancellationToken);

                            if (uploadSuccess)
                            {
                                Interlocked.Increment(ref filesUploaded);
                                // Add to thread-safe collection for final state update
                                completedInThisRun.Add(entry.ArchivePath);
                                fileUploadedSuccessfully = true;
                                Log.Information("{TaskInfo}: Upload Success for {ArchiveFile}", taskInfo, entry.ArchivePath);
                            }
                            else
                            {
                                Interlocked.Increment(ref filesSkipped);
                                Log.Warning("{TaskInfo}: Upload failed for: {ArchiveFile}", taskInfo, entry.ArchivePath);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            Log.Warning("{TaskInfo}: Upload task cancelled for {File}", taskInfo, entry.ArchivePath);
                            Interlocked.Increment(ref filesSkipped); // Count cancellation as a skip/failure for this run
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "{TaskInfo}: Unexpected error uploading file {File}", taskInfo, entry.ArchivePath);
                            Interlocked.Increment(ref filesSkipped);
                        }
                        finally
                        {
                            semaphore.Release(); // Release semaphore slot
                        }
                    }, cancellationToken));
                } // End foreach loop creating tasks

                // Wait for all upload tasks to complete
                await Task.WhenAll(uploadTasks);
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation during WhenAll

                stopwatch.Stop();
                Log.Information("File upload processing complete in {Elapsed:F2}s.", stopwatch.Elapsed.TotalSeconds);
                progress?.Report(new BackupProgressReport("Processing Complete", (int)manifestEntriesCount, (int)manifestEntriesCount));

                // --- Final State Update ---
                if (!completedInThisRun.IsEmpty)
                {
                    Log.Information("Updating restore state with {Count} newly completed files...", completedInThisRun.Count);
                    // Add completed files from this run to the main state list
                    lock (currentRestoreState) // Lock the state object briefly for adding
                    {
                        currentRestoreState.CompletedArchivePaths.AddRange(completedInThisRun);
                        // Ensure uniqueness just in case (though ConcurrentBag shouldn't add duplicates if logic is right)
                        currentRestoreState.CompletedArchivePaths = currentRestoreState.CompletedArchivePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    // Save final state using CancellationToken.None as main operation is complete now
                    bool finalSaveOk = await SaveRestoreStateAsync(tempExtractDir, currentRestoreState, CancellationToken.None);
                    if (!finalSaveOk)
                    {
                        Log.Error("Failed to save final restore state after completion!");
                        // This is problematic, but the upload itself might have finished.
                    }
                }

                // Determine overall success based on final counts
                bool overallSuccess = filesSkipped == 0 && (filesUploaded + filesAlreadyDone) == manifestEntriesCount;

                if (overallSuccess)
                {
                    Log.Information("Restore completed successfully. Uploaded This Run={Up}, Already Done={Done}, Skipped/Failed This Run={Skip}", filesUploaded, filesAlreadyDone, filesSkipped);
                    // --- CLEANUP ON SUCCESS ---
                    Log.Information("Cleaning up temporary directory: {TempExtractDir}", tempExtractDir);
                    try
                    {
                        Directory.Delete(tempExtractDir, true);
                    }
                    catch (Exception ex) { Log.Warning(ex, "Failed to clean up temp directory: {Dir}", tempExtractDir); }
                }
                else
                {
                    Log.Warning("Restore finished with issues. Uploaded This Run={Up}, Already Done={Done}, Skipped/Failed This Run={Skip}. Temporary folder kept for potential resume: {TempExtractDir}", filesUploaded, filesAlreadyDone, filesSkipped, tempExtractDir);
                    Console.WriteLine($"\nWARNING: Restore finished with {filesSkipped} skipped/failed files this run.");
                    Console.WriteLine($"         Temporary folder kept for potential resume: {tempExtractDir}");
                }

                return new RestoreResult
                {
                    Success = overallSuccess,
                    Cancelled = false,
                    Duration = stopwatch.Elapsed,
                    FilesProcessed = (int)manifestEntriesCount, // Total entries in manifest
                    FilesUploaded = (int)filesUploaded,       // Files uploaded *this run*
                    FilesSkippedOrFailed = (int)filesSkipped  // Files skipped/failed *this run*
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                Log.Warning("Restore cancelled by user. Temporary folder kept for potential resume: {TempExtractDir}", tempExtractDir ?? "Unknown (Error before init)");
                if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
                {
                    Console.WriteLine($"\nOperation Cancelled. Temporary folder kept for potential resume: {tempExtractDir}");
                    // Save potentially partially updated state on cancel? Important for resume.
                    if (currentRestoreState != null && !completedInThisRun.IsEmpty)
                    {
                        Log.Information("Attempting to save partial restore state on cancellation...");
                        lock (currentRestoreState)
                        {
                            currentRestoreState.CompletedArchivePaths.AddRange(completedInThisRun);
                            currentRestoreState.CompletedArchivePaths = currentRestoreState.CompletedArchivePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        }
                        await SaveRestoreStateAsync(tempExtractDir, currentRestoreState, CancellationToken.None); // Use None here
                    }
                }
                else
                {
                    Console.WriteLine($"\nOperation Cancelled.");
                }

                // Estimate remaining based on how many iterations started vs total expected
                long remainingOrSkipped = manifestEntriesCount - filesAlreadyDone - filesUploaded;
                return new RestoreResult { Cancelled = true, Duration = stopwatch.Elapsed, FilesProcessed = (int)manifestEntriesCount, FilesUploaded = (int)filesUploaded, FilesSkippedOrFailed = (int)remainingOrSkipped };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log.Error(ex, "CRITICAL ERROR during restore from {SourceDescription}", sourceDescription);
                if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
                {
                    Log.Warning("Temporary folder kept due to error: {TempExtractDir}", tempExtractDir);
                    Console.WriteLine($"\nERROR during restore. Temporary folder kept for potential resume: {tempExtractDir}");
                    // Save potentially partially updated state on error? Also important.
                    if (currentRestoreState != null && !completedInThisRun.IsEmpty)
                    {
                        Log.Information("Attempting to save partial restore state on error...");
                        lock (currentRestoreState)
                        {
                            currentRestoreState.CompletedArchivePaths.AddRange(completedInThisRun);
                            currentRestoreState.CompletedArchivePaths = currentRestoreState.CompletedArchivePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        }
                        await SaveRestoreStateAsync(tempExtractDir, currentRestoreState, CancellationToken.None); // Use None here
                    }
                }
                else
                {
                    Console.WriteLine($"\nERROR during restore. Check logs. Temp folder path: {tempExtractDir ?? "Unknown"}");
                }

                long remainingOrSkipped = manifestEntriesCount - filesAlreadyDone - filesUploaded;
                return new RestoreResult { Success = false, Cancelled = false, Duration = stopwatch.Elapsed, FilesProcessed = (int)manifestEntriesCount, FilesUploaded = (int)filesUploaded, FilesSkippedOrFailed = (int)remainingOrSkipped };
            }
        }

        // --- Load/Save State and Manifest Helpers remain the same ---
        private async Task<RestoreState?> LoadRestoreStateAsync(string tempExtractDir, CancellationToken cancellationToken)
        {
            string stateFilePath = Path.Combine(tempExtractDir, RESTORE_STATE_FILENAME);
            if (!File.Exists(stateFilePath))
            {
                Log.Error("Restore state file not found: {StateFilePath}", stateFilePath);
                return null;
            }
            try
            {
                Log.Information("Loading restore state from: {StateFilePath}", stateFilePath);
                string json = await File.ReadAllTextAsync(stateFilePath, cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var state = JsonSerializer.Deserialize<RestoreState>(json, options);
                if (state == null || state.SettingsUsed == null)
                {
                    Log.Error("Failed to deserialize restore state or settings are missing in {StateFilePath}", stateFilePath);
                    return null;
                }
                state.CompletedArchivePaths ??= new List<string>();
                // Apply defaults for robustness against older state files
                state.SettingsUsed.LocalBackupArchivePath ??= AppSettings.DefaultArchivePath;
                state.SettingsUsed.LocalTempWorkPath ??= AppSettings.DefaultTempPath;
                state.SettingsUsed.GoogleDriveRestoreParentId ??= AppSettings.DefaultRestoreParent;
                state.SettingsUsed.BackupCycleHours ??= AppSettings.DefaultBackupCycle;
                state.SettingsUsed.ShowVerboseProgress ??= AppSettings.DefaultVerboseProgress;
                state.SettingsUsed.ExcludedRelativePaths ??= new List<string>();
                state.SettingsUsed.MaxParallelTasks ??= AppSettings.DefaultMaxParallelTasks;
                // Clamp parallel tasks value loaded from state
                state.SettingsUsed.MaxParallelTasks = Math.Clamp(state.SettingsUsed.MaxParallelTasks.Value, 1, AppSettings.MaxAllowedParallelTasks);
                return state;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(ex, "Failed to read or parse restore state file: {StateFilePath}", stateFilePath); return null; }
        }
        private async Task<bool> SaveRestoreStateAsync(string tempExtractDir, RestoreState state, CancellationToken cancellationToken)
        {
            string stateFilePath = Path.Combine(tempExtractDir, RESTORE_STATE_FILENAME);
            try
            {
                Log.Debug("Saving restore state to: {StateFilePath} ({Count} completed)", stateFilePath, state.CompletedArchivePaths.Count);
                // Sort list before saving for consistency
                lock (state)
                { // Lock state briefly for sorting
                    state.CompletedArchivePaths.Sort(StringComparer.OrdinalIgnoreCase);
                }
                // Ensure MaxParallelTasks is clamped before saving
                state.SettingsUsed.MaxParallelTasks = Math.Clamp(state.SettingsUsed.GetEffectiveMaxParallelTasks(), 1, AppSettings.MaxAllowedParallelTasks);
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(stateFilePath, json, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) { Log.Warning("Saving restore state cancelled."); return false; }
            catch (Exception ex) { Log.Error(ex, "Failed to save restore state file: {StateFilePath}", stateFilePath); return false; }
        }
        private async Task<DiscManifest?> LoadManifestFromDirectoryAsync(string directoryPath)
        {
            string manifestPath = Path.Combine(directoryPath, MANIFEST_FILENAME);
            if (!File.Exists(manifestPath))
            {
                Log.Error("Manifest not found: {Path}", manifestPath);
                return null;
            }
            Log.Information("Reading manifest: {Path}", manifestPath);
            try
            {
                string json = await File.ReadAllTextAsync(manifestPath);
                var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var m = JsonSerializer.Deserialize<DiscManifest>(json, opt);
                if (m?.Files == null)
                {
                    Log.Error("Failed parsing manifest (or Files list is null): {Path}", manifestPath);
                    return null;
                }
                return m;
            }
            catch (Exception ex) { Log.Error(ex, "Error reading/parsing manifest: {Path}", manifestPath); return null; }
        }


        // Modified GetOrCreateParentFolderIdAsync to work with ConcurrentDictionary cache and lock for creation
        private async Task<string?> GetOrCreateParentFolderIdAsync(string relativeDirPath, string rootId, CancellationToken token)
        {
            // Ensure input path is normalized (relative, no leading/trailing slashes needed internally here)
            relativeDirPath = relativeDirPath?.Replace('\\', '/').Trim('/') ?? string.Empty;

            if (string.IsNullOrEmpty(relativeDirPath))
                return _folderPathToIdCache.TryGetValue("", out var rId) ? rId : rootId; // Return root if path is empty

            // Check cache first (thread-safe)
            if (_folderPathToIdCache.TryGetValue(relativeDirPath, out string? cachedId))
                return cachedId;

            // If not in cache, lock to ensure only one thread creates the structure for this path segment
            lock (_folderLock)
            {
                // Double-check cache inside lock to prevent race condition
                if (_folderPathToIdCache.TryGetValue(relativeDirPath, out cachedId))
                    return cachedId;

                // --- Perform Find/Create Logic Synchronously Inside Lock ---
                // This simplifies logic compared to complex async/await inside lock,
                // suitable if folder creation isn't the primary bottleneck.
                string[] parts = relativeDirPath.Split('/');
                string currentParentId = _folderPathToIdCache.TryGetValue("", out var rtId) ? rtId : rootId; // Start from root
                string builtPath = "";

                Log.Debug("Ensuring folder path exists in Drive (Locked): {RelativePath}", relativeDirPath);
                foreach (var part in parts)
                {
                    // Check cancellation within the loop, even though waits are synchronous
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(part))
                        continue;

                    builtPath = string.IsNullOrEmpty(builtPath) ? part : $"{builtPath}/{part}";

                    // Check cache again for the intermediate part *inside the lock*
                    if (_folderPathToIdCache.TryGetValue(builtPath, out string? existingId))
                    {
                        currentParentId = existingId;
                        continue;
                    }

                    // --- Find or Create Logic ---
                    string? foundId = FindFolderIdAsync(part, currentParentId, token).GetAwaiter().GetResult(); // **Synchronous wait inside lock**

                    if (string.IsNullOrEmpty(foundId))
                    {
                        Log.Information("(Locked) Creating folder: '{FolderName}' in parent {ParentId}", part, currentParentId);
                        var folderMetadata = new GFile { Name = part, MimeType = "application/vnd.google-apps.folder", Parents = new List<string> { currentParentId } };
                        var createRequest = _driveService.Files.Create(folderMetadata);
                        createRequest.Fields = "id";
                        createRequest.SupportsAllDrives = true;
                        try
                        {
                            var createdFolder = createRequest.ExecuteAsync(token).GetAwaiter().GetResult(); // **Synchronous wait inside lock**
                            foundId = createdFolder.Id;
                            Log.Information("(Locked) Created folder '{FolderName}' with ID: {FolderId}", part, foundId);
                        }
                        catch (OperationCanceledException) { throw; } // Let cancellation bubble up
                        catch (Exception ex) { Log.Error(ex, "(Locked) Failed to create folder '{FolderName}' in parent {ParentId}", part, currentParentId); return null; } // Return null on creation failure
                    }
                    else
                    {
                        Log.Debug("(Locked) Found existing folder '{FolderName}' with ID: {FolderId}", part, foundId);
                    }
                    // --- End Find or Create ---

                    // Cache the found/created ID and update current parent
                    if (!string.IsNullOrEmpty(foundId))
                    {
                        _folderPathToIdCache.TryAdd(builtPath, foundId); // Use TryAdd for ConcurrentDictionary
                        currentParentId = foundId;
                    }
                    else
                    {
                        Log.Error("(Locked) Could not determine ID for folder path segment '{BuiltPath}'. Aborting path creation.", builtPath);
                        return null;
                    }
                } // end foreach part

                // After loop completes, currentParentId holds the ID of the final directory
                _folderPathToIdCache.TryAdd(relativeDirPath, currentParentId); // Ensure final path is cached
                return currentParentId;
            } // End lock
        }


        // FindFolderIdAsync remains largely the same (async)
        private async Task<string?> FindFolderIdAsync(string name, string parentId, CancellationToken token)
        {
            string escapedName = name.Replace("'", "\\'");
            string query = $"mimeType='application/vnd.google-apps.folder' and name='{escapedName}' and '{parentId}' in parents and trashed=false";
            var request = _driveService.Files.List();
            request.Q = query;
            request.Fields = "files(id)";
            request.PageSize = 1;
            request.SupportsAllDrives = true;
            try
            {
                var result = await request.ExecuteAsync(token);
                return result.Files?.FirstOrDefault()?.Id;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(ex, "Error finding folder '{FolderName}' in parent {ParentId}", name, parentId); return null; }
        }

        // UploadFileAsync remains the same (handles one file)
        private async Task<bool> UploadFileAsync(string localPath, string driveName, string parentId, CancellationToken token)
        {
            int? taskId = Task.CurrentId;
            string taskInfo = taskId.HasValue ? $"Task {taskId.Value}" : "Task ?";
            var fileMetadata = new GFile { Name = driveName, Parents = new List<string> { parentId } };
            Log.Debug("{TaskInfo}: Upload preparing: Local='{LocalPath}', Drive='{DriveName}', Parent='{ParentId}'", taskInfo, localPath, driveName, parentId);
            bool uploadReportedSuccess = false;
            try
            {
                if (!File.Exists(localPath))
                {
                    Log.Error("{TaskInfo}: Upload source file missing: {LocalPath}", taskInfo, localPath);
                    return false;
                }
                long fileSize = 0;
                try
                {
                    fileSize = new FileInfo(localPath).Length;
                }
                catch { Log.Warning("{TaskInfo}: Could not get file size for {LocalPath}", taskInfo, localPath); }
                Log.Information("{TaskInfo}: Starting upload: {DriveName} ({SizeInfo})", taskInfo, driveName, FormatBytesStatic(fileSize));
                await using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read))
                {
                    var request = _driveService.Files.Create(fileMetadata, stream, GetMimeType(localPath));
                    request.Fields = "id,name";
                    request.SupportsAllDrives = true;
                    // Omit progress handler to reduce log noise in parallel execution
                    // request.ProgressChanged += (IUploadProgress p) => LogUploadProgress(driveName, p.Status, p.BytesSent, fileSize, p.Exception);
                    request.ResponseReceived += (GFile f) => { Log.Information("{TaskInfo}: Upload confirmed OK: Drive='{Name}', ID={Id}", taskInfo, f.Name, f.Id); uploadReportedSuccess = true; };
                    var uploadResult = await request.UploadAsync(token);
                    if (uploadResult.Status == UploadStatus.Failed)
                    {
                        Log.Error(uploadResult.Exception, "{TaskInfo}: Upload final status FAILED: {DriveName}", taskInfo, driveName);
                        return false;
                    }
                    else if (uploadResult.Status == UploadStatus.Completed)
                    {
                        if (!uploadReportedSuccess)
                        {
                            Log.Warning("{TaskInfo}: Upload status COMPLETED for '{DriveName}', but success confirmation event may not have fired. Assuming success.", taskInfo, driveName);
                        }
                        else
                        {
                            Log.Debug("{TaskInfo}: Upload final status COMPLETED and confirmed OK: {DriveName}", taskInfo, driveName);
                        }
                        return true;
                    }
                    else
                    {
                        Log.Warning("{TaskInfo}: Upload finished with unexpected status {Status} for {DriveName}", taskInfo, uploadResult.Status, driveName);
                        return false;
                    }
                }
            }
            catch (OperationCanceledException) { Log.Warning("{TaskInfo}: Upload cancelled: {DriveName}", taskInfo, driveName); throw; }
            catch (Exception ex) { Log.Error(ex, "{TaskInfo}: Critical error during upload attempt for: {DriveName}", taskInfo, driveName); return false; }
        }

        // GetMimeType remains the same
        private string GetMimeType(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return ext switch
            {
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".zip" => "application/zip",
                ".gz" => "application/gzip",
                ".tar" => "application/x-tar",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".html" or ".htm" => "text/html",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                _ => "application/octet-stream"
            };
        }

        // FormatBytesStatic remains the same
        private static string FormatBytesStatic(long bytes)
        {
            if (bytes < 0)
                return "N/A";
            if (bytes == 0)
                return "0 B";
            string[] suffix = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < suffix.Length - 1 && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            string format = (i > 0) ? "{0:0.#} {1}" : "{0:0} {1}";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, dblSByte, suffix[i]);
        }
    }
}