using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using SSHTunnel4Win.Models;
using SSHTunnel4Win.Resources;
using SSHTunnel4Win.Services;
using SSHTunnel4Win.ViewModels;
using SSHTunnel4Win.Views;
using System.Runtime.InteropServices;
using static SSHTunnel4Win.App.NativeMethods;
using System.Windows.Documents;
using System.Collections.Generic;
using System.Windows.Controls;



namespace SSHTunnel4Win;

public partial class App : Application
{
    private static Mutex? _mutex;
    private bool _mutexOwned;
    private TaskbarIcon? _trayIcon;
    private MainViewModel _vm = null!;
    private MainWindow? _mainWindow;

    private IntPtr _hiddenWindowHandle = IntPtr.Zero;
    // Keeps the managed delegate alive while the unmanaged window class holds its pointer.
    // Without this reference the GC could collect the delegate and crash the process
    // when a message arrives at the hidden window.
    private WndProcDelegate _wndProcDelegate = null!;

    private readonly object _windowLock = new object();
    private const string HiddenWindowClassName = "SSHTunnelHiddenWindow";

    private static readonly uint WM_SHOW_MAIN_WINDOW = NativeMethods.RegisterWindowMessage("SSHTunnel_ShowWindow");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers: log crashes to disk and keep the app alive
        // where possible instead of dying silently.
        RegisterGlobalExceptionHandlers();

        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            LogCrash("Startup", ex);
            try
            {
                MessageBox.Show(
                    string.Format(Strings.StartupError, ex.Message),
                    Strings.SSHTunnelManager,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }
            Shutdown();
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        // Single instance check
        _mutex = new Mutex(true, "SSHTunnel4Win_SingleInstance", out bool isNew);
        _mutexOwned = isNew;
        if (!isNew)
        {
            SendShowWindowCommand();
            Shutdown();
            return;
        }
        

        // Initialize services
        var configStore = new ConfigStore();
        var tunnelStatus = new TunnelStatus();
        var processManager = new SSHProcessManager(tunnelStatus);
        var appSettings = new AppSettings();

        _vm = new MainViewModel(configStore, processManager, tunnelStatus, appSettings);

        // Create system tray icon
        CreateTrayIcon();

        // Auto-connect
        foreach (var config in configStore.Configs.Where(c => c.AutoConnect))
            processManager.Connect(config);

        // Open manager on launch
        ShowMainWindow();
        if (!appSettings.OpenManagerOnLaunch)
            _mainWindow?.Hide();

        // Hidden window receives a registered message from a second instance
        // so the running instance can show its main window.
        CreateHiddenWindow();
        // Auto-check for updates
        if (appSettings.AutoCheckForUpdates)
        {
            Task.Run(async () =>
            {
                var info = await UpdateService.CheckForUpdateAsync();
                if (info != null)
                {
                    var result = Dispatcher.Invoke(() =>
                        MessageBox.Show(
                            string.Format(Strings.NewVersionAvailable, info.Version),
                            Strings.UpdateAvailable,
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information));

                    if (result == MessageBoxResult.Yes)
                    {
                        if (info.InstallerUrl != null)
                        {
                            try
                            {
                                await UpdateService.PerformUpdateAsync(info.InstallerUrl, _ => { });
                            }
                            catch
                            {
                                Process.Start(new ProcessStartInfo { FileName = info.HtmlUrl, UseShellExecute = true });
                            }
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo { FileName = info.HtmlUrl, UseShellExecute = true });
                        }
                    }
                }
            });
        }

        // Handle URL scheme from command line
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && args[1].StartsWith("sshtunnel://"))
        {
            _vm.ImportFromShareString(args[1]);
            ShowMainWindow();
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // UI thread exceptions: log and show the error instead of crashing
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            try
            {
                MessageBox.Show(args.Exception.Message, Strings.SSHTunnelManager,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
        };

        // Background thread exceptions that cannot be recovered: log only
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SSHTunnel");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { }
    }

    // Runs an action on the UI thread, swallowing exceptions that occur while
    // the dispatcher is shutting down (otherwise Dispatcher.Invoke throws
    // TaskCanceledException during app exit).
    private void SafeDispatch(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SafeDispatch failed: {ex.Message}");
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "SSH Tunnel Manager"
        };

        UpdateTrayIcon();

        // Single left click on the tray icon opens the manager window.
        // A double click fires two TrayLeftMouseUp events first, so the window
        // is simply shown/activated again - ShowMainWindow is idempotent.
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

        // Build context menu
        RebuildTrayMenu2();
        _vm.ConfigStore.ConfigsChanged += () => SafeDispatch(() => { RebuildTrayMenu2(); UpdateTrayIcon(); });
        _vm.Status.StateChanged += _ => SafeDispatch(() => { RebuildTrayMenu2(); UpdateTrayIcon(); });
    }

    private static void SendShowWindowCommand()
    {
        try
        {
            // The running instance keeps a hidden message window - find it by class name
            IntPtr hWnd = NativeMethods.FindWindow(HiddenWindowClassName, null);
            if (hWnd != IntPtr.Zero)
            {
                NativeMethods.SendMessage(hWnd, WM_SHOW_MAIN_WINDOW, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            // Fallback: send the message to the main window of any other instance
            string currentProcessName = Process.GetCurrentProcess().ProcessName;
            Process[] processes = Process.GetProcessesByName(currentProcessName);
            foreach (var proc in processes)
            {
                if (proc.Id == Process.GetCurrentProcess().Id)
                    continue;

                IntPtr mainHwnd = proc.MainWindowHandle;
                if (mainHwnd != IntPtr.Zero)
                {
                    NativeMethods.SendMessage(mainHwnd, WM_SHOW_MAIN_WINDOW, IntPtr.Zero, IntPtr.Zero);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to send message: {ex.Message}");
        }
    }

    private System.Drawing.Icon? _iconConnected = null;
    
    private System.Drawing.Icon? IconConnected
    {
        get {
            if(_iconConnected == null)
            {
                var iconUri = new Uri($"pack://application:,,,/Assets/tray-icon-connected.ico", UriKind.Absolute);
                try
                {
                    var iconStream = GetResourceStream(iconUri)?.Stream;
                    if (iconStream != null)
                        _iconConnected = new System.Drawing.Icon(iconStream);
                }
                catch { }
                // Fallback so the tray icon is always visible even if the resource is missing
                _iconConnected ??= System.Drawing.SystemIcons.Application;
            }
            return _iconConnected;
        }
    }

    private System.Drawing.Icon? _iconDisconnected = null;

    private System.Drawing.Icon? IconDisconnected
    {
        get
        {
            if( _iconDisconnected == null)
            {
                var iconUri = new Uri($"pack://application:,,,/Assets/tray-icon.ico", UriKind.Absolute);
                try
                {
                    var iconStream = GetResourceStream(iconUri)?.Stream;
                    if (iconStream != null)
                        _iconDisconnected = new System.Drawing.Icon(iconStream);
                }
                catch { }
                // Fallback so the tray icon is always visible even if the resource is missing
                _iconDisconnected ??= System.Drawing.SystemIcons.Application;
            }
            return (_iconDisconnected);
        }
    }
    private void UpdateTrayIcon()
    {
        if (_trayIcon == null) return;
        var hasConnection = _vm.Status.HasAnyConnection();
        _trayIcon.Icon = hasConnection ? IconConnected : IconDisconnected;
    }

    private void RebuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // Tunnel items
        foreach (var config in _vm.ConfigStore.Configs)
        {
            var state = _vm.Status.GetState(config.Id);
            var header = $"{(state.IsActive() ? "\u25CF" : "\u25CB")} {(string.IsNullOrEmpty(config.Name) ? config.Host : config.Name)}";
            var item = new System.Windows.Controls.MenuItem { Header = header };
            var capturedConfig = config;
            item.Tag = config.Id;
            item.Click += Config_Click;
            
            menu.Items.Add(item);
        }

        if (_vm.ConfigStore.Configs.Count > 0)
            menu.Items.Add(new System.Windows.Controls.Separator());

        // Open Manager
        var openItem = new System.Windows.Controls.MenuItem { Header = Strings.OpenManager };
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(openItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Reconnect All
        var reconnectAllItem = new System.Windows.Controls.MenuItem { Header = Strings.ReconnectAll };
        reconnectAllItem.IsEnabled = _vm.ConfigStore.Configs.Any(c => _vm.Status.GetState(c.Id).IsActive());
        reconnectAllItem.Click += (_, _) => _vm.ProcessManager.ReconnectAll();
        menu.Items.Add(reconnectAllItem);

        // Disconnect All
        var disconnectItem = new System.Windows.Controls.MenuItem { Header = Strings.DisconnectAll };
        disconnectItem.Click += (_, _) => _vm.ProcessManager.DisconnectAll();
        menu.Items.Add(disconnectItem);

        // Check for Updates
        var updateItem = new System.Windows.Controls.MenuItem { Header = Strings.CheckForUpdates };
        updateItem.Click += async (_, _) => await _vm.CheckForUpdatesCommand.ExecuteAsync(null);
        menu.Items.Add(updateItem);

        // Settings
        var settingsItem = new System.Windows.Controls.MenuItem { Header = Strings.Settings };
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Quit
        var quitItem = new System.Windows.Controls.MenuItem { Header = Strings.Quit };
        quitItem.Click += (_, _) => QuitApp();
        menu.Items.Add(quitItem);

        if (_trayIcon != null)
            _trayIcon.ContextMenu = menu;
    }

    private void RebuildTrayMenu2()
    {

        if (_trayIcon == null)
            return;

        if (_trayIcon.ContextMenu == null)
            _trayIcon.ContextMenu = BuildBaseMenu();

        var menu = _trayIcon.ContextMenu;

        var toRemove = new List<System.Windows.Controls.MenuItem>();
        var Updated = new List<Guid>();



        foreach(var m1 in menu.Items)
        {

            var mid = (m1 as System.Windows.Controls.MenuItem)?.Tag as Guid?;
            
            if(mid ==  null) continue;

            var config = _vm.ConfigStore.Configs.Find(a => a.Id == mid);

            if(config == null)
            {
                toRemove.Add((System.Windows.Controls.MenuItem)m1);
                continue;
            }

            var mi = (System.Windows.Controls.MenuItem)m1;
            var state = _vm.Status.GetState(config.Id);
            mi.Header = $"{(state.IsActive() ? "\u25CF" : "\u25CB")} {(string.IsNullOrEmpty(config.Name) ? config.Host : config.Name)}";
            Updated.Add(mid.Value);
        }

        foreach (var m1 in toRemove)
        {
            menu.Items.Remove(m1);
        }


            // Tunnel items - insert before the first separator so they stay
            // grouped at the top of the menu and keep list order.
            int insertIndex = menu.Items.Count;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is System.Windows.Controls.Separator)
                {
                    insertIndex = i;
                    break;
                }
            }

            foreach (var config in _vm.ConfigStore.Configs)
            {
                if(Updated.Contains(config.Id))
                    continue;

                var state = _vm.Status.GetState(config.Id);
                var header = $"{(state.IsActive() ? "\u25CF" : "\u25CB")} {(string.IsNullOrEmpty(config.Name) ? config.Host : config.Name)}";
                var item = new System.Windows.Controls.MenuItem { Header = header };
                //var capturedConfig = config;
                item.Tag = config.Id;
                item.Click += Config_Click;
                menu.Items.Insert(insertIndex++, item);
            }

    }

    private ContextMenu BuildBaseMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Open Manager
        var openItem = new System.Windows.Controls.MenuItem { Header = Strings.OpenManager };
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(openItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Reconnect All
        var reconnectAllItem = new System.Windows.Controls.MenuItem { Header = Strings.ReconnectAll };
        reconnectAllItem.IsEnabled = _vm.ConfigStore.Configs.Any(c => _vm.Status.GetState(c.Id).IsActive());
        reconnectAllItem.Click += (_, _) => _vm.ProcessManager.ReconnectAll();
        menu.Items.Add(reconnectAllItem);

        // Disconnect All
        var disconnectItem = new System.Windows.Controls.MenuItem { Header = Strings.DisconnectAll };
        disconnectItem.Click += (_, _) => _vm.ProcessManager.DisconnectAll();
        menu.Items.Add(disconnectItem);

        // Check for Updates
        var updateItem = new System.Windows.Controls.MenuItem { Header = Strings.CheckForUpdates };
        updateItem.Click += async (_, _) => await _vm.CheckForUpdatesCommand.ExecuteAsync(null);
        menu.Items.Add(updateItem);

        // Settings
        var settingsItem = new System.Windows.Controls.MenuItem { Header = Strings.Settings };
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Quit
        var quitItem = new System.Windows.Controls.MenuItem { Header = Strings.Quit };
        quitItem.Click += (_, _) => QuitApp();
        menu.Items.Add(quitItem);

        return menu;
    }

    private void Config_Click(object sender, RoutedEventArgs e)
    {
        _vm.ProcessManager.Toggle(_vm.ConfigStore.Configs.First(a => a.Id == (Guid)((System.Windows.Controls.MenuItem)sender).Tag));
    }
    private void ShowMainWindow()
    {
        if (_mainWindow == null)// || !_mainWindow.IsLoaded)
        {
            lock (_windowLock)
            {
                if (_mainWindow == null)
                {
                    _mainWindow = new MainWindow();
                    _mainWindow.Initialize(_vm);
                }
            }
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        if (!_mainWindow.IsVisible)
            _mainWindow.Show();

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;

    }

    private void ShowSettings()
    {
        var settingsVm = new SettingsViewModel(_vm.AppSettings);
        var window = new SettingsWindow(settingsVm);
        if (_mainWindow?.IsLoaded == true)
            window.Owner = _mainWindow;
        window.ShowDialog();
    }

    private void QuitApp()
    {
        try
        {
            _vm.ProcessManager.DisconnectOnQuit(_vm.ConfigStore.Configs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DisconnectOnQuit failed: {ex.Message}");
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_hiddenWindowHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hiddenWindowHandle);
                _hiddenWindowHandle = IntPtr.Zero;
            }
            _trayIcon?.Dispose();
            _trayIcon = null;
            // Only release the mutex if this instance actually owns it
            // (the second instance never acquires it and ReleaseMutex would throw)
            if (_mutexOwned)
            {
                _mutex?.ReleaseMutex();
                _mutexOwned = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnExit error: {ex.Message}");
        }
        base.OnExit(e);
    }

    private void CreateHiddenWindow()
    {
        try
        {
            // Pin the managed WndProc delegate: the unmanaged window class holds a
            // raw function pointer, and without this reference the GC could collect
            // the delegate and crash the process when a message arrives.
            _wndProcDelegate = WndProc;

            var wndClass = new NativeMethods.WNDCLASS
            {
                style = 0,
                lpfnWndProc = _wndProcDelegate,
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = HiddenWindowClassName
            };
            NativeMethods.RegisterClass(ref wndClass);

            // ���� ���� (���������)
            _hiddenWindowHandle = NativeMethods.CreateWindowEx(
                0,
                HiddenWindowClassName,
                "SSHTunnel Hidden Window",
                0, 0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.GetModuleHandle(null),
                IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create hidden window: {ex.Message}");
        }
    }
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SHOW_MAIN_WINDOW)
        {
            ShowMainWindow();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }


    internal static class NativeMethods
    {
        // ����������� ���������
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern uint RegisterWindowMessage(string lpString);

        // �������� ���������
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // ����� ���� �� ������ ��������
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        // �������� ����
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        // ����������� ������ ����
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        // ������� ��������� �� ���������
        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        // ����������� ����
        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        // ��������� ���������� ����������
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        // ��������� ��� ����������� ������ ����
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct WNDCLASS
        {
            public int style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
