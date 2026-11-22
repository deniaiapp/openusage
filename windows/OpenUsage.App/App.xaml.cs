using System.Windows;
using OpenUsage.App.Services;
using OpenUsage.App.Views;
using OpenUsage.Core;
using OpenUsage.Core.Support;
using Application = System.Windows.Application;

namespace OpenUsage.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppContainer? _container;
    private TrayController? _tray;
    private DashboardWindow? _window;
    private HotkeyService? _hotkey;
    private NotificationActivation? _notificationActivation;
    private bool _openDashboard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == NotificationRequest.Argument)
        {
            if (!NotificationRequest.OpensDashboard(e.Args))
            {
                Shutdown();
                return;
            }
            _openDashboard = true;
        }

        var mutex = new Mutex(true, @"Local\OpenUsage.Windows", out var created);
        if (!created)
        {
            mutex.Dispose();
            if (_openDashboard) NotificationActivation.Forward();
            Shutdown();
            return;
        }
        _mutex = mutex;

        try
        {
            try { _notificationActivation = new NotificationActivation(OpenDashboardFromNotification); }
            catch (Exception ex) { AppLog.Warn("notifications", $"Could not listen for toast activation ({ex.GetType().Name})."); }

            _container = new AppContainer(action =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.Invoke(action);
            });
            ThemeService.Apply(_container.Settings.Theme);
            LaunchAtLogin.Apply(_container.Settings.LaunchAtLogin);
            _container.Start();
            if (_container.WelcomePending) ShowWelcome(_container);
            else StartDashboard(_container);
        }
        catch (Exception ex)
        {
            FailStartup(ex);
        }
    }

    private void FailStartup(Exception ex)
    {
        AppLog.Error("lifecycle", "startup failed", ex);
        System.Windows.MessageBox.Show(
            $"OpenUsage failed to start.\n\n{ex.Message}",
            "OpenUsage",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown();
    }

    /// <summary>
    /// A fresh install opens the welcome screen first; the tray icon and dashboard (and scheduled
    /// refreshes) start only once the user leaves it.
    /// </summary>
    private void ShowWelcome(AppContainer container)
    {
        var setup = container.MakeFirstLaunchSetup();
        var welcome = new WelcomeWindow(setup);
        welcome.Finished += () =>
        {
            try
            {
                container.FinishWelcome(setup);
                StartDashboard(container);
                _window?.ShowScreen(DashboardScreen.Dashboard);
            }
            catch (Exception ex)
            {
                FailStartup(ex);
            }
        };
        welcome.Show();
        welcome.Activate();
    }

    private void StartDashboard(AppContainer container)
    {
        _window = new DashboardWindow(container);
        _tray = new TrayController(container, _window);
        _hotkey = new HotkeyService();
        _hotkey.Attach(_window);
        _hotkey.Apply(container.Settings.GlobalHotkey);
        var window = _window;
        if (_openDashboard) window.ShowScreen(DashboardScreen.Dashboard);
        _hotkey.Activated += () => window.Dispatcher.Invoke(window.ToggleFromTray);
        _window.HotkeyChanged += combo =>
        {
            container.Settings.GlobalHotkey = combo;
            container.Settings.Save();
            _hotkey.Apply(combo);
        };
    }

    private void OpenDashboardFromNotification()
    {
        if (_window is { } window) window.ShowScreen(DashboardScreen.Dashboard);
        else _openDashboard = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notificationActivation?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _container?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
