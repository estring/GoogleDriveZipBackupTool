# Google Drive ZIP Backup Tool

[![License: LGPL v2.1](https://img.shields.io/badge/License-LGPL_v2.1-blue.svg)](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.en.html)
<!-- Add other badges if applicable (e.g., build status) -->

A robust and flexible tool built with C# and .NET for creating reliable, local backups of your Google Drive data into standard ZIP archives. Features incremental backups, Google Workspace file export, archive repair, resumable restores, parallel processing, and automation capabilities.

## Table of Contents

- [Motivation](#motivation)
- [Features](#features)
- [Technology Stack](#technology-stack)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Google Cloud Setup (IMPORTANT)](#google-cloud-setup-important)
  - [Building the Project (Optional)](#building-the-project-optional)
  - [Configuration](#configuration)
- [Usage](#usage)
  - [Interactive Mode](#interactive-mode)
  - [Command-Line Interface (CLI)](#command-line-interface-cli)
  - [PowerShell Automation Script](#powershell-automation-script)
- [Project Structure](#project-structure)
- [Detailed Articles](#detailed-articles)
- [Contributing](#contributing)
- [License](#license)

## Motivation

While Google Drive offers excellent cloud storage, relying solely on it presents risks like accidental deletion, account issues, ransomware, or simply needing an offline, independent copy. Existing solutions often lack specific features, and Google's native "download folder as ZIP" can be unreliable, especially with deep folder structures or long filenames, sometimes resulting in corrupted archives.

This tool was created to address these needs by providing:

-   **Data Sovereignty:** Local backups in the universal ZIP format.
-   **Reliability:** Custom archive creation avoiding long path issues by using a flat structure and manifest file.
-   **Efficiency:** Incremental backups to minimize download time and bandwidth.
-   **Resilience:** Mechanisms to resume interrupted restores and attempt repairs on damaged archives.
-   **Flexibility:** Control via interactive menus or command-line arguments for automation.

## Features

-   **Local ZIP Backups:** Creates standard `.zip` files on your local machine or network storage.
-   **Flat Archive Structure:** Stores files directly in the ZIP root using their Google Drive File ID as the filename, avoiding long path issues and ensuring reliability.
-   **Manifest File (`_manifest.json`):** Embedded within each ZIP, mapping File IDs back to original paths, names, modification times, and sizes. Crucial for incremental backups and restores.
-   **Incremental Backups:** Uses the manifest from a previous backup (`previous=` argument) to compare modification times and download only new or changed files.
-   **Google Workspace Export:** Automatically exports Google Docs, Sheets, Slides, and Drawings to standard formats (e.g., `.docx`, `.xlsx`, `.pptx`, `.png`).
-   **Archive Repair:** Attempts to fix incomplete backups by identifying missing files (via manifest) and re-downloading them from Google Drive using their File ID.
-   **Resumable Restores:** If a restore operation is interrupted, it can be resumed later, skipping files that were already successfully uploaded (using `_restore_state.json`).
-   **Parallel Processing:** Leverages multiple threads (`MaxParallelTasks` setting) to speed up downloads and uploads.
-   **Configurable:** Settings managed via `app_settings.json` (paths, Drive IDs, exclusions, performance). Supports loading/saving settings profiles.
-   **Exclusion Rules:** Define relative folder paths to exclude from backups.
-   **Dual Interface:**
    -   **Interactive Console Menu:** Easy-to-use menu for manual operations.
    -   **Command-Line Interface (CLI):** Powerful arguments for scripting and automation (`action=`, `settings=`, `previous=`, `input=`, `runIfDue=`, etc.).
-   **PowerShell Automation:** Includes a script (`Run-IncrementalBackup.ps1`) to automatically find the latest backup and run the tool incrementally.
-   **Logging:** Detailed logging using Serilog to console and rolling log files (`logs/log-.txt`).

## Technology Stack

-   **Language:** C#
-   **Framework:** .NET (e.g., .NET 6, 7, or 8 - specify if known)
-   **Google API:** Google Drive API v3 (`Google.Apis.Drive.v3`)
-   **Authentication:** Google OAuth 2.0 (`Google.Apis.Auth`)
-   **JSON Handling:** `System.Text.Json`
-   **Logging:** Serilog
-   **ZIP Handling:** `System.IO.Compression`
-   **Automation:** PowerShell (for the helper script)

## Getting Started

### Prerequisites

1.  **.NET SDK:** Install the appropriate .NET SDK (6.0 or later recommended) from [Microsoft](https://dotnet.microsoft.com/download).
2.  **PowerShell:** Required for the automation script (usually included with Windows). Version 3.0+ needed for `$PSScriptRoot`.
3.  **Google Account:** You need a Google account to access Google Drive and set up API access.

### Google Cloud Setup (IMPORTANT)

To use this tool, you **must** enable the Google Drive API and obtain OAuth 2.0 credentials:

1.  **Create/Select Project:** Go to the [Google Cloud Console](https://console.cloud.google.com/). Create a new project or select an existing one.
2.  **Enable Drive API:** Navigate to "APIs & Services" -> "Library". Search for "Google Drive API" and enable it for your project.
3.  **Configure OAuth Consent Screen:** Go to "APIs & Services" -> "OAuth consent screen".
    -   Choose "External" user type unless you have a Google Workspace account and only need internal access.
    -   Fill in the required app name, user support email, and developer contact information. You can skip scopes for now. Add your Google account email as a Test User during development/testing if needed.
4.  **Create Credentials:** Go to "APIs & Services" -> "Credentials".
    -   Click "+ CREATE CREDENTIALS" -> "OAuth client ID".
    -   Select "Desktop app" as the Application type.
    -   Give it a name (e.g., "Drive Backup Tool Client").
    -   Click "Create".
5.  **Download Credentials:** A pop-up will show your Client ID and Client Secret. Click "**DOWNLOAD JSON**".
6.  **Rename and Place:** Rename the downloaded file to exactly `client_secrets.json` and place it in the **same directory** as the `GoogleDriveZipBackupTool.exe` executable. **Treat this file securely - do not commit it to public repositories!**

### Building the Project (Optional)

If you clone the repository, you can build the executable yourself:

1.  Clone the repository: `git clone <repository-url>`
2.  Navigate to the solution directory: `cd GoogleDriveZipBackupTool` (or similar)
3.  Build the solution: `dotnet build -c Release`
4.  The executable will typically be in `GoogleDriveZipBackupTool/bin/Release/netX.Y/` (where `netX.Y` is your .NET version). Remember to place `client_secrets.json` alongside the `.exe`.

### Configuration

1.  **`client_secrets.json`:** Must be placed next to the executable (see Google Cloud Setup).
2.  **`app_settings.json`:** This file is created/managed by the application in the executable's directory. You can configure it via the Interactive Mode (Option 4) or by editing it directly. Key settings:
    *   `GoogleDriveFolderId`: The ID of the Google Drive folder you want to back up.
    *   `LocalBackupArchivePath`: The *directory* where backup ZIP files will be saved.
    *   `LocalTempWorkPath`: A directory for temporary files during backup/restore/repair.
    *   `GoogleDriveRestoreParentId`: The ID of the Drive folder where restores will be placed.
    *   `MaxParallelTasks`: Number of parallel downloads/uploads (e.g., 1-10).
    *   `ExcludedRelativePaths`: A list of relative paths (e.g., `["/Temporary Files", "/Cache"]`) to exclude.
    *   `runIfDue`: (Used by CLI/Script) If true, checks `LastSuccessfulBackupUtc` before running.
    *   `LastSuccessfulBackupUtc`: Timestamp updated automatically after successful backups.

## Usage

### Interactive Mode

-   Run `GoogleDriveZipBackupTool.exe` without any command-line arguments.
-   Follow the on-screen menu prompts to:
    -   Create backups (full or incremental).
    -   Restore from archives.
    -   Resume interrupted restores.
    -   Repair damaged archives.
    -   Configure settings.
    -   Manage exclusions.
    -   Load/Save settings profiles.

### Command-Line Interface (CLI)

Execute `GoogleDriveZipBackupTool.exe` with arguments. The `action=` argument is required.

**Common Examples:**

-   **Full Backup (using default settings):**
    ```bash
    .\GoogleDriveZipBackupTool.exe action=backup
    ```
-   **Incremental Backup (using specific settings profile & latest backup):**
    *(Use the PowerShell script below for easier incremental automation)*
    ```bash
    .\GoogleDriveZipBackupTool.exe action=backup settings="C:\MySettings\Work.settings.json" previous="D:\Backups\GDriveBackup_20240515_180000.zip"
    ```
-   **Conditional Incremental Backup (runs only if due):**
    ```bash
    .\GoogleDriveZipBackupTool.exe action=backup runIfDue=true parallelTasks=4
    ```
-   **Restore:**
    ```bash
    .\GoogleDriveZipBackupTool.exe action=restore input="D:\Backups\GDriveBackup_20240516_100000.zip"
    ```
-   **Resume Restore:**
    ```bash
    .\GoogleDriveZipBackupTool.exe action=resume-restore resume="C:\Temp\restore_extract_abc123"
    ```
-   **Repair:**
    ```bash
    .\GoogleDriveZipBackupTool.exe action=repair input="D:\Backups\GDriveBackup_PossiblyDamaged.zip"
    ```

### PowerShell Automation Script

The repository includes `Run-IncrementalBackup.ps1` to simplify automated incremental backups.

1.  **Purpose:** Automatically finds the most recent `GDriveBackup_*.zip` file in your backup directory and passes it as the `previous=` argument to `GoogleDriveZipBackupTool.exe`.
2.  **Configuration:** **YOU MUST EDIT** the configuration variables at the top of the script:
    *   `$backupDir`: Set this to the *exact* same path as `LocalBackupArchivePath` in your `app_settings.json` or profile.
    *   `$backupPattern`: Adjust if your backup files have a different naming scheme.
    *   `$additionalArgs`: Add other arguments like `settings="..."`, `runIfDue=true`, `parallelTasks=X`, etc.
3.  **Execution Policy:** You may need to adjust PowerShell's execution policy: `Set-ExecutionPolicy RemoteSigned -Scope CurrentUser` (run PowerShell as Admin once).
4.  **Running:** Open PowerShell, `cd` to the script's directory, and run `.\Run-IncrementalBackup.ps1`.
5.  **Scheduling:** Use Windows Task Scheduler to run the `.ps1` script regularly for fully automated incremental backups. (See Part 3 article for details).

## Project Structure

-   **`GoogleDriveBackup.Core/`**: The class library containing all core logic (API interaction, backup/restore/repair processes, manifest handling, etc.). UI-independent. **Licensed under LGPL 2.1.**
-   **`GoogleDriveZipBackupTool/`**: The .NET Console Application project. References `GoogleDriveBackup.Core`. Provides the interactive menu and handles CLI arguments. Acts as the orchestrator for the Core library. **Also licensed under LGPL 2.1.**
-   **`Run-IncrementalBackup.ps1`**: PowerShell script for automating incremental backups. (Typically considered separate, but you might want to clarify its license if different, otherwise assume it falls under the project license).

## Detailed Articles

For a deeper dive into the design decisions and implementation details, please refer to the articles published on [TK Softwareprojekte (Google Site)](https://sites.google.com/view/tk-softwareprojekte/home):

-   **Part 1: The Core Library:** Focuses on the `GoogleDriveBackup.Core` architecture and implementation.
-   **Part 2: The Console UI:** Describes how the console application uses the Core library.
-   **Part 3: Automating Incremental Backups with PowerShell:** Explains the PowerShell automation script.

## Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues for bugs, feature requests, or improvements. Note that contributions will be licensed under LGPL 2.1 as per the project license.
<!-- Add more specific contribution guidelines if desired -->

## License

This project is licensed under the **GNU Lesser General Public License v2.1**. See the [LICENSE](LICENSE) file for details. You can also find the full text of the license [here](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.en.html).
