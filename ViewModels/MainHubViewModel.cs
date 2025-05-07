// ViewModels/MainHubViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JpnStudyTool.Services;
using Microsoft.UI.Dispatching;
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


        private Uri? _originalImageUri;


        public SessionDisplayItem(SessionInfo sessionInfo)
        {
            SessionId = sessionInfo.SessionId;
            Name = sessionInfo.Name ?? "Unnamed Session";
            ImagePath = sessionInfo.ImagePath;
            TotalTokensUsedSession = sessionInfo.TotalTokensUsedSession;

            StartTimeDisplay = "Loading...";
            EndTimeDisplay = "-";
            LastModifiedDisplay = "Loading...";

            HasImagePath = !string.IsNullOrEmpty(ImagePath);

            DisplayImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
        }


        public async Task LoadDetailsAsync(SessionInfo sessionInfo)
        {
            try
            {

                Name = sessionInfo.Name ?? "Unnamed Session";
                ImagePath = sessionInfo.ImagePath;
                TotalTokensUsedSession = sessionInfo.TotalTokensUsedSession;
                try { StartTimeDisplay = DateTime.Parse(sessionInfo.StartTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g"); } catch { StartTimeDisplay = "Invalid Date"; }
                try { EndTimeDisplay = !string.IsNullOrEmpty(sessionInfo.EndTime) ? DateTime.Parse(sessionInfo.EndTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g") : "Interrupted"; } catch { EndTimeDisplay = "-"; }
                try { LastModifiedDisplay = DateTime.Parse(sessionInfo.LastModified, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("g"); } catch { LastModifiedDisplay = "Invalid Date"; }


                bool imageSuccessfullyLoaded = false;
                BitmapImage? newImageSource = null;
                Uri? newUri = null;

                if (!string.IsNullOrEmpty(ImagePath))
                {
                    try
                    {

                        StorageFile file = await StorageFile.GetFileFromPathAsync(ImagePath);
                        newUri = new Uri(ImagePath);
                        newImageSource = new BitmapImage(newUri);
                        imageSuccessfullyLoaded = true;
                        System.Diagnostics.Debug.WriteLine($"[SessionDisplayItem {SessionId}] Image loaded from: {ImagePath}");
                    }
                    catch (System.IO.FileNotFoundException) { System.Diagnostics.Debug.WriteLine($"[SessionDisplayItem {SessionId}] Image file not found: {ImagePath}"); imageSuccessfullyLoaded = false; }
                    catch (UriFormatException) { System.Diagnostics.Debug.WriteLine($"[SessionDisplayItem {SessionId}] Invalid image path format: {ImagePath}"); imageSuccessfullyLoaded = false; }
                    catch (ArgumentException) { System.Diagnostics.Debug.WriteLine($"[SessionDisplayItem {SessionId}] ArgumentException for path: {ImagePath}"); imageSuccessfullyLoaded = false; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SessionDisplayItem {SessionId}] Error loading image {ImagePath}: {ex.Message}"); imageSuccessfullyLoaded = false; }
                }


                if (!imageSuccessfullyLoaded)
                {
                    newUri = new Uri("ms-appx:///Assets/default-session.png");
                    newImageSource = new BitmapImage(newUri);
                }


                if (imageSuccessfullyLoaded != HasImagePath) HasImagePath = imageSuccessfullyLoaded;
                if (newUri != _originalImageUri)
                {
                    _originalImageUri = newUri;
                    DisplayImageSource = newImageSource;
                }
            }
            catch (Exception outerEx)
            {

                System.Diagnostics.Debug.WriteLine($"[SessionDisplayItem {SessionId}] Error loading details: {outerEx.Message}");
                HasImagePath = false;
                DisplayImageSource = new BitmapImage(new Uri("ms-appx:///Assets/default-session.png"));
            }
        }
    }



    public partial class MainHubViewModel : ObservableObject
    {

        private readonly DatabaseService _dbService;
        private readonly SessionManagerService _sessionManager;
        private readonly SettingsService _settingsService;
        private readonly DispatcherQueue _dispatcherQueue;


        [ObservableProperty]
        private ObservableCollection<SessionDisplayItem> _recentSessions = new();

        [ObservableProperty] private int _globalTokensToday = 0;
        [ObservableProperty] private int _globalTokensLast30 = 0;
        [ObservableProperty] private int _globalTokensTotal = 0;
        [ObservableProperty] private string _connectedJoystickName = "Joystick: None";
        [ObservableProperty] private bool _isLoading = false;


        public IAsyncRelayCommand LoadDataCommand { get; }
        public IAsyncRelayCommand<string> ContinueSessionCommand { get; }
        public IAsyncRelayCommand CreateNewSessionCommand { get; }
        public IAsyncRelayCommand StartFreeSessionCommand { get; }
        public IRelayCommand GoToVocabularyCommand { get; }
        public IRelayCommand GoToSettingsCommand { get; }


        public MainHubViewModel()
        {

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Cannot get DispatcherQueue for current thread.");
            _dbService = DatabaseService.Instance;
            _settingsService = new SettingsService();
            _sessionManager = App.SessionManager ?? throw new InvalidOperationException("Session Manager not initialized in App.");


            LoadDataCommand = new AsyncRelayCommand(LoadDataAsync);
            ContinueSessionCommand = new AsyncRelayCommand<string>(ContinueSessionAsync);
            CreateNewSessionCommand = new AsyncRelayCommand(CreateNewSessionAsync);
            StartFreeSessionCommand = new AsyncRelayCommand(StartFreeSessionAsync);
            GoToVocabularyCommand = new RelayCommand(NavigateToVocabulary);
            GoToSettingsCommand = new RelayCommand(NavigateToSettings);


            TryRegisterGamepadEvents();
            UpdateJoystickStatus();
        }


        public void Cleanup() { TryUnregisterGamepadEvents(); System.Diagnostics.Debug.WriteLine("[MainHubVM] Cleanup called."); }
        private void TryRegisterGamepadEvents() { try { Gamepad.GamepadAdded += OnGamepadConnectionChanged; Gamepad.GamepadRemoved += OnGamepadConnectionChanged; } catch (Exception ex) { /* log */ } }
        private void TryUnregisterGamepadEvents() { try { Gamepad.GamepadAdded -= OnGamepadConnectionChanged; Gamepad.GamepadRemoved -= OnGamepadConnectionChanged; } catch (Exception ex) { /* log */ } }

        private async Task LoadDataAsync()
        {
            if (!_dispatcherQueue.HasThreadAccess) { await DispatcherQueue_EnqueueAsync(async () => await LoadDataAsync()); return; }
            IsLoading = true;
            RecentSessions.Clear();
            try
            {
                var (today, last30, total) = await _dbService.GetGlobalTokenCountsAsync();
                GlobalTokensToday = today; GlobalTokensLast30 = last30; GlobalTokensTotal = total;
                UpdateJoystickStatus();

                var sessionsFromDb = await _dbService.GetAllNamedSessionsAsync();
                var recentSessionInfos = sessionsFromDb
                    .OrderByDescending(s => DateTime.Parse(s.LastModified, null, System.Globalization.DateTimeStyles.RoundtripKind))
                    .Take(5).ToList();

                List<SessionDisplayItem> itemsToAdd = recentSessionInfos.Select(s => new SessionDisplayItem(s)).ToList();
                foreach (var item in itemsToAdd) { RecentSessions.Add(item); }

                var loadTasks = itemsToAdd.Select((item, index) => item.LoadDetailsAsync(recentSessionInfos[index])).ToList();
                _ = Task.Run(() => Task.WhenAll(loadTasks));

            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainHubVM] Error loading data: {ex.Message}"); }
            finally { IsLoading = false; }
        }

        private async Task ContinueSessionAsync(string? sessionId) { if (string.IsNullOrEmpty(sessionId) || IsLoading) return; IsLoading = true; try { await _sessionManager.StartExistingSessionAsync(sessionId); if (App.MainWin != null) { await App.MainWin.NavigateToSessionViewAsync(); } } catch (Exception ex) { /* log */ } finally { IsLoading = false; } }
        private async Task CreateNewSessionAsync() { if (IsLoading) return; IsLoading = true; try { string sessionName = "New Session " + DateTime.Now.ToString("yy-MM-dd HH:mm"); string? imagePath = null; /* TODO: Dialogo */ await _sessionManager.CreateAndStartNewSessionAsync(sessionName, imagePath); if (App.MainWin != null) { await App.MainWin.NavigateToSessionViewAsync(); } } catch (Exception ex) { /* log */ } finally { IsLoading = false; } }
        private async Task StartFreeSessionAsync() { if (IsLoading) return; IsLoading = true; try { await _sessionManager.StartFreeSessionAsync(); if (App.MainWin != null) { await App.MainWin.NavigateToSessionViewAsync(); } } catch (Exception ex) { /* log */ } finally { IsLoading = false; } }
        private void NavigateToVocabulary() { System.Diagnostics.Debug.WriteLine("[MainHubVM] TODO: Navigate to Vocabulary View"); }
        private void NavigateToSettings() { App.MainWin?.ShowSettingsWindow(); }

        private void UpdateJoystickStatus() { try { ConnectedJoystickName = Gamepad.Gamepads.Any() ? $"Joystick: Connected" : "Joystick: None"; } catch (Exception ex) { ConnectedJoystickName = "Joystick: Error"; } }
        private void OnGamepadConnectionChanged(object? sender, Gamepad e) { _dispatcherQueue?.TryEnqueue(UpdateJoystickStatus); }


        private Task DispatcherQueue_EnqueueAsync(Action function, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null on MainHubViewModel.")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, () => { try { function(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue action on MainHubViewModel.")); } return tcs.Task; }
        private Task DispatcherQueue_EnqueueAsync(Func<Task> asyncFunction, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) { var tcs = new TaskCompletionSource(); if (_dispatcherQueue == null) { tcs.SetException(new InvalidOperationException("DispatcherQueue is null on MainHubViewModel.")); return tcs.Task; } if (!_dispatcherQueue.TryEnqueue(priority, async () => { try { await asyncFunction(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } })) { tcs.SetException(new InvalidOperationException("Failed to enqueue async action on MainHubViewModel.")); } return tcs.Task; }
    }
}