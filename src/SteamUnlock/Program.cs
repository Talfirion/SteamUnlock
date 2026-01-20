using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace SteamUnlock;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

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
        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Exit());

        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
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
