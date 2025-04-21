using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Serilog;

namespace GoogleDriveBackup.Core
{
    public static class GoogleDriveService
    {
        static readonly string[] Scopes = { DriveService.Scope.Drive };
        static readonly string ApplicationName = "Google Drive ZIP Backup Tool"; // Consistent name

        public static async Task<DriveService?> AuthenticateAsync()
        {
            Log.Information("Attempting Google Drive authentication...");
            UserCredential credential;
            // Store token relative to calling application data
            // Using AppData allows token reuse even if app location changes
            string credDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                           "GoogleDriveZipBackupTool", // Consistent identifier
                                           ".credentials");
            string credPath = Path.Combine(credDir, "drive-dotnet-token.json"); // Use a descriptive name

            try
            {
                // Ensure the directory exists
                Directory.CreateDirectory(credDir);
                Log.Debug("Credential directory: {CredDir}", credDir);

                // Look for secrets relative to calling application base directory
                string secretsPath = Path.Combine(AppContext.BaseDirectory, "client_secrets.json");
                Log.Debug("Looking for client secrets: {Path}", secretsPath);
                if (!File.Exists(secretsPath))
                {
                    Log.Error("Authentication failed: client_secrets.json not found: {Path}", secretsPath);
                    // Let the caller handle UI message
                    return null;
                }

                using (var stream = new FileStream(secretsPath, FileMode.Open, FileAccess.Read))
                {
                    Log.Debug("Requesting user authorization, credential path: {Path}", credPath);
                    credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.FromStream(stream).Secrets,
                        Scopes,
                        "user", // Identifier for the token store user
                        CancellationToken.None,
                        new FileDataStore(credDir, true)); // Store token file(s) in credDir
                }
                Log.Information("User credential obtained successfully. Token stored in: {CredDir}", credDir);

                var service = new DriveService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = ApplicationName,
                    // Consider adding exponential backoff for retries on API errors
                    // GacktMessageHandler = new Gackt.GacktMessageHandler(...),
                });
                Log.Information("DriveService created successfully.");
                return service;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred during Google Drive authentication.");
                // Let the caller handle UI message
                return null;
            }
        }
    }
}