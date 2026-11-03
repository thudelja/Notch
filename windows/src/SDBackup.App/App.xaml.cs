using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using SDBackup.Windows;

namespace SDBackup.App;

/// <summary>
/// Starts Notch in the notification area. Command-line options:
/// <list type="bullet">
/// <item><c>--background</c>: start without opening the panel (used when starting with Windows).</item>
/// <item><c>--self-test &lt;folder&gt;</c>: open every window with sample data, write screenshots and a report, then exit.</item>
/// </list>
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\Notch.SingleInstance";
    private const string ShowSignalName = @"Local\Notch.ShowPanel";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private AppModel? _model;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var selfTest = Array.IndexOf(e.Args, "--self-test");
        if (selfTest >= 0)
        {
            var folder = selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1] : Path.Combine(Path.GetTempPath(), "Notch-self-test");
            Shutdown(SelfTest.Run(this, folder));
            return;
        }

        // One copy only. Starting it again opens the running copy's panel.
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showSignal = showSignal;
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                Dispatcher.InvokeAsync(ShowPanel);
            }
        })
        { IsBackground = true, Name = "Show panel signal" };
        listener.Start();

        System.Windows.Forms.Application.EnableVisualStyles();
        ThemeManager.Apply(Resources);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    ThemeManager.Apply(Resources);
                    if (_settings is not null) ThemeManager.ApplyTitleBar(_settings);
                });
            }
        };

        Directory.CreateDirectory(AppPaths.Data);
        var settings = new AppSettings(Path.Combine(AppPaths.Data, "settings.json"));
        var library = new LibraryStore(AppPaths.Data);
        var log = new BackupLog(AppPaths.Logs);

        var tray = new TrayIcon(LoadIcon("Tray.ico"), LoadIcon("TrayAttention.ico"));
        var model = new AppModel(settings, library, log, new WindowsDialogs(), (title, body) => tray.Notify(title, body),
            Path.Combine(AppPaths.Data, "history.json"));
        var flyout = new FlyoutWindow { DataContext = model };
        _tray = tray;
        _model = model;
        _flyout = flyout;

        tray.Clicked += flyout.Toggle;
        tray.SettingsRequested += ShowSettings;
        tray.QuitRequested += () => model.QuitCommand.Execute(null);
        model.SettingsRequested += ShowSettings;
        model.QuitRequested += ExitApp;
        model.WaitingCountChanged += tray.SetWaitingCount;

        model.Start();
        ConfigureStartupOnFirstRun();
        if (!e.Args.Contains("--background")) ShowPanel();
    }

    private void ShowPanel() => _flyout?.ShowNearTray();

    private void ShowSettings()
    {
        _flyout?.HideFlyout();
        var settings = _settings;
        if (settings is null)
        {
            settings = new SettingsWindow { DataContext = _model };
            settings.Closed += (_, _) => _settings = null;
            _settings = settings;
            settings.Show();
        }
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    /// <summary>
    /// An auto-import app only works while it runs, so it starts with Windows by default once installed
    /// (build.ps1 -Install puts it in %LOCALAPPDATA%\Programs). The user can turn this off in Settings.
    /// </summary>
    private static void ConfigureStartupOnFirstRun()
    {
        var marker = Path.Combine(AppPaths.Data, "startup-configured");
        if (File.Exists(marker)) return;
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (Environment.ProcessPath is not { } exe || !exe.StartsWith(programs, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            StartupRegistration.SetEnabled(true);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    internal static System.Drawing.Icon LoadIcon(string name)
    {
        var resource = GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))
            ?? throw new FileNotFoundException($"Missing icon resource {name}.");
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private void ExitApp()
    {
        _model?.Dispose();
        _tray?.Dispose();
        _showSignal?.Dispose();
        _instance?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
