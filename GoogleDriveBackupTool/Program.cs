using Google.Apis.Drive.v3;
using GoogleDriveBackup.Core; // Use the Core library namespace
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.Text.Json;

namespace GoogleDriveZipBackupTool
{
    class Program
    {
        private static BackupStatus? _backupStatus;
        private static AppSettings _appSettings = null!; // Initialize in relevant method
        private const string STATUS_FILENAME = "backup_status.json"; // Keep local for status file path
        private static CancellationTokenSource? _cts; // For cancellation
        private const string SETTINGS_PROFILE_EXTENSION = ".settings.json"; // Define extension for profiles

        static async Task<int> Main(string[] args)
        {
            // --- Serilog Configuration ---
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Google", LogEventLevel.Warning) // Reduce Google API library noise
                .Enrich.FromLogContext()
                .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information) // Log INFO+ to console
                .WriteTo.File(path: Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt"),
                              rollingInterval: RollingInterval.Day,
                              restrictedToMinimumLevel: LogEventLevel.Debug, // Log DEBUG+ to file for more detail
                              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            try
            {
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));
            }
            catch (Exception ex) { Console.WriteLine($"FATAL: Could not create logs directory: {ex.Message}"); return 1; }

            // --- Trap Console Cancellation (Ctrl+C) ---
            _cts = new CancellationTokenSource(); // Create CTS early for both modes
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                Log.Warning("Cancellation requested via Ctrl+C.");
                Console.WriteLine("\nCtrl+C detected. Requesting cancellation (may take a moment)...");
                try
                {
                    _cts?.Cancel();
                }
                catch (ObjectDisposedException) { /* Ignore if already disposed */ }
                eventArgs.Cancel = true; // Prevent the process from terminating immediately
            };

            // --- Entry Point ---
            int exitCode = 0;
            try
            {
                if (args.Length > 0)
                {
                    // --- Command Line Mode ---
                    Log.Information("================ Application Starting (Command Line Mode) ================");
                    Log.Information("Arguments: {Args}", string.Join(" ", args));
                    exitCode = await HandleCommandLineAsync(args);
                    Log.Information("================ Application Exiting (Command Line Mode) - Exit Code: {Code} ================", exitCode);
                }
                else
                {
                    // --- Interactive Mode ---
                    Log.Information("================ Application Starting (Interactive Mode) ================");
                    await RunAppLogicAsync(); // Call the main interactive loop
                    Log.Information("================ Application Exiting Normally (Interactive Mode) ================");
                }
            }
            catch (OperationCanceledException) // Catch cancellation specifically (includes Ctrl+C)
            {
                Log.Warning("Operation cancelled by user request.");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nOperation Cancelled.");
                Console.ResetColor();
                exitCode = 2; // Specific exit code for cancellation
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "A fatal error occurred during execution.");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nA FATAL error occurred: {ex.Message}");
                Console.WriteLine("Please check the log file in the 'logs' directory for details.");
                Console.ResetColor();
                exitCode = 1; // Indicate error exit
            }
            finally
            {
                _cts?.Dispose(); // Dispose the cancellation token source
                await Log.CloseAndFlushAsync(); // Ensure all logs are written
                // Keep console window open only if interactive and not debugging
                if (args.Length == 0 && Debugger.IsAttached == false && Environment.UserInteractive)
                {
                    Console.WriteLine("\nPress Enter to close...");
                    Console.ReadLine();
                }
            }
            return exitCode;
        }

        // --- Command Line Execution Handler ---
        static async Task<int> HandleCommandLineAsync(string[] args)
        {
            var parsedArgs = ParseArguments(args);
            bool runIfDue = false;

            if (parsedArgs.TryGetValue("runifdue", out var runIfDueVal) && (bool.TryParse(runIfDueVal, out runIfDue) || runIfDueVal.Equals("yes", StringComparison.OrdinalIgnoreCase) || runIfDueVal == "1"))
            {
                runIfDue = true;
                Log.Information("Conditional execution requested (runIfDue=true).");
            }

            // Determine Settings File Path
            string? settingsFilePath = null;
            string defaultSettingsPath = Path.Combine(AppContext.BaseDirectory, "app_settings.json");
            bool usingDefaultSettingsFile = true;
            if (parsedArgs.TryGetValue("settings", out var settingsArg))
            {
                settingsFilePath = Path.GetFullPath(settingsArg);
                Log.Information("Using settings file specified via command line: {Path}", settingsFilePath);
                if (!File.Exists(settingsFilePath))
                {
                    Log.Error("Specified settings file not found: {Path}", settingsFilePath);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"ERROR: Settings file not found: {settingsFilePath}");
                    Console.ResetColor();
                    return 1;
                }
                usingDefaultSettingsFile = false;
            }
            else
            {
                settingsFilePath = defaultSettingsPath;
                Log.Information("Using default settings file path: {Path}", settingsFilePath);
            }

            // Load Settings
            _appSettings = settingsFilePath != null && !usingDefaultSettingsFile
                ? SettingsManager.LoadSettingsFromPath(settingsFilePath) ?? CreateDefaultSettingsWithError($"loading specified settings file ({settingsFilePath})")
                : SettingsManager.LoadSettings();
            if (_appSettings == null)
                return 1;

            // Apply Overrides (handles paralleltasks now)
            ApplySettingOverrides(_appSettings, parsedArgs);
            Log.Information("Applied command-line overrides to settings.");
            LogEffectiveSettings(_appSettings); // Shows effective parallel tasks

            // Perform Conditional Check (if runIfDue)
            if (runIfDue)
            {
                int cycleHours = _appSettings.BackupCycleHours ?? AppSettings.DefaultBackupCycle;
                DateTime? lastRunUtc = _appSettings.LastSuccessfulBackupUtc;
                Log.Information("Checking if backup is due based on settings file timestamp and cycle.");
                Log.Information(" - Last Successful Backup (UTC, from settings): {Timestamp}", lastRunUtc?.ToString("o") ?? "<Never>");
                Log.Information(" - Backup Cycle (Hours): {Hours}", cycleHours);
                if (lastRunUtc.HasValue)
                {
                    TimeSpan elapsed = DateTime.UtcNow - lastRunUtc.Value;
                    Log.Information(" - Time Elapsed Since Last Backup: {ElapsedHours:F2} hours", elapsed.TotalHours);
                    if (elapsed.TotalHours < cycleHours)
                    {
                        Log.Information("Backup is NOT due (Cycle: {Cycle}h, Elapsed: {Elapsed:F2}h). Skipping backup action.", cycleHours, elapsed.TotalHours);
                        Console.WriteLine($"\nBackup is not due (Last run: {lastRunUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}, Cycle: {cycleHours}h). Skipping.");
                        return 0; // Success, just skipped
                    }
                    else
                    {
                        Log.Information("Backup IS due (Cycle: {Cycle}h, Elapsed: {Elapsed:F2}h). Proceeding...", cycleHours, elapsed.TotalHours);
                        Console.WriteLine("\nBackup is due. Proceeding...");
                    }
                }
                else
                {
                    Log.Information("Backup IS due (no previous successful run timestamp found in settings file). Proceeding...");
                    Console.WriteLine("\nBackup is due (no previous run recorded in settings). Proceeding...");
                }
            }
            else
            {
                Log.Information("Conditional execution not requested. Proceeding with action unconditionally.");
            }


            // Determine Action
            if (!parsedArgs.TryGetValue("action", out var action) || string.IsNullOrWhiteSpace(action))
            {
                Log.Error("Command line execution requires an 'action=' argument (e.g., action=backup, action=restore, action=repair, action=resume-restore).");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: Missing 'action=' argument.");
                Console.WriteLine("Usage examples:");
                Console.WriteLine(" GDrvBackup.exe action=backup [settings=...] [previous=...] [runIfDue=true] [parallelTasks=4]");
                Console.WriteLine(" GDrvBackup.exe action=restore input=archive.zip [settings=...] [parallelTasks=2]");
                Console.WriteLine(" GDrvBackup.exe action=resume-restore resume=folder_path [settings=...] [parallelTasks=3]");
                Console.WriteLine(" GDrvBackup.exe action=repair input=archive.zip [settings=...] [parallelTasks=1]");
                Console.ResetColor();
                return 1;
            }
            action = action.ToLowerInvariant();


            // Authenticate (if needed)
            DriveService? driveService = null;
            if (action == "backup" || action == "restore" || action == "resume-restore" || action == "repair")
            {
                Console.WriteLine("\nAuthenticating with Google Drive...");
                driveService = await GoogleDriveService.AuthenticateAsync();
                if (driveService == null)
                {
                    Log.Error("Authentication failed.");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\nAuthentication failed. Check logs.");
                    Console.ResetColor();
                    return 1;
                }
                Log.Information("Google Drive Authentication Successful.");
            }

            // Ensure Dirs Exist
            try
            {
                Directory.CreateDirectory(Path.GetFullPath(_appSettings.LocalBackupArchivePath ?? AppSettings.DefaultArchivePath));
                Directory.CreateDirectory(Path.GetFullPath(_appSettings.LocalTempWorkPath ?? AppSettings.DefaultTempPath));
                Log.Debug("Ensured backup archive and temp directories exist.");
            }
            catch (Exception ex) { Log.Error(ex, "Failed creating required directories."); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("\nError: Could not create required directories."); Console.ResetColor(); return 1; }

            // Instantiate Managers
            var backupManager = new BackupManager(driveService!, _appSettings);
            var restoreManager = new RestoreManager(driveService!, _appSettings);
            var repairManager = new RepairManager(driveService!, _appSettings, backupManager);
            var progress = CreateProgressHandler(_appSettings);

            // Execute Action
            bool success = false;
            try
            {
                switch (action)
                {
                    case "backup":
                        if (driveService == null)
                        {
                            Log.Error("Backup action requires Google Drive authentication.");
                            return 1;
                        }
                        if (!_appSettings.IsBackupConfigured())
                        {
                            PrintConfigurationWarning("Backup");
                            return 1;
                        }
                        parsedArgs.TryGetValue("previous", out var prevBackupPath);
                        if (!string.IsNullOrWhiteSpace(prevBackupPath))
                        {
                            try
                            {
                                prevBackupPath = Path.GetFullPath(prevBackupPath);
                            }
                            catch (Exception ex) { Log.Warning(ex, "Invalid path format for previous backup: {Path}", prevBackupPath); prevBackupPath = null; }
                            if (prevBackupPath != null && !File.Exists(prevBackupPath))
                            {
                                Log.Warning("Previous backup path provided via CLI not found: {Path}. Performing full backup.", prevBackupPath);
                                prevBackupPath = null;
                            }
                        }
                        Log.Information("Starting Backup (CLI) with MaxParallelTasks={Tasks}...", _appSettings.GetEffectiveMaxParallelTasks());
                        Console.WriteLine($"\nStarting Backup (Max Parallel: {_appSettings.GetEffectiveMaxParallelTasks()})...");
                        BackupResult backupResult = await backupManager.StartBackupAsync(_appSettings.GoogleDriveFolderId!, prevBackupPath, progress, _cts!.Token);
                        DisplayBackupResult(backupResult);
                        success = backupResult.Success && !backupResult.Cancelled;
                        if (success && !string.IsNullOrEmpty(backupResult.FinalArchivePath))
                        {
                            // Update Timestamp in Settings
                            DateTime backupCompletionTimeUtc = DateTime.UtcNow;
                            Log.Information("Backup successful. Updating timestamp in settings object.");
                            _appSettings.LastSuccessfulBackupUtc = backupCompletionTimeUtc;
                            string effectiveSettingsPathToSave = settingsFilePath!;
                            Log.Information("Saving updated settings back to: {Path}", effectiveSettingsPathToSave);
                            bool saveOk = SettingsManager.SaveSettingsToPath(_appSettings, effectiveSettingsPathToSave);
                            if (!saveOk)
                            {
                                Log.Error("Failed to save updated settings file with new timestamp to {Path}!", effectiveSettingsPathToSave);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"\nERROR: Backup succeeded but failed to save updated timestamp to settings file: {effectiveSettingsPathToSave}");
                                Console.ResetColor();
                            }
                            // Update Legacy Status File (if using default settings)
                            if (usingDefaultSettingsFile)
                            {
                                _backupStatus = StatusManager.LoadBackupStatus();
                                _backupStatus!.LastSuccessfulBackupTimestamp = backupCompletionTimeUtc;
                                StatusManager.SaveBackupStatus(_backupStatus);
                                Console.WriteLine($"\nLast backup time updated in settings file and status file.");
                            }
                            else
                            {
                                Console.WriteLine($"\nLast backup time updated in settings file: {effectiveSettingsPathToSave}");
                            }
                            Console.WriteLine($"New archive created at: {backupResult.FinalArchivePath}");
                        }
                        break;

                    case "restore":
                        if (driveService == null)
                        {
                            Log.Error("Restore action requires Google Drive authentication.");
                            return 1;
                        }
                        if (!_appSettings.IsRestoreConfigured())
                        {
                            PrintConfigurationWarning("Restore");
                            return 1;
                        }
                        if (!parsedArgs.TryGetValue("input", out var restoreZip) || string.IsNullOrWhiteSpace(restoreZip))
                        {
                            Log.Error("Restore action requires 'input=' argument.");
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"ERROR: Missing 'input=' argument for restore action.");
                            Console.ResetColor();
                            return 1;
                        }
                        try
                        {
                            restoreZip = Path.GetFullPath(restoreZip);
                        }
                        catch (Exception ex) { Log.Error(ex, "Invalid path format for input zip: {Path}", restoreZip); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Invalid path format for input zip: {restoreZip}"); Console.ResetColor(); return 1; }
                        if (!File.Exists(restoreZip))
                        {
                            Log.Error("Input zip file not found: {Path}", restoreZip);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"ERROR: Input zip file not found: {restoreZip}");
                            Console.ResetColor();
                            return 1;
                        }
                        Log.Information("Starting Restore (CLI) with MaxParallelTasks={Tasks}: {Path}", _appSettings.GetEffectiveMaxParallelTasks(), restoreZip);
                        Console.WriteLine($"\nStarting Restore from: {restoreZip} (Max Parallel: {_appSettings.GetEffectiveMaxParallelTasks()})");
                        RestoreResult restoreResult = await restoreManager.StartRestoreAsync(restoreZip, null, progress, _cts!.Token);
                        DisplayRestoreResult(restoreResult, restoreZip);
                        success = restoreResult.Success && !restoreResult.Cancelled;
                        break;

                    case "resume-restore":
                        if (driveService == null)
                        {
                            Log.Error("Resume Restore action requires Google Drive authentication.");
                            return 1;
                        }
                        if (!_appSettings.IsRestoreConfigured())
                        {
                            PrintConfigurationWarning("Resume Restore (basic config)");
                            return 1;
                        } // Needs temp path
                        if (!parsedArgs.TryGetValue("resume", out var resumeFolder) || string.IsNullOrWhiteSpace(resumeFolder))
                        {
                            Log.Error("Resume-restore action requires 'resume=' argument.");
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"ERROR: Missing 'resume=' argument.");
                            Console.ResetColor();
                            return 1;
                        }
                        try
                        {
                            resumeFolder = Path.GetFullPath(resumeFolder);
                        }
                        catch (Exception ex) { Log.Error(ex, "Invalid path format for resume folder: {Path}", resumeFolder); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Invalid path format for resume folder: {resumeFolder}"); Console.ResetColor(); return 1; }
                        if (!Directory.Exists(resumeFolder))
                        {
                            Log.Error("Resume folder not found: {Path}", resumeFolder);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"ERROR: Resume folder not found: {resumeFolder}");
                            Console.ResetColor();
                            return 1;
                        }
                        Log.Information("Starting Resume Restore (CLI) with MaxParallelTasks={Tasks} from folder: {Path}", _appSettings.GetEffectiveMaxParallelTasks(), resumeFolder);
                        Console.WriteLine($"\nAttempting to Resume Restore from: {resumeFolder} (Max Parallel: {_appSettings.GetEffectiveMaxParallelTasks()})");
                        RestoreResult resumeResult = await restoreManager.StartRestoreAsync(null, resumeFolder, progress, _cts!.Token);
                        DisplayRestoreResult(resumeResult, $"Resume from {Path.GetFileName(resumeFolder)}");
                        success = resumeResult.Success && !resumeResult.Cancelled;
                        break;


                    case "repair":
                        if (driveService == null)
                        {
                            Log.Error("Repair action requires Google Drive authentication.");
                            return 1;
                        }
                        if (!_appSettings.IsRepairConfigured())
                        {
                            PrintConfigurationWarning("Repair");
                            return 1;
                        }
                        if (!parsedArgs.TryGetValue("input", out var repairZip) || string.IsNullOrWhiteSpace(repairZip))
                        {
                            Log.Error("Repair action requires 'input=' argument.");
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"ERROR: Missing 'input=' argument.");
                            Console.ResetColor();
                            return 1;
                        }
                        try
                        {
                            repairZip = Path.GetFullPath(repairZip);
                        }
                        catch (Exception ex) { Log.Error(ex, "Invalid path format for repair zip: {Path}", repairZip); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"ERROR: Invalid path format for repair zip: {repairZip}"); Console.ResetColor(); return 1; }
                        if (!File.Exists(repairZip))
                        {
                            Log.Error("Input zip file for repair not found: {Path}", repairZip);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"ERROR: Input zip file not found: {repairZip}");
                            Console.ResetColor();
                            return 1;
                        }
                        Log.Information("Starting Repair (CLI) with MaxParallelTasks={Tasks}: {Path}", _appSettings.GetEffectiveMaxParallelTasks(), repairZip);
                        Console.WriteLine($"\nAttempting repair for: {repairZip} (Max Parallel Downloads: {_appSettings.GetEffectiveMaxParallelTasks()})");
                        RepairResult repairResult = await repairManager.RepairBackupAsync(repairZip, progress, _cts!.Token);
                        DisplayRepairResult(repairResult);
                        success = repairResult.OverallSuccess && !repairResult.Cancelled;
                        break;

                    default:
                        Log.Error("Invalid action specified: {Action}. Use 'backup', 'restore', 'resume-restore', or 'repair'.", action);
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"ERROR: Invalid action '{action}'. Use 'backup', 'restore', 'resume-restore', or 'repair'.");
                        Console.ResetColor();
                        return 1;
                }
            }
            catch (OperationCanceledException) { throw; } // Re-throw cancellation to be caught by Main
            catch (Exception ex) { Log.Error(ex, "Error executing command line action '{Action}'.", action); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\nERROR executing action '{action}': {ex.Message}"); Console.ResetColor(); return 1; }

            return success ? 0 : 1;
        }


        // --- Argument Parsing Helper ---
        static Dictionary<string, string> ParseArguments(string[] args)
        {
            var parsedArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? currentAction = null;
            foreach (var arg in args)
            {
                if (arg.Contains('='))
                {
                    var parts = arg.Split('=', 2);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                    {
                        string key = parts[0].Trim();
                        string value = parts[1];
                        if (value.Length > 1 && value.StartsWith('"') && value.EndsWith('"'))
                        {
                            value = value.Substring(1, value.Length - 2);
                        }
                        else if (!value.StartsWith('"') && !value.EndsWith('"'))
                        {
                            value = value.Trim();
                        }
                        else if (value == "\"\"" || value == "''")
                        {
                            value = string.Empty;
                        }
                        parsedArgs[key] = value;
                        if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
                        {
                            if (currentAction != null && !currentAction.Equals(value, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Warning("Multiple different 'action=' arguments provided ('{Action1}', '{Action2}'). Using the last one: '{Action2}'.", currentAction, value);
                            }
                            currentAction = value.ToLowerInvariant();
                        }
                    }
                    else
                    {
                        Log.Warning("Ignoring malformed argument (expected key=value): {Arg}", arg);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(arg))
                {
                    string potentialAction = arg.ToLowerInvariant();
                    if (potentialAction == "backup" || potentialAction == "restore" || potentialAction == "repair")
                    {
                        if (currentAction == null)
                        {
                            currentAction = potentialAction;
                            parsedArgs["action"] = currentAction;
                            Log.Information("Interpreted argument '{Arg}' as action shorthand.", arg);
                        }
                        else if (!currentAction.Equals(potentialAction, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Error("Conflicting actions specified ('{Action1}', '{Action2}'). Please specify only one action.", currentAction, potentialAction);
                        }
                    }
                    else
                    {
                        Log.Warning("Ignoring argument without '=' sign (and not a recognized action shorthand): {Arg}", arg);
                    }
                }
            }
            return parsedArgs;
        }


        // --- Settings Override Helper ---
        static void ApplySettingOverrides(AppSettings settings, Dictionary<string, string> overrides)
        {
            foreach (var kvp in overrides)
            {
                string key = kvp.Key.ToLowerInvariant();
                string value = kvp.Value;
                try
                {
                    switch (key)
                    {
                        // SKIP arguments handled by specific actions
                        case "action":
                        case "settings":
                        case "input":
                        case "previous":
                        case "runifdue":
                        case "resume":
                            continue;

                        // Apply AppSettings overrides
                        case "googledrivefolderid":
                            settings.GoogleDriveFolderId = string.IsNullOrWhiteSpace(value) ? null : value;
                            Log.Debug("Override: GoogleDriveFolderId = {V}", settings.GoogleDriveFolderId ?? "<null>");
                            break;
                        case "localbackuppath":
                        case "localbackuparchivepath":
                            settings.LocalBackupArchivePath = string.IsNullOrWhiteSpace(value) ? null : value;
                            Log.Debug("Override: LocalBackupArchivePath = {V}", settings.LocalBackupArchivePath ?? "<null>");
                            break;
                        case "localtemppath":
                        case "localtempworkpath":
                            settings.LocalTempWorkPath = string.IsNullOrWhiteSpace(value) ? null : value;
                            Log.Debug("Override: LocalTempWorkPath = {V}", settings.LocalTempWorkPath ?? "<null>");
                            break;
                        case "googledriverestoreparentid":
                        case "restoreparentid":
                            settings.GoogleDriveRestoreParentId = string.IsNullOrWhiteSpace(value) ? null : value;
                            Log.Debug("Override: GoogleDriveRestoreParentId = {V}", settings.GoogleDriveRestoreParentId ?? "<null>");
                            break;
                        case "backupcyclehours":
                        case "cycle":
                            if (int.TryParse(value, out int cycleHours) && cycleHours > 0)
                            {
                                settings.BackupCycleHours = cycleHours;
                                Log.Debug("Override: BackupCycleHours = {V}", settings.BackupCycleHours);
                            }
                            else if (string.IsNullOrWhiteSpace(value))
                            {
                                settings.BackupCycleHours = null;
                                Log.Debug("Override cleared: BackupCycleHours");
                            }
                            else
                            {
                                Log.Warning("Invalid value for override '{K}': {V}. Must be positive integer.", kvp.Key, value);
                            }
                            break;
                        case "showverboseprogress":
                        case "verbose":
                            if (bool.TryParse(value, out bool vb) || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "1")
                            {
                                settings.ShowVerboseProgress = true;
                                Log.Debug("Override: ShowVerboseProgress = true");
                            }
                            else if (!bool.TryParse(value, out vb) && (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "0"))
                            {
                                settings.ShowVerboseProgress = false;
                                Log.Debug("Override: ShowVerboseProgress = false");
                            }
                            else if (string.IsNullOrWhiteSpace(value))
                            {
                                settings.ShowVerboseProgress = null;
                                Log.Debug("Override cleared: ShowVerboseProgress");
                            }
                            else
                            {
                                Log.Warning("Invalid value for override '{K}': {V}. Use true/false, yes/no, 1/0.", kvp.Key, value);
                            }
                            break;

                        // --- NEW: Override for parallel tasks ---
                        case "paralleltasks":
                        case "maxparalleltasks":
                        case "tasks":
                            if (int.TryParse(value, out int pt) && pt >= 1)
                            {
                                // Apply override, clamping will happen in SettingsManager save or GetEffective
                                settings.MaxParallelTasks = pt;
                                Log.Debug("Override intent: MaxParallelTasks = {Value} (Effective value will be clamped between 1 and {Max})", settings.MaxParallelTasks, AppSettings.MaxAllowedParallelTasks);
                            }
                            else if (string.IsNullOrWhiteSpace(value))
                            {
                                settings.MaxParallelTasks = null; // Allow reverting to default
                                Log.Debug("Override cleared: MaxParallelTasks (will use default: {Default})", AppSettings.DefaultMaxParallelTasks);
                            }
                            else
                            {
                                Log.Warning("Invalid value for override '{Key}': {Value}. Must be an integer >= 1.", kvp.Key, value);
                            }
                            break;

                        default:
                            Log.Warning("Unknown setting override provided: {K}={V}", kvp.Key, value);
                            break;
                    }
                }
                catch (Exception ex) { Log.Error(ex, "Error applying override {K}={V}", kvp.Key, value); }
            }
            // Apply defaults AFTER overrides if any properties are still null
            // Note: The clamping of MaxParallelTasks happens in its setter or GetEffective method
            settings.LocalBackupArchivePath ??= AppSettings.DefaultArchivePath;
            settings.LocalTempWorkPath ??= AppSettings.DefaultTempPath;
            settings.GoogleDriveRestoreParentId ??= AppSettings.DefaultRestoreParent;
            settings.BackupCycleHours ??= AppSettings.DefaultBackupCycle;
            settings.ShowVerboseProgress ??= AppSettings.DefaultVerboseProgress;
            settings.ExcludedRelativePaths ??= new List<string>();
            settings.MaxParallelTasks ??= AppSettings.DefaultMaxParallelTasks;
        }

        // Helper to log effective settings in CLI mode
        private static void LogEffectiveSettings(AppSettings settings)
        {
            Log.Information("--- Effective Settings for Run ---");
            Log.Information("GoogleDriveFolderId: {Value}", settings.GoogleDriveFolderId ?? "<Not Set>");
            Log.Information("LocalBackupArchivePath: {Value}", settings.LocalBackupArchivePath);
            Log.Information("LocalTempWorkPath: {Value}", settings.LocalTempWorkPath);
            Log.Information("GoogleDriveRestoreParentId: {Value}", settings.GoogleDriveRestoreParentId);
            Log.Information("BackupCycleHours: {Value}", settings.BackupCycleHours);
            Log.Information("ShowVerboseProgress: {Value}", settings.ShowVerboseProgress);
            Log.Information("MaxParallelTasks: {Value}", settings.GetEffectiveMaxParallelTasks()); // Use helper to show clamped value
            Log.Information("ExcludedRelativePaths Count: {Value}", settings.ExcludedRelativePaths.Count);
            Log.Information("LastSuccessfulBackupUtc (from settings): {Value}", settings.LastSuccessfulBackupUtc?.ToString("o") ?? "<None>");
            Log.Information("--------------------------------------");
        }

        // Helper to create default settings and log error (used in CLI if load fails)
        private static AppSettings? CreateDefaultSettingsWithError(string context)
        {
            Log.Error("Failed {Context}. Proceeding with default settings, but configuration might be incomplete.", context);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: Failed {context}. Using default settings.");
            Console.ResetColor();
            // Return default settings, ensuring all defaults are explicitly set
            var settings = new AppSettings
            {
                LocalBackupArchivePath = AppSettings.DefaultArchivePath,
                LocalTempWorkPath = AppSettings.DefaultTempPath,
                GoogleDriveRestoreParentId = AppSettings.DefaultRestoreParent,
                BackupCycleHours = AppSettings.DefaultBackupCycle,
                ShowVerboseProgress = AppSettings.DefaultVerboseProgress,
                ExcludedRelativePaths = new List<string>(),
                MaxParallelTasks = AppSettings.DefaultMaxParallelTasks, // Add default
                LastSuccessfulBackupUtc = null
            };
            return settings;
        }


        // --- Main Interactive Application Logic and Menu ---
        static async Task RunAppLogicAsync()
        {
            Console.Title = "Google Drive ZIP Backup Tool";
            Console.WriteLine("Google Drive ZIP Backup Tool");
            Console.WriteLine("============================");

            _appSettings = SettingsManager.LoadSettings(); // Load default settings for interactive mode
            PrintCurrentSettings(); // Display current settings
            _backupStatus = StatusManager.LoadBackupStatus(); // Load legacy status for interactive check
            CheckBackupCycle(); // Perform initial check based on legacy/settings status

            Console.WriteLine("\nAuthenticating with Google Drive...");
            var driveService = await GoogleDriveService.AuthenticateAsync();
            if (driveService == null)
            {
                Log.Error("Authentication failed. Exiting.");
                Console.WriteLine("\nAuthentication failed. Check logs. Exiting.");
                return;
            }
            Log.Information("Google Drive Authentication Successful.");

            // Ensure Dirs Exist (Based on loaded settings)
            try
            {
                Directory.CreateDirectory(Path.GetFullPath(_appSettings.LocalBackupArchivePath ?? AppSettings.DefaultArchivePath));
                Directory.CreateDirectory(Path.GetFullPath(_appSettings.LocalTempWorkPath ?? AppSettings.DefaultTempPath));
                Log.Debug("Ensured backup archive and temp directories exist.");
            }
            catch (Exception ex) { Log.Error(ex, "Failed creating required directories."); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("\nError: Could not create required directories."); Console.ResetColor(); return; }

            // Instantiate Core Managers
            var backupManager = new BackupManager(driveService, _appSettings);
            var restoreManager = new RestoreManager(driveService, _appSettings);
            var repairManager = new RepairManager(driveService, _appSettings, backupManager);

            // --- Main Menu Loop ---
            while (true)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                PrintMenu();
                string? choice = Console.ReadLine()?.Trim();

                try
                {
                    switch (choice)
                    {
                        case "1":
                            await HandleBackupInteractive(backupManager);
                            break;                         // Create Backup
                        case "2":
                            await HandleRestoreInteractive(restoreManager);
                            break;                      // Restore from Zip
                        case "3":
                            await HandleResumeRestoreInteractive(restoreManager);
                            break;               // Resume Restore
                        case "4":
                            ConfigureSettingsAndUpdateGlobals();                                         // Configure Settings
                                                                                                         // Re-instantiate managers after potential settings change
                            _appSettings = SettingsManager.LoadSettings(); // Reload saved settings
                            backupManager = new BackupManager(driveService, _appSettings);
                            restoreManager = new RestoreManager(driveService, _appSettings);
                            repairManager = new RepairManager(driveService, _appSettings, backupManager);
                            PrintCurrentSettings();
                            break;
                        case "5":
                            await HandleRepair(repairManager);
                            break;                                  // Repair Backup
                        case "6":
                            ManageExclusions();                                                         // Manage Exclusions
                                                                                                        // Re-instantiate affected managers
                            _appSettings = SettingsManager.LoadSettings();
                            backupManager = new BackupManager(driveService, _appSettings);
                            repairManager = new RepairManager(driveService, _appSettings, backupManager);
                            break;
                        case "7":
                            HandleSaveSettingsProfile();
                            break;                                       // Save Profile
                        case "8":
                            HandleLoadSettingsProfile();                                                // Load Profile
                                                                                                        // Re-instantiate managers after potential load/save
                            _appSettings = SettingsManager.LoadSettings();
                            backupManager = new BackupManager(driveService, _appSettings);
                            restoreManager = new RestoreManager(driveService, _appSettings);
                            repairManager = new RepairManager(driveService, _appSettings, backupManager);
                            PrintCurrentSettings();
                            break;
                        case "9":
                            Console.WriteLine();
                            CheckBackupCycle();
                            break;                          // Check Backup Status
                        case "10":
                            Log.Information("User requested exit.");
                            Console.WriteLine("Exiting.");
                            return; // Exit
                        default:
                            Log.Debug("Invalid menu choice: {Choice}", choice);
                            Console.WriteLine("Invalid choice.");
                            break;
                    }
                }
                catch (OperationCanceledException) { throw; } // Re-throw to main handler
                catch (Exception ex) { Log.Error(ex, "Unexpected error in menu loop processing choice '{Choice}'.", choice); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\nAn unexpected error occurred: {ex.Message}\nCheck logs."); Console.ResetColor(); }
            } // End while loop
        } // End RunAppLogicAsync


        // --- Menu Printing (Updated) ---
        private static void PrintMenu()
        {
            Console.WriteLine("\nChoose an action:");
            Console.WriteLine(" 1. Create New Backup Archive (.zip)");
            Console.WriteLine(" 2. Restore from Backup Archive (.zip)");
            Console.WriteLine(" 3. Resume Interrupted Restore...");
            Console.WriteLine(" 4. Configure Settings");
            Console.WriteLine(" 5. Repair Backup Archive (.zip)");
            Console.WriteLine(" 6. Manage Exclusions (Subfolders)");
            Console.WriteLine(" 7. Save Settings Profile...");
            Console.WriteLine(" 8. Load Settings Profile...");
            Console.WriteLine(" 9. Check Backup Status Reminder");
            Console.WriteLine("10. Exit");
            Console.Write("Enter choice: ");
        }


        // --- Interactive Handlers ---
        static async Task HandleBackupInteractive(BackupManager backupManager)
        {
            if (!_appSettings.IsBackupConfigured())
            {
                PrintConfigurationWarning("Backup");
                return;
            }
            string? prevBackup = PromptForPreviousBackup();
            Log.Information("Starting Backup (Interactive) with MaxParallelTasks={Tasks}...", _appSettings.GetEffectiveMaxParallelTasks());
            Console.WriteLine($"\nStarting Backup (Max Parallel: {_appSettings.GetEffectiveMaxParallelTasks()})...");
            var progress = CreateProgressHandler(_appSettings);
            BackupResult result = await backupManager.StartBackupAsync(_appSettings.GoogleDriveFolderId!, prevBackup, progress, _cts!.Token);
            DisplayBackupResult(result);
            if (result.Success && !result.Cancelled && !string.IsNullOrEmpty(result.FinalArchivePath))
            {
                // Update Timestamps
                DateTime backupCompletionTimeUtc = DateTime.UtcNow;
                _backupStatus ??= StatusManager.LoadBackupStatus();
                _backupStatus.LastSuccessfulBackupTimestamp = backupCompletionTimeUtc;
                StatusManager.SaveBackupStatus(_backupStatus);
                _appSettings = SettingsManager.LoadSettings();
                _appSettings.LastSuccessfulBackupUtc = backupCompletionTimeUtc;
                bool saveOk = SettingsManager.SaveSettings(_appSettings);
                Log.Information("Backup successful. Updated timestamp in status file and default settings file.");
                Console.WriteLine($"\nLast backup time updated.");
                if (!saveOk)
                {
                    Log.Error("Failed to save updated timestamp to default settings file (app_settings.json).");
                    Console.WriteLine("Warning: Failed to update timestamp in default settings file.");
                }
                Console.WriteLine($"Remember to copy the new archive to offline storage:");
                Console.WriteLine(result.FinalArchivePath);
            }
            else if (!result.Cancelled)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nBackup failed or was interrupted. Check logs.");
                Console.ResetColor();
            }
        }
        static async Task HandleRestoreInteractive(RestoreManager restoreManager)
        {
            if (!_appSettings.IsRestoreConfigured())
            {
                PrintConfigurationWarning("Restore");
                return;
            }
            string? restoreZip = PromptForZipPath("restore");
            if (restoreZip == null)
                return;
            Log.Information("Starting Restore (Interactive) with MaxParallelTasks={Tasks}: {Path}", _appSettings.GetEffectiveMaxParallelTasks(), restoreZip);
            Console.WriteLine($"\nStarting Restore from: {restoreZip} (Max Parallel: {_appSettings.GetEffectiveMaxParallelTasks()})");
            var progress = CreateProgressHandler(_appSettings);
            RestoreResult result = await restoreManager.StartRestoreAsync(restoreZip, null, progress, _cts!.Token);
            DisplayRestoreResult(result, restoreZip);
        }
        static async Task HandleResumeRestoreInteractive(RestoreManager restoreManager)
        {
            if (!_appSettings.IsRestoreConfigured())
            {
                PrintConfigurationWarning("Resume Restore (basic config)");
                return;
            }
            Console.Write($"\nEnter the FULL PATH to the temporary restore folder to resume: ");
            string? resumeFolder = Console.ReadLine()?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(resumeFolder))
            {
                Log.Warning("User provided empty path for resume folder.");
                Console.WriteLine("Invalid path. Operation cancelled.");
                return;
            }
            if (resumeFolder.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("User provided zip instead of folder for resume: {Path}", resumeFolder);
                Console.WriteLine("Invalid path. Expected a folder path. Operation cancelled.");
                return;
            }
            // Resolve potential relative path
            try
            {
                resumeFolder = Path.GetFullPath(resumeFolder);
            }
            catch (Exception ex) { Log.Warning(ex, "Invalid path format for resume folder: {Path}", resumeFolder); Console.WriteLine($"Invalid path format: '{resumeFolder}'. Operation cancelled."); return; }
            if (!Directory.Exists(resumeFolder))
            {
                Log.Warning("User provided resume folder path not found: {Path}", resumeFolder);
                Console.WriteLine("Folder not found. Operation cancelled.");
                return;
            }
            Log.Information("Attempting Resume Restore (Interactive) with MaxParallelTasks={Tasks} from folder: {Path}", _appSettings.GetEffectiveMaxParallelTasks(), resumeFolder);
            Console.WriteLine($"\nAttempting to Resume Restore from: {resumeFolder} (Max Parallel: {_appSettings.GetEffectiveMaxParallelTasks()})");
            var progress = CreateProgressHandler(_appSettings);
            RestoreResult result = await restoreManager.StartRestoreAsync(null, resumeFolder, progress, _cts!.Token);
            DisplayRestoreResult(result, $"Resume from folder: {Path.GetFileName(resumeFolder)}");
        }
        static async Task HandleRepair(RepairManager repairManager)
        {
            if (!_appSettings.IsRepairConfigured())
            {
                PrintConfigurationWarning("Repair");
                return;
            }
            string? repairZip = PromptForZipPath("repair");
            if (repairZip == null)
                return;
            Log.Information("Starting Repair (Interactive) with MaxParallelTasks={Tasks}: {Path}", _appSettings.GetEffectiveMaxParallelTasks(), repairZip);
            Console.WriteLine($"\nAttempting repair for: {repairZip} (Max Parallel Downloads: {_appSettings.GetEffectiveMaxParallelTasks()})");
            var progress = CreateProgressHandler(_appSettings);
            RepairResult result = await repairManager.RepairBackupAsync(repairZip, progress, _cts!.Token);
            DisplayRepairResult(result);
        }

        // --- ManageExclusions ---
        private static void ManageExclusions()
        {
            bool changed = false;
            AppSettings tempSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_appSettings)) ?? SettingsManager.LoadSettings();
            tempSettings.ExcludedRelativePaths ??= new List<string>();

            while (true)
            {
                Console.Clear();
                Console.WriteLine("--- Manage Backup Exclusions ---");
                Console.WriteLine("Enter paths relative to the backup root folder ID specified in settings.");
                Console.WriteLine("Paths should start with '/' and represent folders.");
                Console.WriteLine("Example: /Temporary Files  or  /Project Cache/SubCache");
                Console.WriteLine("Excluding a folder excludes all its contents.");
                Console.WriteLine("\nCurrent Exclusions:");
                if (!tempSettings.ExcludedRelativePaths.Any())
                {
                    Console.WriteLine("  <None>");
                }
                else
                {
                    var sortedExclusions = tempSettings.ExcludedRelativePaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
                    for (int i = 0; i < sortedExclusions.Count; i++)
                    {
                        Console.WriteLine($"  {i + 1}. {sortedExclusions[i]}");
                    }
                }
                Console.WriteLine("\nOptions:\nA-Add, R-Remove, C-Clear, S-Save, D-Discard");
                Console.Write("Enter choice: ");
                string? choice = Console.ReadLine()?.ToUpperInvariant().Trim();
                switch (choice)
                {
                    case "A":
                        Console.Write("Enter relative path (e.g., /Temp): ");
                        string? newPath = Console.ReadLine()?.Trim();
                        if (!string.IsNullOrWhiteSpace(newPath))
                        {
                            string normPath = NormalizeExclusionPath(newPath);
                            if (!tempSettings.ExcludedRelativePaths.Contains(normPath, StringComparer.OrdinalIgnoreCase))
                            {
                                tempSettings.ExcludedRelativePaths.Add(normPath);
                                Console.WriteLine($"Added: {normPath}");
                                changed = true;
                            }
                            else
                            {
                                Console.WriteLine("Path already exists.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Invalid path.");
                        }
                        Thread.Sleep(1200);
                        break;
                    case "R":
                        var sortedList = tempSettings.ExcludedRelativePaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
                        if (!sortedList.Any())
                        {
                            Console.WriteLine("List empty.");
                            Thread.Sleep(1000);
                            break;
                        }
                        Console.Write("Enter number to remove: ");
                        if (int.TryParse(Console.ReadLine(), out int index) && index >= 1 && index <= sortedList.Count)
                        {
                            string pathToRemove = sortedList[index - 1];
                            int removedCount = tempSettings.ExcludedRelativePaths.RemoveAll(p => p.Equals(pathToRemove, StringComparison.OrdinalIgnoreCase));
                            if (removedCount > 0)
                            {
                                Console.WriteLine($"Removed: {pathToRemove}");
                                changed = true;
                            }
                            else
                            {
                                Console.WriteLine("Error removing path.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Invalid number.");
                        }
                        Thread.Sleep(1200);
                        break;
                    case "C":
                        if (tempSettings.ExcludedRelativePaths.Any())
                        {
                            Console.Write("Clear ALL exclusions? (Y/N): ");
                            if (Console.ReadLine()?.Trim().ToUpperInvariant() == "Y")
                            {
                                tempSettings.ExcludedRelativePaths.Clear();
                                Console.WriteLine("All cleared.");
                                changed = true;
                            }
                            else
                            {
                                Console.WriteLine("Clear cancelled.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("List already empty.");
                        }
                        Thread.Sleep(1200);
                        break;
                    case "S":
                        if (changed)
                        {
                            if (SettingsManager.SaveSettings(tempSettings))
                            {
                                _appSettings = tempSettings;
                                Console.WriteLine("Exclusions saved.");
                                Log.Information("User saved exclusion changes.");
                            }
                            else
                            {
                                Console.WriteLine("ERROR saving settings.");
                                Log.Error("Failed to save exclusion changes.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("No changes.");
                        }
                        Console.WriteLine("Returning...");
                        Thread.Sleep(1500);
                        return;
                    case "D":
                        if (changed)
                        {
                            Console.WriteLine("Changes discarded.");
                        }
                        else
                        {
                            Console.WriteLine("No changes to discard.");
                        }
                        Console.WriteLine("Returning...");
                        Thread.Sleep(1500);
                        return;
                    default:
                        Console.WriteLine("Invalid choice.");
                        Thread.Sleep(1000);
                        break;
                }
            }
        }
        private static string NormalizeExclusionPath(string path)
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

        // --- Save/Load Profile Handlers ---
        private static void HandleSaveSettingsProfile()
        {
            Console.WriteLine("\n--- Save Current Settings to Profile ---");
            Console.Write($"Enter the full path for the settings file (e.g., C:\\MyConfigs\\WorkProfile{SETTINGS_PROFILE_EXTENSION}): ");
            string? savePath = Console.ReadLine()?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(savePath))
            {
                Console.WriteLine("Save cancelled. Path cannot be empty.");
                Log.Warning("Save settings profile cancelled - empty path provided.");
                return;
            }
            try
            {
                Path.GetFullPath(savePath);
                if (!Path.IsPathRooted(savePath))
                {
                    Log.Warning("Provided save path is relative: {Path}. Saving relative to application directory.", savePath);
                    savePath = Path.Combine(AppContext.BaseDirectory, savePath);
                    Console.WriteLine($"Relative path detected. Saving to: {savePath}");
                }
            }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\nERROR checking path: {ex.Message}"); Console.ResetColor(); Log.Error(ex, "Error checking path provided for saving settings profile: {Path}", savePath); return; }
            if (!savePath.EndsWith(SETTINGS_PROFILE_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Warning: Path does not end with '{SETTINGS_PROFILE_EXTENSION}'. Recommended for clarity.");
            }
            if (File.Exists(savePath))
            {
                Console.Write($"File already exists: {savePath}\nOverwrite? (Y/N): ");
                if (Console.ReadLine()?.Trim().ToUpperInvariant() != "Y")
                {
                    Console.WriteLine("Save cancelled.");
                    Log.Information("User cancelled saving settings profile due to existing file.");
                    return;
                }
            }
            Log.Information("Attempting to save current settings to profile: {FilePath}", savePath);
            if (SettingsManager.SaveSettingsToPath(_appSettings, savePath))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\nSettings profile successfully saved to:\n{savePath}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nERROR: Failed to save settings profile.\nCheck permissions and logs.");
                Console.ResetColor();
            }
        }
        private static void HandleLoadSettingsProfile()
        {
            Console.WriteLine("\n--- Load Settings from Profile ---");
            Console.Write("Enter the full path to the settings profile file to load: ");
            string? loadPath = Console.ReadLine()?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(loadPath))
            {
                Console.WriteLine("Load cancelled. Path cannot be empty.");
                Log.Warning("Load settings profile cancelled - empty path provided.");
                return;
            }
            try
            {
                loadPath = Path.GetFullPath(loadPath);
            }
            catch (Exception ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"\nERROR checking path: {ex.Message}"); Console.ResetColor(); Log.Error(ex, "Error checking path provided for loading settings profile: {Path}", loadPath); return; }
            if (!File.Exists(loadPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nERROR: Settings profile file not found at:\n{loadPath}");
                Console.ResetColor();
                Log.Error("Load settings profile failed - file not found: {FilePath}", loadPath);
                return;
            }
            Log.Information("Attempting to load settings from profile: {FilePath}", loadPath);
            AppSettings? loadedSettings = SettingsManager.LoadSettingsFromPath(loadPath);
            if (loadedSettings != null)
            {
                Console.WriteLine("\n--- Profile Settings Preview ---");
                LogEffectiveSettings(loadedSettings); // Log settings Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" Loaded Folder ID (Backup): {loadedSettings.GoogleDriveFolderId ?? "<Not Set>"}");
                Console.WriteLine($" Loaded Archive Path      : {loadedSettings.LocalBackupArchivePath ?? AppSettings.DefaultArchivePath}");
                Console.WriteLine($" Loaded Temp Path         : {loadedSettings.LocalTempWorkPath ?? AppSettings.DefaultTempPath}");
                Console.WriteLine($" Loaded Restore Parent ID : {loadedSettings.GoogleDriveRestoreParentId ?? AppSettings.DefaultRestoreParent}");
                Console.WriteLine($" Loaded Cycle (Hrs)       : {loadedSettings.BackupCycleHours ?? AppSettings.DefaultBackupCycle}");
                Console.WriteLine($" Loaded Verbose Progress  : {(loadedSettings.ShowVerboseProgress ?? AppSettings.DefaultVerboseProgress ? "Yes" : "No")}");
                Console.WriteLine($" Loaded Max Parallel Tasks: {loadedSettings.GetEffectiveMaxParallelTasks()}");
                Console.WriteLine($" Loaded Exclusions        : {loadedSettings.ExcludedRelativePaths?.Count ?? 0}");
                if (loadedSettings.LastSuccessfulBackupUtc.HasValue)
                {
                    Console.WriteLine($" Loaded Last Backup (UTC) : {loadedSettings.LastSuccessfulBackupUtc.Value:yyyy-MM-dd HH:mm:ss}");
                }
                else
                {
                    Console.WriteLine($" Loaded Last Backup (UTC) : <None>");
                }
                Console.ResetColor();
                Console.WriteLine("-----------------------------");
                Console.Write("\nApply these settings and save as current default configuration? (Y/N): ");
                if (Console.ReadLine()?.Trim().ToUpperInvariant() == "Y")
                {
                    _appSettings = loadedSettings;
                    if (SettingsManager.SaveSettings(_appSettings))
                    {
                        Log.Information("Loaded settings profile applied and saved to default settings file.");
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\nSettings profile applied and saved as current configuration.");
                        Console.ResetColor();
                    }
                    else
                    {
                        Log.Error("Loaded settings profile, but failed to save them to the default settings file.");
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\nERROR: Settings loaded but failed to save as current configuration. Check logs.");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.WriteLine("Load cancelled. Current settings remain unchanged.");
                    Log.Information("User chose not to apply the loaded settings profile.");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nERROR: Failed to load settings profile from the specified file.\nFile might be corrupted or invalid. Check logs.");
                Console.ResetColor();
            }
        }

        // --- Configure Settings ---
        private static void ConfigureSettingsAndUpdateGlobals()
        {
            bool changed = false;
            AppSettings tempSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_appSettings)) ?? SettingsManager.LoadSettings(); // Deep copy

            while (true)
            {
                Console.Clear();
                Console.WriteLine("--- Configure Application Settings ---");
                Console.ResetColor();
                Console.Write(" 1. GDrive Folder ID (Backup): ");
                PrintSettingValue(tempSettings.GoogleDriveFolderId, "<Not Set>");
                Console.Write(" 2. Local Archive Path       : ");
                PrintSettingValue(tempSettings.LocalBackupArchivePath, AppSettings.DefaultArchivePath);
                Console.Write(" 3. Local Temp Path          : ");
                PrintSettingValue(tempSettings.LocalTempWorkPath, AppSettings.DefaultTempPath);
                Console.Write(" 4. GDrive Restore Parent ID : ");
                PrintSettingValue(tempSettings.GoogleDriveRestoreParentId, AppSettings.DefaultRestoreParent);
                Console.Write(" 5. Backup Reminder (Hours)  : ");
                PrintSettingValue(tempSettings.BackupCycleHours?.ToString(), AppSettings.DefaultBackupCycle.ToString());
                Console.Write(" 6. Show Verbose Progress    : ");
                PrintSettingValue((tempSettings.ShowVerboseProgress ?? AppSettings.DefaultVerboseProgress ? "Yes" : "No"), (AppSettings.DefaultVerboseProgress ? "Yes" : "No"));
                Console.Write($" 7. Max Parallel Tasks       : ");
                PrintSettingValue(tempSettings.MaxParallelTasks?.ToString(), AppSettings.DefaultMaxParallelTasks.ToString() + $" (Max: {AppSettings.MaxAllowedParallelTasks})");

                Console.WriteLine($"\nChoose setting (1-7), S=Save, C=Cancel:");
                string? c = Console.ReadLine()?.Trim().ToUpperInvariant();
                switch (c)
                {
                    case "1":
                        tempSettings.GoogleDriveFolderId = PromptForSettingChange("GDrive Folder ID", tempSettings.GoogleDriveFolderId, ref changed, canBeNull: true);
                        break;
                    case "2":
                        tempSettings.LocalBackupArchivePath = PromptForSettingChange("Archive Path", tempSettings.LocalBackupArchivePath, ref changed, AppSettings.DefaultArchivePath);
                        break;
                    case "3":
                        tempSettings.LocalTempWorkPath = PromptForSettingChange("Temp Path", tempSettings.LocalTempWorkPath, ref changed, AppSettings.DefaultTempPath);
                        break;
                    case "4":
                        tempSettings.GoogleDriveRestoreParentId = PromptForSettingChange("Restore Parent ID", tempSettings.GoogleDriveRestoreParentId, ref changed, AppSettings.DefaultRestoreParent);
                        break;
                    case "5":
                        tempSettings.BackupCycleHours = PromptForIntSettingChange("Reminder (Hours)", tempSettings.BackupCycleHours, AppSettings.DefaultBackupCycle, ref changed, 1);
                        break;
                    case "6":
                        tempSettings.ShowVerboseProgress = PromptForBoolSettingChange("Show verbose progress?", tempSettings.ShowVerboseProgress, AppSettings.DefaultVerboseProgress, ref changed);
                        break;
                    case "7":
                        tempSettings.MaxParallelTasks = PromptForIntSettingChange($"Max Parallel Tasks (1-{AppSettings.MaxAllowedParallelTasks})", tempSettings.MaxParallelTasks, AppSettings.DefaultMaxParallelTasks, ref changed, 1, AppSettings.MaxAllowedParallelTasks);
                        break;
                    case "S": // Save
                        if (changed)
                        {
                            tempSettings.LocalBackupArchivePath ??= AppSettings.DefaultArchivePath;
                            tempSettings.LocalTempWorkPath ??= AppSettings.DefaultTempPath;
                            tempSettings.GoogleDriveRestoreParentId ??= AppSettings.DefaultRestoreParent;
                            tempSettings.BackupCycleHours ??= AppSettings.DefaultBackupCycle;
                            tempSettings.ShowVerboseProgress ??= AppSettings.DefaultVerboseProgress;
                            tempSettings.MaxParallelTasks ??= AppSettings.DefaultMaxParallelTasks;
                            tempSettings.MaxParallelTasks = Math.Clamp(tempSettings.MaxParallelTasks.Value, 1, AppSettings.MaxAllowedParallelTasks); // Clamp before save
                            if (SettingsManager.SaveSettings(tempSettings))
                            {
                                _appSettings = tempSettings;
                                Console.WriteLine("Settings saved.");
                                Log.Information("User saved configuration changes.");
                            }
                            else
                            {
                                Console.WriteLine("ERROR saving settings.");
                                Log.Error("Failed to save user configuration changes.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("No changes made.");
                        }
                        Console.WriteLine("Returning...");
                        Thread.Sleep(1500);
                        return;
                    case "C":
                        Console.WriteLine("Changes cancelled.");
                        Log.Information("User cancelled configuration changes.");
                        Thread.Sleep(1000);
                        return;
                    default:
                        Console.WriteLine("Invalid choice.");
                        Thread.Sleep(1000);
                        break;
                }
            }
        }
        // --- Setting Prompt Helpers ---
        private static void PrintSettingValue(string? currentValue, string defaultValue)
        {
            bool isDefault = string.IsNullOrEmpty(currentValue?.Trim());
            string displayValue = isDefault ? defaultValue : currentValue!;
            Console.ForegroundColor = isDefault ? ConsoleColor.DarkGray : ConsoleColor.White;
            Console.Write(displayValue);
            if (isDefault && currentValue != null)
            { /* Explicitly set to empty */
            }
            else if (isDefault)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(" (Default)");
            }
            Console.ResetColor();
            Console.WriteLine();
        }
        private static string? PromptForSettingChange(string settingName, string? currentValue, ref bool changed, string? defaultValue = null, bool canBeNull = false)
        {
            Console.Write($"Enter {settingName} [{(string.IsNullOrEmpty(currentValue) ? (defaultValue != null ? $"Default: {defaultValue}" : "<Not Set>") : currentValue)}]: ");
            string? input = Console.ReadLine()?.Trim();
            string? newValue;
            if (string.IsNullOrWhiteSpace(input))
            {
                newValue = canBeNull ? null : (currentValue ?? defaultValue);
            }
            else
            {
                newValue = input;
            }
            if (!string.Equals(currentValue, newValue))
            {
                changed = true;
            }
            return newValue;
        }
        private static int? PromptForIntSettingChange(string settingName, int? currentValue, int defaultValue, ref bool changed, int minValue = 1, int? maxValue = null)
        {
            Console.Write($"Enter {settingName} [{(currentValue.HasValue ? currentValue.Value.ToString() : $"Default: {defaultValue}")}]: ");
            string? input = Console.ReadLine()?.Trim();
            int? newValue;
            if (string.IsNullOrWhiteSpace(input))
            {
                newValue = currentValue ?? defaultValue;
            }
            else if (int.TryParse(input, out int parsedValue) && parsedValue >= minValue && (!maxValue.HasValue || parsedValue <= maxValue.Value))
            {
                newValue = parsedValue;
            }
            else
            {
                Console.WriteLine($"Invalid input. Must be an integer >= {minValue}" + (maxValue.HasValue ? $" and <= {maxValue.Value}." : "."));
                Thread.Sleep(1200);
                newValue = currentValue;
            }
            if (currentValue != newValue)
            {
                changed = true;
            }
            return newValue;
        }
        private static bool? PromptForBoolSettingChange(string settingName, bool? currentValue, bool defaultValue, ref bool changed)
        {
            bool effectiveCurrent = currentValue ?? defaultValue;
            Console.Write($"{settingName} (Y/N) [Current: {(effectiveCurrent ? 'Y' : 'N')}]: ");
            string? input = Console.ReadLine()?.Trim().ToUpperInvariant();
            bool? newValue;
            if (input == "Y")
            {
                newValue = true;
            }
            else if (input == "N")
            {
                newValue = false;
            }
            else if (string.IsNullOrWhiteSpace(input))
            {
                newValue = currentValue ?? defaultValue;
            }
            else
            {
                Console.WriteLine("Invalid input. Use Y or N.");
                Thread.Sleep(1000);
                newValue = currentValue;
            }
            if (currentValue != newValue)
            {
                changed = true;
            }
            return newValue;
        }

        // --- Progress Handler ---
        private static Progress<BackupProgressReport> CreateProgressHandler(AppSettings settings)
        {
            bool verbose = settings.ShowVerboseProgress ?? AppSettings.DefaultVerboseProgress;
            int lastReportedProcessed = -1;
            int lastTotal = -1;
            string lastAction = "";
            bool summaryLineActive = false;
            Stopwatch throttleStopwatch = Stopwatch.StartNew();
            const int throttleMs = 200;
            return new Progress<BackupProgressReport>(report => { if (verbose) { if (summaryLineActive) { Console.WriteLine(); summaryLineActive = false; } string message = $"[Core] {report.CurrentAction}"; if (!string.IsNullOrEmpty(report.CurrentFilePath)) { bool showFullPath = report.CurrentAction.Contains("Analyzing") || report.CurrentAction.Contains("Preparing") || report.CurrentAction.Contains("Listing"); string displayPath = showFullPath ? report.CurrentFilePath : Path.GetFileName(report.CurrentFilePath); if (!string.IsNullOrEmpty(report.CurrentArchivePath) && !report.CurrentArchivePath.Equals(Path.GetFileName(displayPath), StringComparison.OrdinalIgnoreCase) && (report.CurrentAction.Contains("Processing") || report.CurrentAction.Contains("Downloading") || report.CurrentAction.Contains("Copying") || report.CurrentAction.Contains("Checking"))) { message += $": {displayPath} -> {Path.GetFileName(report.CurrentArchivePath)}"; } else { message += $": {displayPath}"; } } else if (!string.IsNullOrEmpty(report.CurrentArchivePath)) { message += $": {Path.GetFileName(report.CurrentArchivePath)}"; } if (report.TotalItemsToProcess > 1) { message += $" ({report.ProcessedItems}/{report.TotalItemsToProcess})"; } Console.WriteLine(message); lastAction = report.CurrentAction; } else { bool actionChanged = report.CurrentAction != lastAction; bool totalChanged = report.TotalItemsToProcess != lastTotal; if (actionChanged || (totalChanged && summaryLineActive)) { if (summaryLineActive) { Console.Write($"\r[Core] {lastAction}: {lastReportedProcessed} of {lastTotal}... Done.      "); Console.WriteLine(); } if (report.TotalItemsToProcess > 0) { Console.Write($"[Core] {report.CurrentAction}: 0 of {report.TotalItemsToProcess}...      "); summaryLineActive = true; } else { Console.WriteLine($"[Core] {report.CurrentAction}..."); summaryLineActive = false; } lastReportedProcessed = 0; lastTotal = report.TotalItemsToProcess; lastAction = report.CurrentAction; throttleStopwatch.Restart(); } if (report.TotalItemsToProcess > 1 && report.ProcessedItems > lastReportedProcessed && throttleStopwatch.ElapsedMilliseconds >= throttleMs) { if (report.TotalItemsToProcess != lastTotal) lastTotal = report.TotalItemsToProcess; Console.Write($"\r[Core] {report.CurrentAction}: {report.ProcessedItems} of {lastTotal}...      "); lastReportedProcessed = report.ProcessedItems; if (!summaryLineActive) summaryLineActive = true; throttleStopwatch.Restart(); if (report.ProcessedItems == lastTotal) { Console.Write($"\r[Core] {report.CurrentAction}: {report.ProcessedItems} of {lastTotal}... Done.      "); Console.WriteLine(); summaryLineActive = false; } } else if (summaryLineActive && report.ProcessedItems == lastTotal && report.ProcessedItems > lastReportedProcessed) { Console.Write($"\r[Core] {report.CurrentAction}: {report.ProcessedItems} of {lastTotal}... Done.      "); Console.WriteLine(); summaryLineActive = false; } else if (report.TotalItemsToProcess == 1 && report.ProcessedItems == 1 && !summaryLineActive && actionChanged) { Console.WriteLine($"[Core] {report.CurrentAction}: Done."); summaryLineActive = false; } } });
        }

        // --- Result Display Helpers ---
        private static void DisplayBackupResult(BackupResult result)
        {
            Console.WriteLine("\n--- Backup Summary ---");
            Console.WriteLine($"Result: {(result.Cancelled ? "Cancelled" : (result.Success ? "Success" : "Failed"))}");
            Console.WriteLine($"Duration: {result.Duration:hh\\:mm\\:ss}");
            if (!result.Cancelled)
            {
                Console.WriteLine($"Final Archive: {result.FinalArchivePath ?? "N/A"}");
                Console.WriteLine($"Files Listed (Drive Scan): {result.FilesListed}");
                Console.WriteLine($"Unsupported/Excluded Skipped: {result.UnsupportedSkipped}");
                Console.WriteLine($"Files Copied (Incremental): {result.FilesCopied} ({FormatBytes(result.TotalBytesCopied)})");
                Console.WriteLine($" - Copy Fallback/Errors: {result.CopyErrors}");
                Console.WriteLine($"Download Attempts (New/Changed/Fallback): {result.DownloadAttempts}");
                Console.WriteLine($" - Successful Downloads: {result.SuccessfulDownloads} ({FormatBytes(result.TotalBytesDownloaded)})");
                Console.WriteLine($" - Failed Downloads: {result.FailedDownloads}");
                if (result.FailedDownloads > 0 || result.CopyErrors > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: {result.FailedDownloads + result.CopyErrors} file(s) could not be backed up correctly. Check logs.");
                    Console.ResetColor();
                }
                else if (!result.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Backup failed. Check logs.");
                    Console.ResetColor();
                }
            }
            Console.WriteLine("----------------------");
        }
        private static void DisplayRestoreResult(RestoreResult result, string sourceDescription)
        {
            Console.WriteLine("\n--- Restore Summary ---");
            Console.WriteLine($"Source: {sourceDescription}");
            Console.WriteLine($"Result: {(result.Cancelled ? "Cancelled" : (result.Success ? "Success" : "Partial/Failed"))}");
            Console.WriteLine($"Duration: {result.Duration:hh\\:mm\\:ss}");
            if (!result.Cancelled)
            {
                Console.WriteLine($"Manifest Entries (Expected Files): {result.FilesProcessed}");
                Console.WriteLine($"Files Uploaded Successfully (This Run): {result.FilesUploaded}");
                Console.WriteLine($"Files Skipped/Failed (This Run): {result.FilesSkippedOrFailed}");
                if (result.FilesSkippedOrFailed > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: {result.FilesSkippedOrFailed} file(s) were skipped or failed to upload. Check logs.");
                    if (!result.Success)
                    {
                        Console.WriteLine("The restore did not complete fully. You may need to resume or check the logs.");
                    }
                    Console.ResetColor();
                }
                else if (!result.Success && !result.Cancelled)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Restore failed or did not process all expected files. Check logs.");
                    Console.ResetColor();
                }
            }
            Console.WriteLine("-----------------------");
        }
        private static void DisplayRepairResult(RepairResult result)
        {
            Console.WriteLine("\n--- Repair Summary ---");
            Console.WriteLine($"Result: {(result.Cancelled ? "Cancelled" : (result.OverallSuccess ? "Success" : (result.RepairAttempted ? "Partial/Failed" : "Not Needed/Failed")))}");
            Console.WriteLine($"Duration: {result.Duration:hh\\:mm\\:ss}");
            if (!result.Cancelled)
            {
                Console.WriteLine($"Repaired Archive: {result.RepairedArchivePath ?? "N/A (Repair failed or not needed)"}");
                Console.WriteLine($"Manifest Entries Checked: {result.ManifestEntries}");
                Console.WriteLine($"Files Found OK Initially: {result.FilesFoundOk}");
                Console.WriteLine($"Files Initially Missing: {result.FilesInitiallyMissing}");
                Console.WriteLine($" - Downloads Attempted: {result.DownloadsAttempted}");
                Console.WriteLine($" - Downloads Succeeded: {result.DownloadsSucceeded} ({FormatBytes(result.TotalBytesRepaired)})");
                Console.WriteLine($" - Downloads Failed: {result.FailedDownloads}");
                Console.WriteLine($" - Repairs Skipped (No ID): {result.RepairsSkippedNoId}");
                int totalErrors = result.FailedDownloads + result.RepairsSkippedNoId;
                if (!result.OverallSuccess && result.RepairAttempted)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: Repair attempted but {totalErrors} file(s) could not be recovered. Check logs.");
                    Console.ResetColor();
                }
                else if (!result.RepairAttempted && result.ManifestEntries > 0 && result.FilesInitiallyMissing > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error: {result.FilesInitiallyMissing} missing file(s) detected, but repair could not be performed (e.g., missing IDs).");
                    Console.ResetColor();
                }
                else if (!result.OverallSuccess && !result.RepairAttempted && result.ManifestEntries == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Repair failed early, likely could not load manifest. Check logs.");
                    Console.ResetColor();
                }
            }
            Console.WriteLine("----------------------");
        }
        private static string FormatBytes(long bytes)
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

        // --- Other Helpers ---
        private static void PrintCurrentSettings()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nCurrent Settings (from app_settings.json):");
            Console.ResetColor();
            Console.WriteLine($" Folder ID (Backup): {_appSettings.GoogleDriveFolderId ?? "<Not Set>"}");
            Console.WriteLine($" Archive Path      : {_appSettings.LocalBackupArchivePath ?? AppSettings.DefaultArchivePath}");
            Console.WriteLine($" Temp Path         : {_appSettings.LocalTempWorkPath ?? AppSettings.DefaultTempPath}");
            Console.WriteLine($" Restore Parent ID : {_appSettings.GoogleDriveRestoreParentId ?? AppSettings.DefaultRestoreParent}");
            Console.WriteLine($" Cycle (Hrs)       : {_appSettings.BackupCycleHours ?? AppSettings.DefaultBackupCycle}");
            Console.WriteLine($" Verbose Progress  : {(_appSettings.ShowVerboseProgress ?? AppSettings.DefaultVerboseProgress ? "Yes" : "No")}");
            Console.WriteLine($" Max Parallel Tasks: {_appSettings.GetEffectiveMaxParallelTasks()} (Config: {_appSettings.MaxParallelTasks?.ToString() ?? "Default"})");
            int exclusionCount = _appSettings.ExcludedRelativePaths?.Count ?? 0;
            Console.WriteLine($" Exclusions        : {exclusionCount} configured (use option 6 to view/manage)");
            if (_appSettings.LastSuccessfulBackupUtc.HasValue)
            {
                Console.WriteLine($" Last Backup (UTC) : {_appSettings.LastSuccessfulBackupUtc.Value:yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                Console.WriteLine($" Last Backup (UTC) : <Never recorded in settings>");
            }
        }
        private static void PrintConfigurationWarning(string op)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nERROR: Cannot perform {op}. Required settings missing/invalid.");
            Console.WriteLine("Use option 4 or CLI args to configure.");
            bool tempPathMissing = string.IsNullOrWhiteSpace(_appSettings.LocalTempWorkPath);
            if (op.Contains("Backup"))
            {
                if (string.IsNullOrWhiteSpace(_appSettings.GoogleDriveFolderId))
                    Console.WriteLine("- Google Drive Folder ID needed.");
                if (string.IsNullOrWhiteSpace(_appSettings.LocalBackupArchivePath))
                    Console.WriteLine("- Local Backup Archive Path needed.");
                if (tempPathMissing)
                    Console.WriteLine("- Local Temp Path needed.");
            }
            if (op.Contains("Restore"))
            {
                if (tempPathMissing)
                    Console.WriteLine("- Local Temp Path needed.");
                if (!op.Contains("Resume") && string.IsNullOrWhiteSpace(_appSettings.GoogleDriveRestoreParentId))
                    Console.WriteLine("- Google Drive Restore Parent ID needed for fresh restore.");
            }
            if (op.Contains("Repair"))
            {
                if (tempPathMissing)
                    Console.WriteLine("- Local Temp Path needed.");
            }
            Console.ResetColor();
        }
        private static string? PromptForPreviousBackup()
        {
            Console.WriteLine("\nOptional: Path to previous backup ZIP for incremental check?");
            Console.Write("Enter path or press Enter for full backup: ");
            string? p = Console.ReadLine()?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(p))
            {
                Log.Information("No previous backup path provided. Performing full backup.");
                Console.WriteLine("[Backup] Performing full backup.");
                return null;
            }
            try
            {
                p = Path.GetFullPath(p);
            }
            catch (Exception ex) { Log.Warning(ex, "Invalid path format for previous backup: {Path}", p); Console.WriteLine($"[Backup] Warning: Invalid path format '{p}'. Performing full backup."); return null; }
            if (!File.Exists(p))
            {
                Log.Warning("Previous backup path not found: {Path}. Performing full backup.", p);
                Console.WriteLine($"[Backup] Warning: Previous backup path '{p}' not found. Performing full backup.");
                return null;
            }
            Log.Information("Using previous backup for incremental check: {Path}", p);
            Console.WriteLine($"[Backup] Using previous archive: {Path.GetFileName(p)}");
            return p;
        }
        private static string? PromptForZipPath(string action)
        {
            Console.Write($"Enter FULL PATH to the backup .zip file to {action}: ");
            string? p = Console.ReadLine()?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(p))
            {
                Log.Warning("Empty path provided for {Action}.", action);
                Console.WriteLine("Invalid path.");
                return null;
            }
            if (!p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Path for {Action} does not end with .zip: {Path}", action, p);
                Console.WriteLine("Invalid path (must be .zip).");
                return null;
            }
            try
            {
                p = Path.GetFullPath(p);
            }
            catch (Exception ex) { Log.Warning(ex, "Invalid path format for {Action} zip: {Path}", action, p); Console.WriteLine($"Invalid path format '{p}'."); return null; }
            if (!File.Exists(p))
            {
                Log.Warning("Path for {Action} not found: {Path}", action, p);
                Console.WriteLine("File not found.");
                return null;
            }
            Log.Information("User selected ZIP file for {Action}: {Path}", action, p);
            return p;
        }

        // --- CheckBackupCycle ---
        private static void CheckBackupCycle()
        {
            DateTime? settingsTimestamp = _appSettings.LastSuccessfulBackupUtc;
            int cycle = _appSettings.BackupCycleHours ?? AppSettings.DefaultBackupCycle;
            _backupStatus ??= StatusManager.LoadBackupStatus();
            DateTime? legacyTimestamp = _backupStatus?.LastSuccessfulBackupTimestamp;
            DateTime? lastBackupToShow = null;
            string source = "";
            if (settingsTimestamp.HasValue && legacyTimestamp.HasValue)
            {
                lastBackupToShow = settingsTimestamp.Value > legacyTimestamp.Value ? settingsTimestamp : legacyTimestamp;
                source = settingsTimestamp.Value > legacyTimestamp.Value ? "settings file" : "legacy status file";
            }
            else if (settingsTimestamp.HasValue)
            {
                lastBackupToShow = settingsTimestamp;
                source = "settings file";
            }
            else if (legacyTimestamp.HasValue)
            {
                lastBackupToShow = legacyTimestamp;
                source = "legacy status file";
            }
            if (lastBackupToShow == null)
            {
                Log.Information("Backup cycle check (interactive): No previous successful backup recorded.");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Backup Status Reminder: No previous successful backup recorded.");
                Console.ResetColor();
                return;
            }
            DateTime last = lastBackupToShow.Value;
            TimeSpan elapsed = DateTime.UtcNow - last;
            string msg = $"Last successful backup ({source}): {last.ToLocalTime():yyyy-MM-dd HH:mm} Local / {last:yyyy-MM-dd HH:mm} UTC ({(int)elapsed.TotalHours} hours ago)";
            if (elapsed.TotalHours > cycle)
            {
                Log.Warning("Backup cycle check (interactive): {Msg} - Exceeds configured cycle ({Cycle} hrs).", msg, cycle);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARNING (Reminder): {msg}. Backup cycle is {cycle} hours.");
                Console.ResetColor();
            }
            else
            {
                Log.Information("Backup cycle check (interactive): {Msg} - Within configured cycle ({Cycle} hrs).", msg, cycle);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Status (Reminder): {msg}. (Cycle: {cycle} hrs)");
                Console.ResetColor();
            }
        }

    } // End Class Program
} // End Namespace