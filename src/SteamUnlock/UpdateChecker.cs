using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamUnlock;

public static class UpdateChecker
{
    private const string GitHubApiUrl = "https://api.github.com/repos/Talfirion/SteamUnlock/releases/latest";
    private static readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "SteamUnlock-Updater" } }
    };

    public static async Task CheckForUpdatesAsync()
    {
        try
        {
            // Get current version
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion == null)
            {
                MessageBox.Show("Unable to determine current version.", "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Fetch latest release from GitHub
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response);

            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                MessageBox.Show("Unable to fetch update information.", "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Parse version from tag (e.g., "v1.0.0" -> "1.0.0")
            string versionString = release.TagName.TrimStart('v');
            if (!Version.TryParse(versionString, out var latestVersion))
            {
                MessageBox.Show($"Unable to parse version from tag: {release.TagName}", "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Compare versions
            if (latestVersion <= currentVersion)
            {
                MessageBox.Show($"You are already using the latest version ({currentVersion}).", "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // New version available
            var result = MessageBox.Show(
                $"New version available!\n\nCurrent: {currentVersion}\nLatest: {latestVersion}\n\nDo you want to download and install the update?",
                "Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                await DownloadAndInstallUpdateAsync(release);
            }
        }
        catch (HttpRequestException ex)
        {
            MessageBox.Show($"Failed to check for updates:\n{ex.Message}\n\nPlease check your internet connection.", "Update Check Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unexpected error during update check:\n{ex.Message}", "Update Check Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task DownloadAndInstallUpdateAsync(GitHubRelease release)
    {
        try
        {
            // Find the installer asset (.exe file)
            var installerAsset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (installerAsset == null || string.IsNullOrEmpty(installerAsset.BrowserDownloadUrl))
            {
                MessageBox.Show("No installer found in the latest release.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Download installer to temp folder
            string tempPath = Path.Combine(Path.GetTempPath(), installerAsset.Name);
            
            MessageBox.Show("Downloading update...\n\nThis may take a moment.", "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);

            var fileBytes = await _httpClient.GetByteArrayAsync(installerAsset.BrowserDownloadUrl);
            await File.WriteAllBytesAsync(tempPath, fileBytes);

            // Launch installer with silent flags
            var startInfo = new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/VERYSILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            };

            Process.Start(startInfo);

            // Exit the application to allow installer to update files
            Application.Exit();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to download or install update:\n{ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // DTO for GitHub Release API response
    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
