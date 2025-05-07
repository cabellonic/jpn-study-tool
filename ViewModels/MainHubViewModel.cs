// ViewModels/MainHubViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JpnStudyTool.Services;
using JpnStudyTool.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Gaming.Input;
using Windows.Storage;

namespace JpnStudyTool.ViewModels
{
    public partial class SessionDisplayItem : ObservableObject
    {
        public string SessionId { get; }

        [ObservableProperty]
        private string? _name;

        [ObservableProperty]
        private string? _imagePath;

        [ObservableProperty]
        private string? _startTimeDisplay;

        [ObservableProperty]
        private string? _endTimeDisplay;

        [ObservableProperty]
        private int _totalTokensUsedSession;

        [ObservableProperty]
        private string _lastModifiedDisplay;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDefaultImage))]
        private bool _hasImagePath;

        public bool ShowDefaultImage => !HasImagePath;

        [ObservableProperty]
        private BitmapImage? _displayImageSource;

        public SessionDisplayItem(SessionInfo sessionInfo)
        {
            SessionId = sessionInfo.SessionId;
            Name = sessionInfo.Name ?? "Unnamed Session";
            ImagePath = sessionInfo.ImagePath;
            TotalTokensUsedSession = sessionInfo.TotalTokensUsedSession;
            HasImagePath = false;
            DisplayImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
            StartTimeDisplay = "Loading...";
            EndTimeDisplay = "-";
            LastModifiedDisplay = "Loading...";
        }

        public async Task LoadDetailsAsync(SessionInfo sessionInfo)
        {
            Name = sessionInfo.Name ?? "Unnamed Session";
            ImagePath = sessionInfo.ImagePath;
            TotalTokensUsedSession = sessionInfo.TotalTokensUsedSession;

            try { StartTimeDisplay = DateTime.Parse(sessionInfo.StartTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g"); } catch { StartTimeDisplay = "Invalid Date"; }
            try { EndTimeDisplay = !string.IsNullOrEmpty(sessionInfo.EndTime) ? DateTime.Parse(sessionInfo.EndTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g") : (sessionInfo.IsDefaultFreeSession ? "-" : "Interrupted"); } catch { EndTimeDisplay = "-"; }
            try { LastModifiedDisplay = DateTime.Parse(sessionInfo.LastModified, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g"); } catch { LastModifiedDisplay = "Invalid Date"; }

            bool imageSuccessfullyLoaded = false;
            BitmapImage? newImageSource = null;

            if (!string.IsNullOrEmpty(ImagePath))
            {
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(ImagePath);
                    newImageSource = new BitmapImage();
                    using (var stream = await file.OpenAsync(FileAccessMode.Read))
                    {
                        await newImageSource.SetSourceAsync(stream);
                    }
                    imageSuccessfullyLoaded = true;
                }
                catch (Exception) { imageSuccessfullyLoaded = false; }
            }

            if (imageSuccessfullyLoaded && newImageSource != null)
            {
                DisplayImageSource = newImageSource;
                HasImagePath = true;
            }
            else
            {
                DisplayImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
                HasImagePath = false;
            }
        }
    }

    public partial class MainHubViewModel : ObservableObject
    {
        private readonly DatabaseService _dbService;
        private readonly SessionManagerService _sessionManager;
        private readonly DispatcherQueue _dispatcherQueue;

        [ObservableProperty]
        private ObservableCollection<SessionDisplayItem> _recentSessions = new();

        [ObservableProperty] private int _globalTokensToday = 0;
        [ObservableProperty] private int _globalTokensLast30 = 0;
        [ObservableProperty] private int _globalTokensTotal = 0;
        [ObservableProperty] private string _connectedJoystickName = "Joystick: None";
        [ObservableProperty] private bool _isLoading = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEndSession))]
        private bool _isSessionCurrentlyActive = false;

        [ObservableProperty]
        private string _activeSessionDisplayName = "No active session";

        public bool CanEndSession => IsSessionCurrentlyActive;

        public IAsyncRelayCommand LoadDataCommand { get; }
        public IAsyncRelayCommand<string> ContinueSessionCommand { get; }
        public IAsyncRelayCommand CreateNewSessionCommand { get; }
        public IAsyncRelayCommand StartFreeSessionCommand { get; }
        public IRelayCommand GoToVocabularyCommand { get; }
        public IRelayCommand GoToSettingsCommand { get; }
        public IAsyncRelayCommand EndActiveSessionCommand { get; }

        public MainHubViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Cannot get DispatcherQueue for current thread.");
            _dbService = DatabaseService.Instance;
            _sessionManager = App.SessionManager ?? throw new InvalidOperationException("Session Manager not initialized in App.");

            LoadDataCommand = new AsyncRelayCommand(LoadDataAsync);
            ContinueSessionCommand = new AsyncRelayCommand<string>(ContinueSessionAsync);
            CreateNewSessionCommand = new AsyncRelayCommand(CreateNewSessionAsync);
            StartFreeSessionCommand = new AsyncRelayCommand(StartFreeSessionAsync);
            GoToVocabularyCommand = new RelayCommand(NavigateToVocabulary);
            GoToSettingsCommand = new RelayCommand(NavigateToSettings);
            EndActiveSessionCommand = new AsyncRelayCommand(EndSessionAndRefreshAsync, () => CanEndSession);

            _sessionManager.ActiveSessionChanged += SessionManager_ActiveSessionChanged_Hub;
            UpdateSessionStatusDisplay();
            TryRegisterGamepadEvents();
            UpdateJoystickStatus();
        }

        private async Task EndSessionAndRefreshAsync()
        {
            if (!IsSessionCurrentlyActive) return;
            await _sessionManager.EndCurrentSessionAsync();
        }

        private void SessionManager_ActiveSessionChanged_Hub(object? sender, EventArgs e)
        {
            _dispatcherQueue?.TryEnqueue(UpdateSessionStatusDisplay);
        }

        private async void UpdateSessionStatusDisplay()
        {
            string? currentActiveId = _sessionManager.ActiveSessionId;
            IsSessionCurrentlyActive = !string.IsNullOrEmpty(currentActiveId);

            if (!IsSessionCurrentlyActive)
            {
                ActiveSessionDisplayName = "No active session";
            }
            else if (_sessionManager.IsFreeSessionActive)
            {
                ActiveSessionDisplayName = "Active: Free Session";
            }
            else
            {
                var activeSessionFromList = RecentSessions.FirstOrDefault(s => s.SessionId == currentActiveId);
                if (activeSessionFromList != null)
                {
                    ActiveSessionDisplayName = $"Active: {activeSessionFromList.Name}";
                }
                else
                {
                    var sessionsFromDb = await _dbService.GetAllNamedSessionsAsync();
                    var sessionFromDb = sessionsFromDb.FirstOrDefault(s => s.SessionId == currentActiveId);
                    ActiveSessionDisplayName = sessionFromDb != null ? $"Active: {sessionFromDb.Name}" : $"Active Session: ID {currentActiveId?.Substring(0, 8)}...";
                }
            }
            EndActiveSessionCommand.NotifyCanExecuteChanged();
        }

        public void Cleanup()
        {
            TryUnregisterGamepadEvents();
            if (_sessionManager != null) _sessionManager.ActiveSessionChanged -= SessionManager_ActiveSessionChanged_Hub;
        }
        private void TryRegisterGamepadEvents() { try { Gamepad.GamepadAdded += OnGamepadConnectionChanged; Gamepad.GamepadRemoved += OnGamepadConnectionChanged; } catch (Exception) { } }
        private void TryUnregisterGamepadEvents() { try { Gamepad.GamepadAdded -= OnGamepadConnectionChanged; Gamepad.GamepadRemoved -= OnGamepadConnectionChanged; } catch (Exception) { } }

        public async Task LoadDataAsync()
        {
            if (!_dispatcherQueue.HasThreadAccess) { await DispatcherQueue_EnqueueAsync(LoadDataAsync); return; }
            IsLoading = true;
            RecentSessions.Clear();
            try
            {
                var (today, last30, total) = await _dbService.GetGlobalTokenCountsAsync();
                GlobalTokensToday = today; GlobalTokensLast30 = last30; GlobalTokensTotal = total;
                UpdateJoystickStatus();
                UpdateSessionStatusDisplay();

                var sessionsFromDb = await _dbService.GetAllNamedSessionsAsync();
                var sessionInfosToDisplay = sessionsFromDb
                    .OrderByDescending(s => DateTime.Parse(s.LastModified, null, System.Globalization.DateTimeStyles.RoundtripKind))
                    .ToList();

                List<SessionDisplayItem> itemsToAdd = sessionInfosToDisplay.Select(s => new SessionDisplayItem(s)).ToList();
                foreach (var item in itemsToAdd) { RecentSessions.Add(item); }

                if (itemsToAdd.Any())
                {
                    var loadTasks = itemsToAdd.Select((item, index) => item.LoadDetailsAsync(sessionInfosToDisplay[index])).ToList();
                    _ = Task.Run(() => Task.WhenAll(loadTasks));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainHubVM LoadDataAsync] CRITICAL Error: {ex.Message}\n{ex.StackTrace}"); }
            finally { IsLoading = false; }
        }

        private async Task ContinueSessionAsync(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || IsLoading) return;
            IsLoading = true;
            try
            {
                await _sessionManager.StartExistingSessionAsync(sessionId);
                if (App.MainWin != null) { await App.MainWin.NavigateToSessionViewAsync(); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainHubVM ContinueSessionAsync] Error: {ex.Message}"); }
            finally { IsLoading = false; }
        }

        private async Task CreateNewSessionAsync()
        {
            if (IsLoading) return;
            XamlRoot? xamlRoot = null;
            if (App.MainWin?.Content is Frame rootFrame && rootFrame.Content is Page currentPage) { xamlRoot = currentPage.XamlRoot; }
            else if (App.MainWin != null) { xamlRoot = App.MainWin.Content.XamlRoot; }
            if (xamlRoot == null) { System.Diagnostics.Debug.WriteLine("[MainHubVM CreateNew] XamlRoot not found."); return; }

            var dialog = new NewSessionDialog { XamlRoot = xamlRoot };
            ContentDialogResult result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                string sessionName = dialog.SessionName;
                string? imagePath = dialog.SelectedImagePath;
                IsLoading = true;
                try
                {
                    await _sessionManager.CreateAndStartNewSessionAsync(sessionName, imagePath);
                    if (App.MainWin != null) { await App.MainWin.NavigateToSessionViewAsync(); }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainHubVM CreateNew] Error: {ex.Message}"); }
                finally { IsLoading = false; }
            }
        }

        private async Task StartFreeSessionAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                await _sessionManager.StartFreeSessionAsync();
                if (App.MainWin != null) { await App.MainWin.NavigateToSessionViewAsync(); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainHubVM StartFreeSessionAsync] Error: {ex.Message}"); }
            finally { IsLoading = false; }
        }

        private void NavigateToVocabulary() { System.Diagnostics.Debug.WriteLine("[MainHubVM] TODO: Navigate to Vocabulary View"); }
        private void NavigateToSettings() { App.MainWin?.ShowSettingsWindow(); }
        private void UpdateJoystickStatus() { try { ConnectedJoystickName = Gamepad.Gamepads.Any() ? $"Joystick: Connected" : "Joystick: None"; } catch (Exception) { ConnectedJoystickName = "Joystick: Error"; } }
        private void OnGamepadConnectionChanged(object? sender, Gamepad e) { _dispatcherQueue?.TryEnqueue(UpdateJoystickStatus); }

        private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction) { var tcs = new TaskCompletionSource(); if (!_dispatcherQueue.TryEnqueue(async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue async action.")); } return tcs.Task; }
    }
}