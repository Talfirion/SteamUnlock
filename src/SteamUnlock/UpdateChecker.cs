using System;
using System.Diagnostics;
using System.Globalization;
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
            Logger.Info("Checking for updates.");

            // Get current version
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion == null)
            {
                Logger.Warn("Unable to determine current version.");
                MessageBox.Show(Text.CurrentVersionUnknown, Text.UpdateCheckTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Fetch latest release from GitHub
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var release = JsonSerializer.Deserialize<GitHubRelease>(response);

            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                Logger.Warn("GitHub release response did not contain a tag.");
                MessageBox.Show(Text.UpdateInfoUnavailable, Text.UpdateCheckTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Logger.Info($"Latest GitHub release tag: {release.TagName}.");

            // Parse version from tag (e.g., "v1.0.0" -> "1.0.0")
            string versionString = release.TagName.TrimStart('v');
            if (!Version.TryParse(versionString, out var latestVersion))
            {
                Logger.Warn($"Unable to parse release tag: {release.TagName}.");
                MessageBox.Show(Text.VersionParseFailed(release.TagName), Text.UpdateCheckTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Compare versions
            if (latestVersion <= currentVersion)
            {
                Logger.Info($"No update available. Current: {currentVersion}, latest: {latestVersion}.");
                MessageBox.Show(Text.AlreadyLatest(currentVersion), Text.UpdateCheckTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Logger.Info($"Update available. Current: {currentVersion}, latest: {latestVersion}.");

            // New version available
            var result = MessageBox.Show(
                Text.UpdateAvailable(currentVersion, latestVersion, GetExternalZapretSummary()),
                Text.UpdateAvailableTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                await DownloadAndInstallUpdateAsync(release);
            }
            else
            {
                Logger.Info("User declined update installation.");
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"Failed to check for updates: {ex}");
            MessageBox.Show(Text.UpdateCheckFailed(ex.Message), Text.UpdateCheckErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            Logger.Error($"Unexpected update check error: {ex}");
            MessageBox.Show(Text.UpdateUnexpectedError(ex.Message), Text.UpdateCheckErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                Logger.Warn("No installer asset was found in the latest release.");
                MessageBox.Show(Text.NoInstallerAsset, Text.UpdateErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Logger.Info($"Downloading update asset: {installerAsset.Name}.");

            // Download installer to temp folder
            string tempPath = Path.Combine(Path.GetTempPath(), installerAsset.Name);

            using var statusForm = UpdateStatusForm.ShowStatus(Text.DownloadStatusTitle, Text.DownloadStatusMessage);

            var fileBytes = await _httpClient.GetByteArrayAsync(installerAsset.BrowserDownloadUrl);
            await File.WriteAllBytesAsync(tempPath, fileBytes);
            Logger.Info($"Downloaded update to {tempPath}. Size: {fileBytes.Length} bytes.");
            statusForm.SetStatus(Text.InstallStatusMessage);

            // Launch installer with silent flags
            var startInfo = new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            };

            Process.Start(startInfo);
            Logger.Info("Update installer launched. Exiting application.");

            // Exit the application to allow installer to update files
            Application.Exit();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to download or install update: {ex}");
            MessageBox.Show(Text.UpdateInstallFailed(ex.Message), Text.UpdateErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string GetExternalZapretSummary()
    {
        var ownEnginePath = FindOwnEnginePath();
        var processes = Process.GetProcesses()
            .Where(p => string.Equals(p.ProcessName, "winws", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.ProcessName, "winws2", StringComparison.OrdinalIgnoreCase))
            .Where(p => !IsSamePath(TryGetProcessPath(p), ownEnginePath))
            .Select(p => $"{p.ProcessName}:{p.Id}")
            .Take(4)
            .ToArray();

        return processes.Length == 0 ? "" : string.Join(", ", processes);
    }

    private static string FindOwnEnginePath()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, "bin", "winws.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "winws.exe");
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class UpdateStatusForm : Form
    {
        private readonly Label _label;

        private UpdateStatusForm(string title, string message)
        {
            Text = title;
            Width = 430;
            Height = 170;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;

            _label = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                Text = message
            };

            Controls.Add(_label);
        }

        public static UpdateStatusForm ShowStatus(string title, string message)
        {
            var form = new UpdateStatusForm(title, message);
            form.Show();
            form.Update();
            return form;
        }

        public void SetStatus(string message)
        {
            if (IsDisposed)
            {
                return;
            }

            _label.Text = message;
            Update();
        }
    }

    private static class Text
    {
        private static bool Russian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk";

        public static string UpdateCheckTitle => Russian ? "Проверка обновлений" : "Update Check";
        public static string UpdateCheckErrorTitle => Russian ? "Ошибка проверки обновлений" : "Update Check Error";
        public static string UpdateAvailableTitle => Russian ? "Доступно обновление" : "Update Available";
        public static string UpdateErrorTitle => Russian ? "Ошибка обновления" : "Update Error";
        public static string DownloadStatusTitle => Russian ? "Обновление Steam Unlock" : "Steam Unlock Update";

        public static string CurrentVersionUnknown => Russian
            ? "Не удалось определить текущую версию."
            : "Unable to determine current version.";

        public static string UpdateInfoUnavailable => Russian
            ? "Не удалось получить информацию об обновлении."
            : "Unable to fetch update information.";

        public static string NoInstallerAsset => Russian
            ? "В последнем релизе не найден установщик."
            : "No installer found in the latest release.";

        public static string DownloadStatusMessage => Russian
            ? "Идет загрузка обновления.\n\nЭто окно можно закрыть: загрузка и установка продолжатся в фоне.\nSteam Unlock перезапустится после установки."
            : "Downloading update.\n\nYou can close this window: download and installation will continue in the background.\nSteam Unlock will restart after installation.";

        public static string InstallStatusMessage => Russian
            ? "Загрузка завершена. Запускаем установщик.\n\nSteam Unlock будет временно закрыт и запущен снова после обновления."
            : "Download complete. Starting installer.\n\nSteam Unlock will temporarily close and restart after the update.";

        public static string VersionParseFailed(string tag) => Russian
            ? $"Не удалось разобрать версию из тега: {tag}"
            : $"Unable to parse version from tag: {tag}";

        public static string AlreadyLatest(Version version) => Russian
            ? $"У вас уже установлена последняя версия ({version})."
            : $"You are already using the latest version ({version}).";

        public static string UpdateAvailable(Version currentVersion, Version latestVersion, string externalZapretSummary)
        {
            var externalLine = string.IsNullOrWhiteSpace(externalZapretSummary)
                ? ""
                : Russian
                    ? $"\n\nОбнаружен внешний zapret/winws: {externalZapretSummary}.\nУстановщик не будет специально завершать zapret, но сетевое соединение может кратко прерваться, если Windows перезагрузит WinDivert."
                    : $"\n\nExternal zapret/winws detected: {externalZapretSummary}.\nThe installer will not intentionally terminate zapret, but connectivity may briefly drop if Windows reloads WinDivert.";

            return Russian
                ? $"Доступна новая версия.\n\nТекущая: {currentVersion}\nНовая: {latestVersion}{externalLine}\n\nSteam Unlock будет закрыт на время установки и запущен снова после обновления.\nОкно загрузки можно будет закрыть: это не прервет обновление.\n\nСкачать и установить обновление?"
                : $"A new version is available.\n\nCurrent: {currentVersion}\nLatest: {latestVersion}{externalLine}\n\nSteam Unlock will close during installation and restart after the update.\nYou can close the download window: it will not interrupt the update.\n\nDownload and install the update?";
        }

        public static string UpdateCheckFailed(string message) => Russian
            ? $"Не удалось проверить обновления:\n{message}\n\nПроверьте подключение к интернету."
            : $"Failed to check for updates:\n{message}\n\nPlease check your internet connection.";

        public static string UpdateUnexpectedError(string message) => Russian
            ? $"Неожиданная ошибка при проверке обновлений:\n{message}"
            : $"Unexpected error during update check:\n{message}";

        public static string UpdateInstallFailed(string message) => Russian
            ? $"Не удалось скачать или установить обновление:\n{message}"
            : $"Failed to download or install update:\n{message}";
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
