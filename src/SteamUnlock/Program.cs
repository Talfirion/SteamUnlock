using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace SteamUnlock;

static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        _singleInstanceMutex = CreateSingleInstanceMutex(out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Steam Unlock is already running.", "Steam Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Clean up legacy autostart from Registry FIRST
        // This runs even if we are not admin and about to exit
        AutostartManager.CleanupLegacy();

        if (!IsAdministrator())
        {
            MessageBox.Show("This application MUST be run as ADMINISTRATOR!", "Steam Unlock Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Perform one-time migration from old versions
        MigrationManager.CheckAndMigrate();

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
    }

    private static Mutex CreateSingleInstanceMutex(out bool isFirstInstance)
    {
        try
        {
            return new Mutex(true, @"Global\SteamUnlock.SingleInstance", out isFirstInstance);
        }
        catch (UnauthorizedAccessException)
        {
            return new Mutex(true, @"Local\SteamUnlock.SingleInstance", out isFirstInstance);
        }
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
    private readonly string _engineArgsFile;
    private readonly string _coexistEngineArgsFile;
    private readonly string _exeFile;
    private bool _diagnosticsRunning;

    public TrayApplicationContext()
    {
        // Path resolution
        _rootDir = FindRootDir(AppDomain.CurrentDomain.BaseDirectory);
        _binDir = Path.Combine(_rootDir, "bin");
        _listFile = Path.Combine(_rootDir, "list.txt");
        _engineArgsFile = Path.Combine(_rootDir, "engine_args.txt");
        _coexistEngineArgsFile = Path.Combine(_rootDir, "engine_args_coexist.txt");
        _exeFile = Path.Combine(_binDir, "winws.exe");
        Logger.Initialize(_rootDir);
        Logger.Info("Application starting.");
        EnsureDefaultEngineArgsFiles();

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
        var engineArgsItem = new ToolStripMenuItem("Edit engine_args.txt", null, (s, e) => OpenEngineArgs());
        var coexistEngineArgsItem = new ToolStripMenuItem("Edit engine_args_coexist.txt", null, (s, e) => OpenCoexistEngineArgs());
        var diagnosticsItem = new ToolStripMenuItem("Run Diagnostics", null, async (s, e) => await RunDiagnosticsAsync());
        var logsItem = new ToolStripMenuItem("Open Logs Folder", null, (s, e) => OpenLogsFolder());
        var updateItem = new ToolStripMenuItem("Check for Updates", null, async (s, e) => await CheckForUpdates());
        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Exit());

        menu.Items.Add(startItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartMenuItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(engineArgsItem);
        menu.Items.Add(coexistEngineArgsItem);
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(updateItem);
        menu.Items.Add(exitItem);

        return menu;
    }

    private void StartEngine()
    {
        if (_engineProcess != null && !_engineProcess.HasExited)
        {
            Logger.Info("Start requested, but engine is already running.");
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Service is already running.", ToolTipIcon.Info);
            return;
        }

        if (!VerifyFiles()) return;

        if (TryFindRunningOwnEngine(out var runningEnginePid))
        {
            Logger.Warn($"Start requested, but Steam Unlock engine is already running as PID {runningEnginePid}.");
            _trayIcon.Text = "Steam Unlock - Running";
            _trayIcon.ShowBalloonTip(3000, "Steam Unlock", "Steam Unlock engine is already running.", ToolTipIcon.Info);
            return;
        }

        var useCoexistenceProfile = HasExternalZapretEngine(out var externalEngineSummary);
        if (useCoexistenceProfile)
        {
            Logger.Warn($"External zapret/winws process detected ({externalEngineSummary}). Using coexistence profile.");
            _trayIcon.ShowBalloonTip(
                4000,
                "Steam Unlock",
                "External zapret/winws detected. Starting with coexistence profile.",
                ToolTipIcon.Info);
        }

        // Flush DNS
        Logger.Info("Flushing DNS cache.");
        using (var flushProcess = Process.Start(new ProcessStartInfo { FileName = "ipconfig", Arguments = "/flushdns", CreateNoWindow = true, UseShellExecute = false }))
        {
            flushProcess?.WaitForExit();
            Logger.Info($"DNS flush exit code: {flushProcess?.ExitCode.ToString() ?? "unknown"}.");
        }

        string arguments = LoadEngineArguments(useCoexistenceProfile);
        var profileName = useCoexistenceProfile ? Path.GetFileName(_coexistEngineArgsFile) : Path.GetFileName(_engineArgsFile);
        Logger.Info($"Starting engine with {profileName}: \"{_exeFile}\" {arguments}");

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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            _engineProcess.EnableRaisingEvents = true;
            _engineProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Engine(e.Data); };
            _engineProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Logger.EngineError(e.Data); };
            _engineProcess.Exited += (_, _) =>
            {
                try
                {
                    Logger.Warn($"Engine exited with code {_engineProcess?.ExitCode.ToString() ?? "unknown"}.");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Engine exited. Exit code unavailable: {ex.Message}");
                }
            };

            _engineProcess.Start();
            _engineProcess.BeginOutputReadLine();
            _engineProcess.BeginErrorReadLine();
            _trayIcon.Text = "Steam Unlock - Running";
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Service started successfully.", ToolTipIcon.Info);
            Logger.Info($"Engine started. PID: {_engineProcess.Id}.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start engine: {ex}");
            MessageBox.Show($"Failed to start engine: {ex.Message}", "Steam Unlock Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopEngine()
    {
        if (_engineProcess != null && !_engineProcess.HasExited)
        {
            Logger.Info($"Stopping engine. PID: {_engineProcess.Id}.");
            _engineProcess.Kill(true);
            _engineProcess = null;
            _trayIcon.Text = "Steam Unlock - Stopped";
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Service stopped.", ToolTipIcon.Info);
            Logger.Info("Engine stopped.");
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

    private void OpenEngineArgs()
    {
        try
        {
            EnsureDefaultEngineArgsFiles();
            Process.Start("notepad.exe", _engineArgsFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open engine args: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenCoexistEngineArgs()
    {
        try
        {
            EnsureDefaultEngineArgsFiles();
            Process.Start("notepad.exe", _coexistEngineArgsFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open coexistence engine args: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        Logger.Info("Update check requested.");
        await UpdateChecker.CheckForUpdatesAsync();
    }

    private async Task RunDiagnosticsAsync()
    {
        if (_diagnosticsRunning)
        {
            _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Diagnostics are already running.", ToolTipIcon.Info);
            return;
        }

        _diagnosticsRunning = true;
        _trayIcon.ShowBalloonTip(2000, "Steam Unlock", "Diagnostics started. This may take a minute.", ToolTipIcon.Info);
        Logger.Info("Diagnostics started.");

        try
        {
            await LogCommandAsync("ipconfig", "/all", 15000);
            await LogCommandAsync("route", "print", 15000);

            foreach (var domain in GetDiagnosticDomains())
            {
                await DiagnoseDomainAsync(domain);
            }

            Logger.Info("Diagnostics finished.");
            MessageBox.Show(
                $"Diagnostics finished.\n\nLog file:\n{Logger.LogFilePath}",
                "Steam Unlock Diagnostics",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Logger.Error($"Diagnostics failed: {ex}");
            MessageBox.Show($"Diagnostics failed: {ex.Message}", "Steam Unlock Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _diagnosticsRunning = false;
        }
    }

    private static string[] GetDiagnosticDomains()
    {
        return new[]
        {
            "steamcommunity.com",
            "api.curseforge.com",
            "files.minecraftforge.net",
            "maven.minecraftforge.net",
            "wormhole.app",
            "gateway.ea.com",
            "r2-pc.stryder.respawn.com",
            "r2-pc-stats.stryder.respawn.com"
        };
    }

    private static async Task DiagnoseDomainAsync(string domain)
    {
        Logger.Info($"Diagnostic domain: {domain}");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(domain).WaitAsync(TimeSpan.FromSeconds(10));
            Logger.Info($"{domain}: DNS -> {FormatAddresses(addresses)}");
        }
        catch (Exception ex)
        {
            Logger.Error($"{domain}: DNS failed: {ex.Message}");
            return;
        }

        await TestTcpConnectAsync(domain, 443);

        foreach (var address in addresses.Take(6))
        {
            await TestTcpConnectAsync(address, 443);
        }

        var traceAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (traceAddress != null)
        {
            await LogCommandAsync("tracert", $"-d -h 12 -w 1000 {traceAddress}", 20000);
        }
    }

    private static async Task TestTcpConnectAsync(string host, int port)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var client = new TcpClient();
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(8));
            stopwatch.Stop();
            Logger.Info($"{host}:{port}: TCP connect OK in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Logger.Error($"{host}:{port}: TCP connect failed: {ex.Message}");
        }
    }

    private static async Task TestTcpConnectAsync(IPAddress address, int port)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(8));
            stopwatch.Stop();
            Logger.Info($"{address}:{port}: TCP connect OK in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Logger.Error($"{address}:{port}: TCP connect failed: {ex.Message}");
        }
    }

    private static string FormatAddresses(IPAddress[] addresses)
    {
        return addresses.Length == 0 ? "no addresses" : string.Join(", ", addresses.Select(a => a.ToString()));
    }

    private static async Task LogCommandAsync(string fileName, string arguments, int timeoutMs)
    {
        Logger.Info($"Command: {fileName} {arguments}");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit(timeoutMs));

            if (!exited)
            {
                process.Kill(true);
                Logger.Warn($"Command timed out after {timeoutMs} ms: {fileName} {arguments}");
                return;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Logger.Info($"Command exit code: {process.ExitCode}");
            Logger.CommandOutput(fileName, stdout, stderr);
        }
        catch (Exception ex)
        {
            Logger.Error($"Command failed: {fileName} {arguments}: {ex.Message}");
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Logger.LogDirectory);
            Process.Start("explorer.exe", Logger.LogDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open logs folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool VerifyFiles()
    {
        string[] requiredFiles = { _exeFile, Path.Combine(_binDir, "WinDivert.dll"), Path.Combine(_binDir, "WinDivert64.sys"), _listFile };
        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                Logger.Error($"Required file missing: {file}");
                MessageBox.Show($"Required file missing: {file}", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        return true;
    }

    private void EnsureDefaultEngineArgsFiles()
    {
        EnsureEngineArgsFile(_engineArgsFile, GetDefaultEngineArguments());
        EnsureEngineArgsFile(_coexistEngineArgsFile, GetDefaultCoexistEngineArguments());
    }

    private static void EnsureEngineArgsFile(string path, string defaultContents)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, defaultContents);
        }
        catch
        {
            // Use the built-in default if the file cannot be created.
        }
    }

    private string LoadEngineArguments(bool useCoexistenceProfile)
    {
        var parts = new List<string>();
        var argsFile = useCoexistenceProfile ? _coexistEngineArgsFile : _engineArgsFile;
        var defaultArguments = useCoexistenceProfile ? GetDefaultCoexistEngineArguments() : GetDefaultEngineArguments();

        try
        {
            if (File.Exists(argsFile))
            {
                AddArgumentLines(File.ReadLines(argsFile), parts);
            }
        }
        catch
        {
            parts.Clear();
        }

        if (parts.Count == 0)
        {
            AddArgumentLines(defaultArguments.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.None), parts);
        }

        var arguments = string.Join(" ", parts);
        return arguments
            .Replace("{LIST}", _listFile, StringComparison.OrdinalIgnoreCase)
            .Replace("{BIN}", _binDir, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddArgumentLines(IEnumerable<string> lines, List<string> parts)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add(line);
        }
    }

    private static string GetDefaultEngineArguments()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Lines starting with # are ignored. {LIST} and {BIN} are replaced at runtime.",
            "--wf-tcp=80,443,1024-1124,9960-9969,18000,18060,18120,27900,28910,29900",
            "--wf-udp=443,3478-3480,3659,1024-1124,18000,29900,50000-65535",
            "--filter-tcp=80 --hostlist=\"{LIST}\" --dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig --new",
            "--filter-tcp=443 --hostlist=\"{LIST}\" --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld --dpi-desync-repeats=6 --dpi-desync-fooling=badseq,md5sig --new",
            "--filter-udp=443 --hostlist=\"{LIST}\" --dpi-desync=fake --dpi-desync-repeats=11 --new",
            "--filter-tcp=1024-1124,9960-9969,18000,18060,18120,27900,28910,29900 --hostlist=\"{LIST}\" --dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6 --new",
            "--filter-udp=3478-3480,3659,1024-1124,18000,29900,50000-65535 --hostlist=\"{LIST}\" --dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6"
        });
    }

    private static string GetDefaultCoexistEngineArguments()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Used automatically when another zapret/winws instance is already running.",
            "# Keep this profile away from HTTP/TLS/QUIC 80/443 so it can coexist with upstream zapret presets.",
            "# Lines starting with # are ignored. {LIST} and {BIN} are replaced at runtime.",
            "--wf-tcp=1024-1124,9960-9969,18000,18060,18120,27900,28910,29900",
            "--wf-udp=3478-3480,3659,1024-1124,18000,29900,50000-65535",
            "--filter-tcp=1024-1124,9960-9969,18000,18060,18120,27900,28910,29900 --hostlist=\"{LIST}\" --dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6 --new",
            "--filter-udp=3478-3480,3659,1024-1124,18000,29900,50000-65535 --hostlist=\"{LIST}\" --dpi-desync=split2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6"
        });
    }

    private bool TryFindRunningOwnEngine(out int pid)
    {
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                if (process.HasExited || _engineProcess?.Id == process.Id)
                {
                    continue;
                }

                if (IsSamePath(TryGetProcessPath(process), _exeFile))
                {
                    pid = process.Id;
                    return true;
                }
            }
        }

        pid = 0;
        return false;
    }

    private bool HasExternalZapretEngine(out string summary)
    {
        var external = new List<string>();
        AddExternalZapretProcesses("winws", external);
        AddExternalZapretProcesses("winws2", external);

        summary = external.Count == 0 ? "" : string.Join(", ", external.Take(4));
        return external.Count > 0;
    }

    private void AddExternalZapretProcesses(string processName, List<string> external)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.HasExited || _engineProcess?.Id == process.Id)
                {
                    continue;
                }

                var processPath = TryGetProcessPath(process);
                if (IsSamePath(processPath, _exeFile))
                {
                    continue;
                }

                var displayPath = string.IsNullOrWhiteSpace(processPath) ? process.ProcessName : processPath;
                external.Add($"{process.ProcessName}:{process.Id} {displayPath}");
            }
        }
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
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
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

public static class Logger
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object SyncRoot = new();
    private static string _logDirectory = "";
    private static string _logFilePath = "";

    public static string LogDirectory => _logDirectory;
    public static string LogFilePath => _logFilePath;

    public static void Initialize(string rootDir)
    {
        _logDirectory = Path.Combine(rootDir, "logs");
        _logFilePath = Path.Combine(_logDirectory, "SteamUnlock.log");
        Directory.CreateDirectory(_logDirectory);
        RotateIfNeeded();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Engine(string message) => Write("ENGINE", message);
    public static void EngineError(string message) => Write("ENGINE-ERR", message);

    public static void CommandOutput(string commandName, string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Write("CMD", $"{commandName} stdout:{Environment.NewLine}{TrimForLog(stdout)}");
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Write("CMD-ERR", $"{commandName} stderr:{Environment.NewLine}{TrimForLog(stderr)}");
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_logFilePath))
            {
                return;
            }

            lock (SyncRoot)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    _logFilePath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the tray app.
        }
    }

    private static void RotateIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_logFilePath) || !File.Exists(_logFilePath))
        {
            return;
        }

        var info = new FileInfo(_logFilePath);
        if (info.Length < MaxLogBytes)
        {
            return;
        }

        var archivePath = Path.Combine(_logDirectory, $"SteamUnlock-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.Move(_logFilePath, archivePath, overwrite: true);
    }

    private static string TrimForLog(string text)
    {
        const int limit = 12000;
        if (text.Length <= limit)
        {
            return text.TrimEnd();
        }

        return text[..limit].TrimEnd() + $"{Environment.NewLine}... output trimmed ...";
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
