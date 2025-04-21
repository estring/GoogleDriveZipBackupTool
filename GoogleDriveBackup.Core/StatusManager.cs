using System;
using System.IO;
using System.Text.Json;
using Serilog; // For logging within the manager

namespace GoogleDriveBackup.Core
{
    public static class StatusManager // Make static like SettingsManager
    {
        private const string STATUS_FILENAME = "backup_status.json";

        // Status file stored alongside the calling application's executable
        private static string GetStatusFilePath()
        {
            return Path.Combine(AppContext.BaseDirectory, STATUS_FILENAME);
        }

        public static BackupStatus LoadBackupStatus()
        {
            string statusFilePath = GetStatusFilePath();
            if (File.Exists(statusFilePath))
            {
                try
                {
                    string json = File.ReadAllText(statusFilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true }; // Be tolerant
                    var status = JsonSerializer.Deserialize<BackupStatus>(json, options);
                    if (status != null)
                    {
                        Log.Information("Backup status loaded from {StatusFilePath}", statusFilePath);
                        return status;
                    }
                    Log.Warning("Could not deserialize backup status file {StatusFilePath}. Returning default.", statusFilePath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not load backup status file {StatusFilePath}. Returning default.", statusFilePath);
                }
            }
            Log.Information("Backup status file not found or invalid ({StatusFilePath}). Assuming no previous backup.", statusFilePath);
            return new BackupStatus { LastSuccessfulBackupTimestamp = null }; // Default status
        }

        public static void SaveBackupStatus(BackupStatus status)
        {
            string statusFilePath = GetStatusFilePath();
            try
            {
                string json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(statusFilePath, json);
                Log.Information("Backup status updated in {StatusFilePath}", statusFilePath);
            }
            catch (Exception ex)
            {
                // Log error, but don't crash the app just because status couldn't save
                Log.Error(ex, "Could not save backup status file {StatusFilePath}", statusFilePath);
            }
        }
    }
}