using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamUnlock;

public static class MigrationManager
{
    private const string RegistryKeyPath = @"Software\SteamUnlock";
    private const string MigrationVersionValue = "MigrationVersion";
    private static readonly string CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";

    /// <summary>
    /// Main entry point for migration system. Checks if migration is needed and performs it.
    /// Safe to call multiple times - idempotent.
    /// </summary>
    public static void CheckAndMigrate()
    {
        try
        {
            if (IsFirstRunAfterUpgrade())
            {
                MigrateFromLegacyVersion();
                MarkMigrationComplete();
            }
        }
        catch (Exception ex)
        {
            // Don't crash the app if migration fails, just log it
            Debug.WriteLine($"Migration failed: {ex.Message}");
            // Still mark as complete to avoid infinite retry
            MarkMigrationComplete();
        }
    }

    /// <summary>
    /// Checks if this is the first run after upgrade by comparing version in registry
    /// </summary>
    private static bool IsFirstRunAfterUpgrade()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            if (key == null)
            {
                // No registry key = first run ever OR old version without migration support
                return HasLegacyInstallation();
            }

            string? lastMigrated = key.GetValue(MigrationVersionValue)?.ToString();
            if (string.IsNullOrEmpty(lastMigrated))
            {
                return HasLegacyInstallation();
            }

            // Parse versions and compare
            if (Version.TryParse(lastMigrated, out var lastVersion) && 
                Version.TryParse(CurrentVersion, out var currentVersion))
            {
                // Migrate if current version is newer
                return currentVersion > lastVersion;
            }

            return false;
        }
        catch
        {
            // If we can't read registry, assume no migration needed
            return false;
        }
    }

    /// <summary>
    /// Detect if there's a legacy installation by checking for old Registry autostart entries
    /// </summary>
    private static bool HasLegacyInstallation()
    {
        string[] keyPaths = {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        };

        string[] possibleNames = { "SteamUnlock", "Steam Unlock" };

        foreach (var keyPath in keyPaths)
        {
            try
            {
                // Check HKCU
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key != null)
                {
                    foreach (var name in possibleNames)
                    {
                        if (key.GetValue(name) != null)
                            return true;
                    }

                    // Also check for any value containing SteamUnlock.exe
                    foreach (var valueName in key.GetValueNames())
                    {
                        var value = key.GetValue(valueName)?.ToString() ?? "";
                        if (value.Contains("SteamUnlock.exe", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { /* Ignore errors */ }
        }

        return false;
    }

    /// <summary>
    /// Perform migration from legacy version
    /// </summary>
    private static void MigrateFromLegacyVersion()
    {
        bool migrationPerformed = false;

        // Step 1: Cleanup legacy autostart (already implemented in AutostartManager)
        if (CleanupLegacyAutostart())
        {
            migrationPerformed = true;
        }

        // Step 2: Migrate autostart to Task Scheduler if it was enabled
        if (MigrateAutostart())
        {
            migrationPerformed = true;
        }

        // Step 3: Cleanup old WinDivert files from wrong locations
        if (CleanupLegacyWinDivert())
        {
            migrationPerformed = true;
        }

        // Step 4: Show notification if any migration was performed
        if (migrationPerformed)
        {
            MessageBox.Show(
                "SteamUnlock has been updated!\n\n" +
                "Your installation has been migrated to the new version.\n" +
                "- Autostart now uses Task Scheduler (more reliable)\n" +
                "- Old system entries have been cleaned up\n\n" +
                "Everything should work as before.",
                "Update Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    /// <summary>
    /// Clean up legacy Registry autostart entries
    /// </summary>
    private static bool CleanupLegacyAutostart()
    {
        bool cleaned = false;

        string[] keyPaths = {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        };

        string[] possibleNames = { "SteamUnlock", "Steam Unlock" };

        foreach (var keyPath in keyPaths)
        {
            // HKCU cleanup
            if (CleanupRegistryKey(Registry.CurrentUser, keyPath, possibleNames))
                cleaned = true;

            // HKLM cleanup (only works if running as admin)
            if (CleanupRegistryKey(Registry.LocalMachine, keyPath, possibleNames))
                cleaned = true;
        }

        return cleaned;
    }

    /// <summary>
    /// Helper to cleanup a specific registry key
    /// </summary>
    private static bool CleanupRegistryKey(RegistryKey root, string path, string[] names)
    {
        bool cleaned = false;
        try
        {
            using var key = root.OpenSubKey(path, true);
            if (key == null) return false;

            // Delete by name
            foreach (var name in names)
            {
                if (key.GetValue(name) != null)
                {
                    key.DeleteValue(name, false);
                    cleaned = true;
                }
            }

            // Scan for any value containing SteamUnlock.exe
            foreach (var valueName in key.GetValueNames())
            {
                var val = key.GetValue(valueName)?.ToString() ?? "";
                if (val.Contains("SteamUnlock.exe", StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(valueName, false);
                    cleaned = true;
                }
            }
        }
        catch { /* Ignore errors */ }

        return cleaned;
    }

    /// <summary>
    /// Migrate autostart from Registry to Task Scheduler
    /// </summary>
    private static bool MigrateAutostart()
    {
        // If Task Scheduler task already exists, no migration needed
        if (AutostartManager.IsEnabled())
            return false;

        // If there was a Registry autostart, enable Task Scheduler version
        // We already cleaned the Registry, so just enable the new method
        // Only do this if we detected legacy installation
        if (HasLegacyInstallation())
        {
            return AutostartManager.SetEnabled(true);
        }

        return false;
    }

    /// <summary>
    /// Cleanup old WinDivert driver and files from wrong locations or loaded state
    /// </summary>
    private static bool CleanupLegacyWinDivert()
    {
        bool cleaned = false;

        try
        {
            // Try to stop WinDivert service if it's running
            var stopResult = ExecuteCommand("sc", "stop WinDivert", hideWindow: true);
            if (stopResult.ExitCode == 0)
            {
                cleaned = true;
                // Wait a bit for service to stop
                System.Threading.Thread.Sleep(1000);
            }

            // Try to delete WinDivert service
            var deleteResult = ExecuteCommand("sc", "delete WinDivert", hideWindow: true);
            if (deleteResult.ExitCode == 0)
            {
                cleaned = true;
            }

            // Check common legacy locations for WinDivert files
            string[] legacyPaths = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "WinDivert64.sys"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WinDivert64.sys"),
                @"C:\Windows\System32\drivers\WinDivert64.sys",
                @"C:\Windows\System32\WinDivert64.sys"
            };

            foreach (var path in legacyPaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                        cleaned = true;
                    }
                    catch
                    {
                        // File might be in use or require reboot to delete
                        // Try to mark for deletion on reboot
                        MarkForDeletionOnReboot(path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinDivert cleanup failed: {ex.Message}");
        }

        return cleaned;
    }

    /// <summary>
    /// Mark a file for deletion on next reboot using MoveFileEx
    /// </summary>
    private static void MarkForDeletionOnReboot(string path)
    {
        try
        {
            // Use Windows API to mark file for deletion on reboot
            if (!NativeMethods.MoveFileEx(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
            {
                Debug.WriteLine($"Failed to mark {path} for deletion on reboot");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MarkForDeletionOnReboot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mark migration as complete by writing current version to registry
    /// </summary>
    private static void MarkMigrationComplete()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            key?.SetValue(MigrationVersionValue, CurrentVersion);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to mark migration complete: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute a command and return result
    /// </summary>
    private static (int ExitCode, string Output) ExecuteCommand(string fileName, string arguments, bool hideWindow = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = hideWindow,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return (-1, "");

            process.WaitForExit(5000); // 5 second timeout
            string output = process.StandardOutput.ReadToEnd();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }

    /// <summary>
    /// Native methods for file operations
    /// </summary>
    private static class NativeMethods
    {
        public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    }
}
