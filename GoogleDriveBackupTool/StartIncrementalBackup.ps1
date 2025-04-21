<#
.SYNOPSIS
Runs GoogleDriveZipBackupTool.exe for a backup action, automatically finding the
newest existing backup file in a specified directory and using it for the 'previous'
argument for incremental backups.

.DESCRIPTION
This script configures paths for the backup tool and the backup storage location.
It searches the backup directory for files matching a specific pattern (e.g., GDriveBackup_*.zip),
sorts them by modification date (newest first), and selects the newest file.
If a newest file is found, it's added as the 'previous=' argument when calling
GoogleDriveZipBackupTool.exe with 'action=backup'. Otherwise, a full backup is initiated.
Allows specifying additional command-line arguments for the tool.
Reports the success or failure based on the application's exit code.

.NOTES
Author: [Torsten Kelm]
Date:   2024-05-16
Version: 1.0

Requires PowerShell 3.0 or later (for $PSScriptRoot).
Ensure the PowerShell Execution Policy allows running local scripts.
(e.g., Set-ExecutionPolicy RemoteSigned -Scope CurrentUser)
#>

# ============================================================================
# PowerShell Script to run GoogleDriveZipBackupTool with the newest backup as previous
# ============================================================================

# --- Configuration - PLEASE EDIT THESE VALUES ---

# Set the directory where the application executable is located.
# $PSScriptRoot is the directory where the script itself is located.
$appDir = $PSScriptRoot
$appExe = "GoogleDriveZipBackupTool.exe"
$appPath = Join-Path -Path $appDir -ChildPath $appExe

# !!! IMPORTANT: Set this to the EXACT path where your backups are saved !!!
# This MUST match the 'LocalBackupArchivePath' in your app settings or profile.
$backupDir = "D:\bck_path"

# The naming pattern of your backup files. Adjust if necessary.
$backupPattern = "GDriveBackup_*.zip"

# Optional: Add any other command-line arguments here as a single string.
# They will be split and passed to the executable. Use quotes within the string if a value has spaces.
# Example: $additionalArgs = 'verbose=true settings="C:\My Configs\backup_profile.settings.json"'
$additionalArgs = 'settings="[replace_with_your_settings]" runIfDue=true'

# --- End Configuration ---

Write-Host "Starting Backup Script..." -ForegroundColor Cyan
Write-Host "Application Path: `"$appPath`""
Write-Host "Backup Directory: `"$backupDir`""
Write-Host "Backup Pattern: `"$backupPattern`""
if ($additionalArgs) { Write-Host "Additional Args: $additionalArgs" }
Write-Host ""

# --- Check if Application Exists ---
if (-not (Test-Path -Path $appPath -PathType Leaf)) {
    Write-Error "Application executable not found at specified path: `"$appPath`""
    Write-Error "Please check the \$appDir and \$appExe variables in this script."
    exit 1 # Exit with error code 1
}

# --- Check if Backup Directory Exists ---
if (-not (Test-Path -Path $backupDir -PathType Container)) {
    Write-Error "Backup directory not found!"
    Write-Error "Please check the \$backupDir variable in this script."
    Write-Error "Path checked: `"$backupDir`""
    exit 1 # Exit with error code 1
}

# --- Find the Newest Backup File ---
Write-Host "Searching for the newest backup file matching `"$backupPattern`"..." -ForegroundColor Yellow
$previousArgument = ""
$newestBackup = Get-ChildItem -Path $backupDir -Filter $backupPattern -File |
                Sort-Object -Property LastWriteTime -Descending |
                Select-Object -First 1

if ($null -ne $newestBackup) {
    Write-Host "Found newest backup file: `"$($newestBackup.Name)`"" -ForegroundColor Green
    # Ensure the full path is quoted correctly for the argument
    $previousArgument = "previous=`"$($newestBackup.FullName)`""
    Write-Host "Using this file for the 'previous' argument."
} else {
    Write-Host "No previous backup files found matching `"$backupPattern`"." -ForegroundColor Yellow
    Write-Host "Performing a FULL backup (no 'previous' argument will be used)."
}
Write-Host ""

# --- Construct and Execute the Command ---
Write-Host "Preparing command..." -ForegroundColor Cyan

# Start building the argument list for the executable
$arguments = @("action=backup") # Start with mandatory action

# Add 'previous' argument if found
if ($previousArgument) {
    $arguments += $previousArgument
}

# Add any additional user-defined arguments
# Simple split by space - might need more robust parsing if args values contain spaces *without* internal quotes
if ($additionalArgs) {
    $arguments += ($additionalArgs -split ' ') # Add each part as a separate argument
}

$commandStringForDisplay = "`"$appPath`" $($arguments -join ' ')"
Write-Host "Executing: $commandStringForDisplay"
Write-Host ""
Write-Host "--- Application Output Starts ---" -ForegroundColor Gray

# Execute the application
try {
    & $appPath $arguments
    $scriptExitCode = $LASTEXITCODE # Capture the exit code from the application
} catch {
    Write-Error "An error occurred while trying to execute the application:"
    Write-Error $_.Exception.Message
    $scriptExitCode = -1 # Indicate script-level execution error
}

Write-Host "--- Application Output Ends ---" -ForegroundColor Gray
Write-Host ""

# --- Report Script Result based on Exit Code ---
Write-Host "--- Script Finished ---" -ForegroundColor Cyan
if ($scriptExitCode -eq 0) {
    Write-Host "Backup command completed successfully (Application Exit Code: 0)." -ForegroundColor Green
} elseif ($scriptExitCode -eq 2) {
     Write-Host "Backup command was cancelled by user (Application Exit Code: 2)." -ForegroundColor Yellow
} elseif ($scriptExitCode -eq -1) {
     Write-Host "Script failed to execute the application." -ForegroundColor Red
     # Error already written in catch block
} else {
    Write-Host "Backup command failed or completed with errors (Application Exit Code: $scriptExitCode)." -ForegroundColor Red
    Write-Host "Check console output above and application logs for details."
}

#Wait for Enter to be pressed before exiting
read-host “Press ENTER to continue...”

# Exit the script with the application's exit code (or script error code)
exit $scriptExitCode