using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace SteamUnlock;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Clean up legacy autostart from Registry FIRST
        // This runs even if we are not admin and about to exit
        AutostartManager.CleanupLegacy();

        if (!IsAdministrator())
        {
            MessageBox.Show("This application MUST be run as ADMINISTRATOR!", "Steam Unlock Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new TrayApplicationContext());
    }

    static bool IsAdministrator()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        return false;
    }
}

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _autostartMenuItem;
    private Process? _engineProcess;
    private readonly string _rootDir;
    private readonly string _binDir;
    private readonly string _listFile;
    private readonly string _exeFile;

    public TrayApplicationContext()
    {
        // Path resolution
        _rootDir = FindRootDir(AppDomain.CurrentDomain.BaseDirectory);
        _binDir = Path.Combine(_rootDir, "bin");
        _listFile = Path.Combine(_rootDir, "list.txt");
        _exeFile = Path.Combine(_binDir, "winws.exe");

        // Initialize Autostart Menu Item early to use in CreateContextMenu
        _autostartMenuItem = new ToolStripMenuItem("Run at Windows Startup", null, (s, e) => ToggleAutostart());
        _autostartMenuItem.Checked = AutostartManager.IsEnabled();

        // Initialize Tray Icon
        _trayIcon = new NotifyIcon()
        {
            Icon = SystemIcons.Shield, // Premium-ish shield icon for safety
            ContextMenuStrip = CreateContextMenu(),
            Visible = true,
            Text = "Steam Unlock Service"
        };

        _trayIcon.DoubleClick += (s, e) => StartEngine();

        // Auto-start engine
        StartEngine();
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        var startItem = new ToolStripMenuItem("Start Service", null, (s, e) => StartEngine());
        var stopItem = new ToolStripMenuItem("Stop Service", null, (s, e) => StopEngine());
        var settingsItem = new ToolStripMenuItem("Edit list.txt", null, (s, e) => OpenSettings());
        var updateItem = new ToolStripMenuItem("Check for Updates", null, async (s, e) => await CheckForUpdates());
        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Exit());

        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartMenuItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(updateItem);
        menu.Items.Add(exitItem);

        return menu;
    }

    private void StartEngine()
    {
        if (_engineProcess != null && !_engineProcess.HasExited)
        {
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Service is already running.", ToolTipIcon.Info);
            return;
        }

        if (!VerifyFiles()) return;

        // Flush DNS
        Process.Start(new ProcessStartInfo { FileName = "ipconfig", Arguments = "/flushdns", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();

        string arguments = $"--wf-tcp=80,443 --wf-udp=443,50000-65535 --hostlist=\"{_listFile}\" --dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6";

        try
        {
            _engineProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exeFile,
                    Arguments = arguments,
                    WorkingDirectory = _binDir,
                    UseShellExecute = false,
                    CreateNoWindow = true, // Force background
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                }
            };

            _engineProcess.Start();
            _trayIcon.Text = "Steam Unlock - Running";
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Service started successfully.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start engine: {ex.Message}", "Steam Unlock Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopEngine()
    {
        if (_engineProcess != null && !_engineProcess.HasExited)
        {
            _engineProcess.Kill(true);
            _engineProcess = null;
            _trayIcon.Text = "Steam Unlock - Stopped";
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Service stopped.", ToolTipIcon.Info);
        }
    }

    private void OpenSettings()
    {
        try
        {
            Process.Start("notepad.exe", _listFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open settings: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleAutostart()
    {
        bool newState = !_autostartMenuItem.Checked;
        if (AutostartManager.SetEnabled(newState))
        {
            _autostartMenuItem.Checked = newState;
            string status = newState ? "enabled" : "disabled";
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", $"Autostart {status}.", ToolTipIcon.Info);
        }
    }

    private async Task CheckForUpdates()
    {
        await UpdateChecker.CheckForUpdatesAsync();
    }

    private bool VerifyFiles()
    {
        string[] requiredFiles = { _exeFile, Path.Combine(_binDir, "WinDivert.dll"), Path.Combine(_binDir, "WinDivert64.sys"), _listFile };
        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                MessageBox.Show($"Required file missing: {file}", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        return true;
    }

    private string FindRootDir(string baseDir)
    {
        string? current = baseDir;
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current, "bin")) && File.Exists(Path.Combine(current, "list.txt")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return baseDir;
    }

    private void Exit()
    {
        StopEngine();
        _trayIcon.Visible = false;
        Application.Exit();
        Environment.Exit(0); // Ensure process dies completely for Task Scheduler
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}

public static class AutostartManager
{
    private const string TaskName = "SteamUnlockAutostart";
    private static readonly string ExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Query /TN \"{TaskName}\" /NH",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                // Create task: /RL HIGHEST for admin rights, /SC ONLOGON for startup, /IT for interactive
                // We use XML-less creation for simplicity via CLI
                string args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F";
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Delete /TN \"{TaskName}\" /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to modify autostart: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public static void CleanupLegacy()
    {
        string[] keys = {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        };

        string[] possibleNames = { "SteamUnlock", "Steam Unlock" };

        foreach (var subKeyPath in keys)
        {
            // Try HKCU
            CleanupKey(Registry.CurrentUser, subKeyPath, possibleNames);
            // Try HKLM (only works if running as admin, but we try anyway)
            CleanupKey(Registry.LocalMachine, subKeyPath, possibleNames);
        }
    }

    private static void CleanupKey(RegistryKey root, string path, string[] names)
    {
        try
        {
            using var key = root.OpenSubKey(path, true);
            if (key == null) return;

            // Delete by name
            foreach (var name in names)
            {
                if (key.GetValue(name) != null)
                {
                    key.DeleteValue(name, false);
                }
            }

            // Also scan for any value that contains our exe name as a fallback
            foreach (var valueName in key.GetValueNames())
            {
                var val = key.GetValue(valueName)?.ToString() ?? "";
                if (val.Contains("SteamUnlock.exe", StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(valueName, false);
                }
            }
        }
        catch { /* Squelch errors for non-admin or missing keys */ }
    }
}
