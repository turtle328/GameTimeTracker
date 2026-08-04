using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace GameTimeTracker
{
    public sealed partial class MainWindow : Window
    {
        private const int ShowWindowCommandHide = 0;
        private const int ShowWindowCommandShow = 5;
        private const string StartupArgument = "--minimized";
        private const string StartupTaskId = "GameTimeTrackerStartup";
        private const string StartupShortcutFileName = "GameTimeTracker.lnk";
        private const string StartupRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValueName = "GameTimeTracker";

        private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };
        private static readonly string CacheFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameTimeTracker",
            "tracked-games.json");
        private static readonly string StartupShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupShortcutFileName);

        private readonly DispatcherTimer _trackingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateOnly _currentTrackingDate = DateOnly.FromDateTime(DateTime.Now);
        private AppWindow? _appWindow;
        private WinForms.NotifyIcon? _trayIcon;
        private Drawing.Icon? _trayIconImage;
        private IntPtr _windowHandle;
        private bool _allowClose;
        private bool _hideToTrayOnClose = true;
        private bool _ownsTrayIconImage;
        private bool _isUpdatingHideToTrayOnCloseToggle;
        private bool _isUpdatingStartupToggle;
        private bool _isLoadingCache;
        private bool _startMinimized;
        private bool _showSeconds;
        private DateTimeOffset _lastTimerTick = DateTimeOffset.Now;

        public ObservableCollection<TrackedGame> TrackedGames { get; } = [];

        public MainWindow()
        {
            InitializeComponent();
            _windowHandle = WindowNative.GetWindowHandle(this);
            _startMinimized = ShouldStartMinimized();
            SetWindowIcon();
            InitializeTrayIcon();
            _ = InitializeStartupToggleAsync();
            InitializeHideToTrayOnCloseToggle();

            GamesList.ItemsSource = TrackedGames;
            Closed += MainWindow_Closed;
            _trackingTimer.Tick += TrackingTimer_Tick;

            _ = InitializeTrackerAsync();
        }

        public void ActivateForLaunch(bool wasStartedByStartupTask)
        {
            if (_startMinimized || wasStartedByStartupTask)
            {
                Activate();
                HideToTray();
                return;
            }

            ShowMainWindow();
        }

        private void SetWindowIcon()
        {
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += AppWindow_Closing;

            string iconPath = GetAppIconPath();
            if (!File.Exists(iconPath))
            {
                return;
            }

            _appWindow.SetIcon(iconPath);
        }

        private void InitializeTrayIcon()
        {
            string iconPath = GetAppIconPath();
            if (File.Exists(iconPath))
            {
                _trayIconImage = new Drawing.Icon(iconPath);
                _ownsTrayIconImage = true;
            }
            else
            {
                _trayIconImage = Drawing.SystemIcons.Application;
            }

            WinForms.ContextMenuStrip menu = new();
            _ = menu.Items.Add("Open GameTimeTracker", null, (_, _) => ShowMainWindowFromTray());
            _ = menu.Items.Add("Exit", null, (_, _) => ExitApplicationFromTray());

            _trayIcon = new WinForms.NotifyIcon
            {
                ContextMenuStrip = menu,
                Icon = _trayIconImage,
                Text = "Game Time Tracker",
                Visible = true
            };
            _trayIcon.MouseClick += TrayIcon_MouseClick;
        }

        private async System.Threading.Tasks.Task InitializeStartupToggleAsync()
        {
            _isUpdatingStartupToggle = true;
            StartWithWindowsToggle.IsEnabled = false;

            try
            {
                DeleteLegacyStartupRegistryValue();
                if (HasPackageIdentity())
                {
                    DeleteStartupShortcut();
                }

                StartWithWindowsToggle.IsOn = await IsRegisteredForStartupAsync();
                StartWithWindowsToggle.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StartWithWindowsToggle.IsOn = false;
                TrackingStatusText.Text = $"Startup unavailable: {ex.Message}";
            }
            finally
            {
                _isUpdatingStartupToggle = false;
            }
        }

        private void InitializeHideToTrayOnCloseToggle()
        {
            _isUpdatingHideToTrayOnCloseToggle = true;
            HideToTrayOnCloseToggle.IsOn = _hideToTrayOnClose;
            _isUpdatingHideToTrayOnCloseToggle = false;
        }

        private async System.Threading.Tasks.Task InitializeTrackerAsync()
        {
            await LoadCacheAsync();
            _lastTimerTick = DateTimeOffset.Now;
            _trackingTimer.Start();
            RefreshWindowStatus();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            SaveCache();
            DisposeTrayIcon();
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            await AddGameFromInputAsync();
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };

            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            ExecutableInput.Text = file.Path;
            TrackingStatusText.Text = $"Selected {Path.GetFileName(file.Path)}";
        }

        private async void ExecutableInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            await AddGameFromInputAsync();
        }

        private void ShowSecondsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _showSeconds = ShowSecondsToggle.IsOn;

            foreach (TrackedGame game in TrackedGames)
            {
                game.ShowSeconds = _showSeconds;
            }

            if (!_isLoadingCache)
            {
                SaveCache();
            }
        }

        private async void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingStartupToggle)
            {
                return;
            }

            try
            {
                await SetStartupRegistrationAsync(StartWithWindowsToggle.IsOn);
                TrackingStatusText.Text = StartWithWindowsToggle.IsOn
                    ? "Startup tracking enabled"
                    : "Startup tracking disabled";
            }
            catch (Exception ex)
            {
                _isUpdatingStartupToggle = true;
                StartWithWindowsToggle.IsOn = await IsRegisteredForStartupAsync();
                _isUpdatingStartupToggle = false;
                TrackingStatusText.Text = $"Could not update startup: {ex.Message}";
            }
        }

        private void HideToTrayOnCloseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingHideToTrayOnCloseToggle)
            {
                return;
            }

            _hideToTrayOnClose = HideToTrayOnCloseToggle.IsOn;
            TrackingStatusText.Text = _hideToTrayOnClose
                ? "Closing hides to tray"
                : "Closing exits the app";
            SaveCache();
        }

        private async void ResetGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TrackedGame game)
            {
                bool confirmed = await ConfirmDestructiveActionAsync(
                    "Reset tracked time?",
                    $"Reset all tracked time for {game.GameName}? This clears both today's time and total time.",
                    "Reset");
                if (!confirmed)
                {
                    return;
                }

                game.ResetTime();
                TrackingStatusText.Text = $"Reset {game.GameName}";
                SaveCache();
            }
        }

        private async void RemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TrackedGame game)
            {
                bool confirmed = await ConfirmDestructiveActionAsync(
                    "Remove game?",
                    $"Remove {game.GameName} from the tracker? Its tracked time will be deleted from the cache.",
                    "Remove");
                if (!confirmed)
                {
                    return;
                }

                TrackedGames.Remove(game);
                RefreshWindowStatus();
                SaveCache();
            }
        }

        private async System.Threading.Tasks.Task<bool> ConfirmDestructiveActionAsync(
            string title,
            string message,
            string confirmText)
        {
            ContentDialog dialog = new()
            {
                Title = title,
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = ((FrameworkElement)Content).XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async System.Threading.Tasks.Task AddGameFromInputAsync()
        {
            string input = ExecutableInput.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(input))
            {
                TrackingStatusText.Text = "Enter an executable first";
                return;
            }

            AddButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;

            try
            {
                ExecutableMetadata metadata = await ResolveExecutableAsync(input);
                foreach (TrackedGame existingGame in TrackedGames)
                {
                    if (existingGame.Matches(metadata))
                    {
                        TrackingStatusText.Text = $"Already tracking {existingGame.GameName}";
                        return;
                    }
                }

                TrackedGame game = new(metadata.GameName, metadata.ExecutableName, metadata.ExecutablePath, metadata.IconSource)
                {
                    ShowSeconds = _showSeconds
                };

                TrackedGames.Add(game);
                ExecutableInput.Text = string.Empty;
                TrackingStatusText.Text = $"Tracking {game.GameName}";
                SaveCache();
            }
            catch (Exception ex)
            {
                TrackingStatusText.Text = $"Could not add executable: {ex.Message}";
            }
            finally
            {
                AddButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task<ExecutableMetadata> ResolveExecutableAsync(string input)
        {
            string? executablePath = TryGetExistingExecutablePath(input)
                ?? TryGetRunningProcessExecutablePath(input)
                ?? TryGetPathExecutablePath(input);

            string executableName = NormalizeExecutableName(executablePath ?? input);
            string gameName = executablePath is null
                ? Path.GetFileNameWithoutExtension(executableName)
                : GetExecutableDescription(executablePath, executableName);

            ImageSource? iconSource = executablePath is null
                ? null
                : await TryLoadExecutableIconAsync(executablePath);

            return new ExecutableMetadata(gameName, executableName, executablePath, iconSource);
        }

        private static string? TryGetExistingExecutablePath(string input)
        {
            try
            {
                if (File.Exists(input))
                {
                    return Path.GetFullPath(input);
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static string? TryGetRunningProcessExecutablePath(string input)
        {
            string processName = Path.GetFileNameWithoutExtension(input);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        string? fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
                        {
                            return fileName;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            return null;
        }

        private static string? TryGetPathExecutablePath(string input)
        {
            string executableName = NormalizeExecutableName(input);
            string? pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return null;
            }

            foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private static string NormalizeExecutableName(string value)
        {
            string fileName = Path.GetFileName(value);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = value;
            }

            return string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : $"{fileName}.exe";
        }

        private static string GetExecutableDescription(string executablePath, string executableName)
        {
            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                {
                    return versionInfo.FileDescription;
                }

                if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                {
                    return versionInfo.ProductName;
                }
            }
            catch (Exception)
            {
            }

            return Path.GetFileNameWithoutExtension(executableName);
        }

        private static async System.Threading.Tasks.Task<ImageSource?> TryLoadExecutableIconAsync(string executablePath)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(executablePath);
                using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    128,
                    ThumbnailOptions.UseCurrentScale);

                BitmapImage image = new();
                await image.SetSourceAsync(thumbnail);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void TrackingTimer_Tick(object? sender, object e)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            TimeSpan elapsedSinceLastTick = now - _lastTimerTick;
            _lastTimerTick = now;
            bool shouldSaveCache = ResetTodayIfNeeded(now);

            if (elapsedSinceLastTick <= TimeSpan.Zero)
            {
                elapsedSinceLastTick = TimeSpan.FromSeconds(1);
            }
            else if (elapsedSinceLastTick > TimeSpan.FromSeconds(5))
            {
                elapsedSinceLastTick = TimeSpan.FromSeconds(1);
            }

            ForegroundProcess? foregroundProcess = GetForegroundProcess();
            TrackedGame? focusedGame = null;

            foreach (TrackedGame game in TrackedGames)
            {
                bool isFocused = foregroundProcess is not null && game.Matches(foregroundProcess.Value);
                game.IsFocused = isFocused;

                if (isFocused)
                {
                    focusedGame = game;
                }
            }

            if (focusedGame is not null)
            {
                focusedGame.AddTrackedTime(elapsedSinceLastTick);
                shouldSaveCache = true;
            }

            if (shouldSaveCache)
            {
                SaveCache();
            }

            RefreshWindowStatus(focusedGame);
            UpdateTrayTooltip(focusedGame);
        }

        private bool ResetTodayIfNeeded(DateTimeOffset now)
        {
            DateOnly currentDate = DateOnly.FromDateTime(now.LocalDateTime);
            if (currentDate == _currentTrackingDate)
            {
                return false;
            }

            _currentTrackingDate = currentDate;

            foreach (TrackedGame game in TrackedGames)
            {
                game.TodayElapsed = TimeSpan.Zero;
            }

            return true;
        }

        private async System.Threading.Tasks.Task LoadCacheAsync()
        {
            if (!File.Exists(CacheFilePath))
            {
                return;
            }

            try
            {
                _isLoadingCache = true;
                string json = await File.ReadAllTextAsync(CacheFilePath);
                TrackerCache? cache = JsonSerializer.Deserialize<TrackerCache>(json, CacheJsonOptions);
                if (cache is null)
                {
                    return;
                }

                _showSeconds = cache.ShowSeconds;
                ShowSecondsToggle.IsOn = _showSeconds;
                _hideToTrayOnClose = cache.HideToTrayOnClose;
                _isUpdatingHideToTrayOnCloseToggle = true;
                HideToTrayOnCloseToggle.IsOn = _hideToTrayOnClose;
                _isUpdatingHideToTrayOnCloseToggle = false;
                string todayDateKey = GetCacheDateKey(_currentTrackingDate);

                foreach (TrackedGameCacheEntry entry in cache.Games)
                {
                    if (string.IsNullOrWhiteSpace(entry.ExecutableName))
                    {
                        continue;
                    }

                    string? executablePath = string.IsNullOrWhiteSpace(entry.ExecutablePath) ? null : entry.ExecutablePath;
                    ImageSource? iconSource = executablePath is not null && File.Exists(executablePath)
                        ? await TryLoadExecutableIconAsync(executablePath)
                        : null;

                    long totalElapsedTicks = entry.TotalElapsedTicks > 0
                        ? entry.TotalElapsedTicks
                        : entry.ElapsedTicks;
                    long todayElapsedTicks = string.Equals(entry.TodayDate, todayDateKey, StringComparison.Ordinal)
                        ? entry.TodayElapsedTicks
                        : 0;

                    TrackedGame game = new(
                        string.IsNullOrWhiteSpace(entry.GameName) ? Path.GetFileNameWithoutExtension(entry.ExecutableName) : entry.GameName,
                        NormalizeExecutableName(entry.ExecutableName),
                        executablePath,
                        iconSource)
                    {
                        TodayElapsed = TimeSpan.FromTicks(Math.Max(0, todayElapsedTicks)),
                        TotalElapsed = TimeSpan.FromTicks(Math.Max(0, totalElapsedTicks)),
                        ShowSeconds = _showSeconds
                    };

                    TrackedGames.Add(game);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not load tracked games cache: {ex}");
            }
            finally
            {
                _isLoadingCache = false;
            }
        }

        private void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);

                TrackerCache cache = new()
                {
                    HideToTrayOnClose = _hideToTrayOnClose,
                    ShowSeconds = _showSeconds
                };
                string todayDateKey = GetCacheDateKey(_currentTrackingDate);

                foreach (TrackedGame game in TrackedGames)
                {
                    cache.Games.Add(new TrackedGameCacheEntry
                    {
                        GameName = game.GameName,
                        ExecutableName = game.ExecutableName,
                        ExecutablePath = game.ExecutablePath,
                        TodayDate = todayDateKey,
                        TodayElapsedTicks = game.TodayElapsed.Ticks,
                        TotalElapsedTicks = game.TotalElapsed.Ticks,
                        ElapsedTicks = game.TotalElapsed.Ticks
                    });
                }

                string json = JsonSerializer.Serialize(cache, CacheJsonOptions);
                string tempPath = $"{CacheFilePath}.tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, CacheFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not save tracked games cache: {ex}");
            }
        }

        private static string GetCacheDateKey(DateOnly date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private void RefreshWindowStatus(TrackedGame? focusedGame = null)
        {
            if (TrackedGames.Count == 0)
            {
                TrackingStatusText.Text = "No games tracked";
            }
            else if (focusedGame is not null)
            {
                TrackingStatusText.Text = $"Focused: {focusedGame.GameName}";
            }
            else
            {
                TrackingStatusText.Text = $"{TrackedGames.Count} game{(TrackedGames.Count == 1 ? string.Empty : "s")} tracked";
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
            {
                return;
            }

            if (!_hideToTrayOnClose)
            {
                return;
            }

            args.Cancel = true;
            HideToTray();
        }

        private void HideToTray()
        {
            SaveCache();
            _ = ShowWindow(_windowHandle, ShowWindowCommandHide);
            UpdateTrayTooltip();
        }

        private void ShowMainWindowFromTray()
        {
            _ = DispatcherQueue.TryEnqueue(ShowMainWindow);
        }

        private void ExitApplicationFromTray()
        {
            _ = DispatcherQueue.TryEnqueue(ExitApplication);
        }

        private void ShowMainWindow()
        {
            _ = ShowWindow(_windowHandle, ShowWindowCommandShow);
            Activate();
            _ = SetForegroundWindow(_windowHandle);
        }

        private void ExitApplication()
        {
            _allowClose = true;
            SaveCache();
            Close();
        }

        private void TrayIcon_MouseClick(object? sender, WinForms.MouseEventArgs e)
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                ShowMainWindowFromTray();
            }
        }

        private void UpdateTrayTooltip(TrackedGame? focusedGame = null)
        {
            if (_trayIcon is null)
            {
                return;
            }

            string text = focusedGame is null
                ? "Game Time Tracker"
                : $"Tracking {focusedGame.GameName}";

            _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        }

        private void DisposeTrayIcon()
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.MouseClick -= TrayIcon_MouseClick;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_trayIconImage is not null && _ownsTrayIconImage)
            {
                _trayIconImage.Dispose();
            }

            _trayIconImage = null;
            _ownsTrayIconImage = false;
        }

        private static bool ShouldStartMinimized()
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static async System.Threading.Tasks.Task<bool> IsRegisteredForStartupAsync()
        {
            if (HasPackageIdentity())
            {
                Windows.ApplicationModel.StartupTask startupTask = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                return startupTask.State == Windows.ApplicationModel.StartupTaskState.Enabled;
            }

            return File.Exists(StartupShortcutPath);
        }

        private static async System.Threading.Tasks.Task SetStartupRegistrationAsync(bool shouldStartWithWindows)
        {
            if (!HasPackageIdentity())
            {
                if (shouldStartWithWindows)
                {
                    CreateStartupShortcut(GetCurrentExecutablePath());
                }
                else
                {
                    DeleteStartupShortcut();
                }

                DeleteLegacyStartupRegistryValue();
                return;
            }

            Windows.ApplicationModel.StartupTask startupTask = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);

            if (!shouldStartWithWindows)
            {
                startupTask.Disable();
                DeleteStartupShortcut();
                DeleteLegacyStartupRegistryValue();
                return;
            }

            Windows.ApplicationModel.StartupTaskState state = await startupTask.RequestEnableAsync();
            if (state != Windows.ApplicationModel.StartupTaskState.Enabled)
            {
                throw new InvalidOperationException($"Windows returned startup state '{state}'.");
            }

            DeleteStartupShortcut();
            DeleteLegacyStartupRegistryValue();
        }

        private static bool HasPackageIdentity()
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current.Id.FullName;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetCurrentExecutablePath()
        {
            string? executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                return executablePath;
            }

            using Process currentProcess = Process.GetCurrentProcess();
            executablePath = currentProcess.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                return executablePath;
            }

            throw new InvalidOperationException("Could not resolve the current executable path.");
        }

        private static void CreateStartupShortcut(string executablePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupShortcutPath)!);

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                throw new InvalidOperationException("Windows Script Host is unavailable.");
            }

            object? shell = null;
            object? shortcut = null;

            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell is null)
                {
                    throw new InvalidOperationException("Could not create the Windows shortcut shell.");
                }

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: [StartupShortcutPath]);

                if (shortcut is null)
                {
                    throw new InvalidOperationException("Could not create the startup shortcut.");
                }

                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.SetProperty,
                    binder: null,
                    target: shortcut,
                    args: [executablePath]);
                shortcutType.InvokeMember(
                    "Arguments",
                    System.Reflection.BindingFlags.SetProperty,
                    binder: null,
                    target: shortcut,
                    args: [StartupArgument]);
                shortcutType.InvokeMember(
                    "WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty,
                    binder: null,
                    target: shortcut,
                    args: [Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory]);

                string iconPath = GetAppIconPath();
                if (File.Exists(iconPath))
                {
                    shortcutType.InvokeMember(
                        "IconLocation",
                        System.Reflection.BindingFlags.SetProperty,
                        binder: null,
                        target: shortcut,
                        args: [$"{iconPath},0"]);
                }

                shortcutType.InvokeMember(
                    "Save",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null,
                    target: shortcut,
                    args: null);
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static void DeleteStartupShortcut()
        {
            try
            {
                File.Delete(StartupShortcutPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not remove startup shortcut: {ex}");
            }
        }

        private static void DeleteLegacyStartupRegistryValue()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryKeyPath, writable: true);
                key?.DeleteValue(StartupRegistryValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not remove legacy startup registry value: {ex}");
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                _ = Marshal.FinalReleaseComObject(value);
            }
        }

        private static string GetAppIconPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        }

        private static ForegroundProcess? GetForegroundProcess()
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(foregroundWindow, out uint processId);
            if (processId == 0)
            {
                return null;
            }

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                string executableName = NormalizeExecutableName(process.ProcessName);

                try
                {
                    string? executablePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        return new ForegroundProcess(executableName, executablePath);
                    }
                }
                catch (Exception)
                {
                }

                return new ForegroundProcess(executableName, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    public sealed class TrackedGame : INotifyPropertyChanged
    {
        private readonly Brush _focusedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16));
        private readonly Brush _idleBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 96, 96));
        private bool _isFocused;
        private bool _showSeconds;
        private TimeSpan _todayElapsed;
        private TimeSpan _totalElapsed;

        public TrackedGame(string gameName, string executableName, string? executablePath, ImageSource? iconSource)
        {
            GameName = gameName;
            ExecutableName = executableName;
            ExecutablePath = executablePath;
            IconSource = iconSource;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string GameName { get; }

        public string ExecutableName { get; }

        public string? ExecutablePath { get; }

        public ImageSource? IconSource { get; }

        public string ExecutableLabel => ExecutablePath ?? ExecutableName;

        public string TodayDisplayTime => FormatElapsed(TodayElapsed, ShowSeconds);

        public string TotalDisplayTime => FormatElapsed(TotalElapsed, ShowSeconds);

        public string FocusStatus => IsFocused ? "Focused" : "Idle";

        public Brush FocusBrush => IsFocused ? _focusedBrush : _idleBrush;

        public bool ShowSeconds
        {
            get => _showSeconds;
            set
            {
                if (_showSeconds == value)
                {
                    return;
                }

                _showSeconds = value;
                OnPropertyChanged(nameof(ShowSeconds));
                OnPropertyChanged(nameof(TodayDisplayTime));
                OnPropertyChanged(nameof(TotalDisplayTime));
            }
        }

        public TimeSpan TodayElapsed
        {
            get => _todayElapsed;
            set
            {
                if (_todayElapsed == value)
                {
                    return;
                }

                _todayElapsed = value;
                OnPropertyChanged(nameof(TodayElapsed));
                OnPropertyChanged(nameof(TodayDisplayTime));
            }
        }

        public TimeSpan TotalElapsed
        {
            get => _totalElapsed;
            set
            {
                if (_totalElapsed == value)
                {
                    return;
                }

                _totalElapsed = value;
                OnPropertyChanged(nameof(TotalElapsed));
                OnPropertyChanged(nameof(TotalDisplayTime));
            }
        }

        public bool IsFocused
        {
            get => _isFocused;
            set
            {
                if (_isFocused == value)
                {
                    return;
                }

                _isFocused = value;
                OnPropertyChanged(nameof(IsFocused));
                OnPropertyChanged(nameof(FocusStatus));
                OnPropertyChanged(nameof(FocusBrush));
            }
        }

        public bool Matches(ExecutableMetadata metadata)
        {
            if (ExecutablePath is not null && metadata.ExecutablePath is not null)
            {
                return string.Equals(ExecutablePath, metadata.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(ExecutableName, metadata.ExecutableName, StringComparison.OrdinalIgnoreCase);
        }

        public bool Matches(ForegroundProcess process)
        {
            if (ExecutablePath is not null && process.ExecutablePath is not null)
            {
                return string.Equals(ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(ExecutableName, process.ExecutableName, StringComparison.OrdinalIgnoreCase);
        }

        public void AddTrackedTime(TimeSpan elapsed)
        {
            TodayElapsed += elapsed;
            TotalElapsed += elapsed;
        }

        public void ResetTime()
        {
            TodayElapsed = TimeSpan.Zero;
            TotalElapsed = TimeSpan.Zero;
        }

        private static string FormatElapsed(TimeSpan elapsed, bool showSeconds)
        {
            int hours = (int)elapsed.TotalHours;
            return showSeconds
                ? $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{hours:00}:{elapsed.Minutes:00}";
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public readonly record struct ExecutableMetadata(
        string GameName,
        string ExecutableName,
        string? ExecutablePath,
        ImageSource? IconSource);

    public readonly record struct ForegroundProcess(
        string ExecutableName,
        string? ExecutablePath);

    public sealed class TrackerCache
    {
        public bool HideToTrayOnClose { get; set; } = true;

        public bool ShowSeconds { get; set; }

        public Collection<TrackedGameCacheEntry> Games { get; set; } = [];
    }

    public sealed class TrackedGameCacheEntry
    {
        public string GameName { get; set; } = string.Empty;

        public string ExecutableName { get; set; } = string.Empty;

        public string? ExecutablePath { get; set; }

        public string? TodayDate { get; set; }

        public long TodayElapsedTicks { get; set; }

        public long TotalElapsedTicks { get; set; }

        public long ElapsedTicks { get; set; }
    }
}
