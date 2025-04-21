using System;
using System.Collections.Generic; // Added
using Serilog;
using System.IO;
using System.Text.Json;
using System.Linq; // For Order()

namespace GoogleDriveBackup.Core
{
    public static class SettingsManager
    {
        private static readonly string SettingsFileName = "app_settings.json";
        // Assume settings are relative to the *calling application's* directory
        private static string GetSettingsFilePath() => Path.Combine(AppContext.BaseDirectory, SettingsFileName);

        // Loads settings from the default location
        public static AppSettings LoadSettings()
        {
            string filePath = GetSettingsFilePath();
            AppSettings? loadedSettings = LoadSettingsInternal(filePath);

            if (loadedSettings != null)
            {
                Log.Information("Application settings loaded from default path {FilePath}", filePath);
                return loadedSettings;
            }
            else
            {
                Log.Warning("Could not load settings from default path {FilePath}. Using default settings.", filePath);
                // Return default settings (including empty exclusion list and null timestamp)
                return CreateDefaultSettings();
            }
        }

        // Loads settings from a specified path
        public static AppSettings? LoadSettingsFromPath(string filePath)
        {
            Log.Information("Attempting to load settings profile from: {FilePath}", filePath);
            AppSettings? loadedSettings = LoadSettingsInternal(filePath);

            if (loadedSettings != null)
            {
                Log.Information("Settings profile loaded successfully from {FilePath}", filePath);
                return loadedSettings;
            }
            else
            {
                Log.Error("Failed to load settings profile from {FilePath}", filePath);
                return null; // Indicate failure
            }
        }

        // Internal helper for loading logic
        private static AppSettings? LoadSettingsInternal(string filePath)
        {
            if (!File.Exists(filePath))
            {
                // Log differently based on whether it's the default path or a specified one
                if (filePath == GetSettingsFilePath())
                    Log.Information("Default settings file not found at {FilePath}.", filePath);
                else
                    Log.Warning("Specified settings file not found at {FilePath}.", filePath);
                return null;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                // Use options that are tolerant of missing properties when deserializing
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
                if (settings != null)
                {
                    // Apply defaults ONLY if specific settings are missing from the file
                    ApplyDefaults(settings);
                    return settings;
                }
                Log.Warning("Failed to deserialize settings file content from {FilePath}.", filePath);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read or parse settings from {FilePath}.", filePath);
                return null;
            }
        }

        // Helper to apply defaults to a settings object *without* setting LastSuccessfulBackupUtc
        private static void ApplyDefaults(AppSettings settings)
        {
            settings.LocalBackupArchivePath ??= AppSettings.DefaultArchivePath;
            settings.LocalTempWorkPath ??= AppSettings.DefaultTempPath;
            settings.GoogleDriveRestoreParentId ??= AppSettings.DefaultRestoreParent;
            settings.BackupCycleHours ??= AppSettings.DefaultBackupCycle;
            settings.ShowVerboseProgress ??= AppSettings.DefaultVerboseProgress;
            settings.ExcludedRelativePaths ??= new List<string>(); // Ensure initialized
            settings.MaxParallelTasks ??= AppSettings.DefaultMaxParallelTasks; // Apply default for parallel tasks

            // --- Clamp MaxParallelTasks on load/default application ---
            if (settings.MaxParallelTasks.HasValue)
            {
                settings.MaxParallelTasks = Math.Clamp(settings.MaxParallelTasks.Value, 1, AppSettings.MaxAllowedParallelTasks);
            }
            // DO NOT set a default for settings.LastSuccessfulBackupUtc here
        }

        // Helper to create a default settings object
        private static AppSettings CreateDefaultSettings()
        {
            // LastSuccessfulBackupUtc defaults to null
            return new AppSettings
            {
                LocalBackupArchivePath = AppSettings.DefaultArchivePath,
                LocalTempWorkPath = AppSettings.DefaultTempPath,
                GoogleDriveRestoreParentId = AppSettings.DefaultRestoreParent,
                BackupCycleHours = AppSettings.DefaultBackupCycle,
                ShowVerboseProgress = AppSettings.DefaultVerboseProgress,
                ExcludedRelativePaths = new List<string>(), // Ensure initialized
                MaxParallelTasks = AppSettings.DefaultMaxParallelTasks // Add default
            };
        }

        // Saves settings to the default location
        public static bool SaveSettings(AppSettings settings)
        {
            string filePath = GetSettingsFilePath();
            Log.Information("Attempting to save settings to default path: {FilePath}", filePath);
            bool success = SaveSettingsInternal(settings, filePath);
            if (success)
            {
                Log.Information("Application settings saved to default path {FilePath}", filePath);
            }
            return success;
        }

        // Saves settings to a specified path
        public static bool SaveSettingsToPath(AppSettings settings, string filePath)
        {
            Log.Information("Attempting to save settings profile to: {FilePath}", filePath);
            bool success = SaveSettingsInternal(settings, filePath);
            if (success)
            {
                Log.Information("Settings profile saved successfully to {FilePath}", filePath);
            }
            return success;
        }

        // Internal helper for saving logic
        private static bool SaveSettingsInternal(AppSettings settings, string filePath)
        {
            try
            {
                // Ensure directory exists before trying to save
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Ensure list is sorted before saving for consistency
                settings.ExcludedRelativePaths = settings.ExcludedRelativePaths?.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

                // --- Clamp MaxParallelTasks before saving ---
                if (settings.MaxParallelTasks.HasValue)
                {
                    settings.MaxParallelTasks = Math.Clamp(settings.MaxParallelTasks.Value, 1, AppSettings.MaxAllowedParallelTasks);
                }
                else
                {
                    settings.MaxParallelTasks = AppSettings.DefaultMaxParallelTasks; // Save the default if null
                }


                // LastSuccessfulBackupUtc and MaxParallelTasks will be serialized if they have values
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save settings to {FilePath}", filePath);
                return false;
            }
        }
    }
}
