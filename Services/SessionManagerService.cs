using System;
using System.Linq;
using System.Threading.Tasks;
using JpnStudyTool.Models;
using Microsoft.Data.Sqlite;

namespace JpnStudyTool.Services
{
    internal class SessionManagerService
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private string? _activeSessionId = null;
        private string _freeSessionId = string.Empty;
        private AppSettings _currentAppSettings;

        public string? ActiveSessionId => _activeSessionId;
        public bool IsFreeSessionActive => _activeSessionId == _freeSessionId && !string.IsNullOrEmpty(_freeSessionId);

        public event EventHandler? ActiveSessionChanged;

        public SessionManagerService(DatabaseService dbService, SettingsService settingsService)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _currentAppSettings = _settingsService.LoadSettings();
        }
        public async Task InitializeAsync()
        {
            System.Diagnostics.Debug.WriteLine("[SessionManager] Initializing...");
            _freeSessionId = await _dbService.GetOrCreateFreeSessionAsync();
            _activeSessionId = null;
            _currentAppSettings.ActiveSessionId = null;
            _settingsService.SaveSettings(_currentAppSettings);
            System.Diagnostics.Debug.WriteLine($"[SessionManager] Initialization complete. No session auto-started. Free Session ID: {_freeSessionId}");
        }
        public async Task SetActiveSessionAsync(string sessionId)
        {
            await _dbService.ClearActiveSessionMarkerAsync();
            string now = DateTime.UtcNow.ToString("o");
            string sql = "UPDATE Sessions SET IsActive = 1, LastModified = @LastModified WHERE SessionId = @SessionId;";
            await _dbService.ExecuteNonQueryAsync(sql,
               new SqliteParameter("@SessionId", sessionId),
               new SqliteParameter("@LastModified", now));
            _activeSessionId = sessionId;
            System.Diagnostics.Debug.WriteLine($"[SessionManager] Set Active Session in DB: {sessionId}");
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine($"[SessionManager] ActiveSessionChanged event fired.");
        }

        public async Task StartExistingSessionAsync(string sessionId)
        {
            if (sessionId == _activeSessionId)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Session {sessionId} is already active.");
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[SessionManager StartExistingSessionAsync] Starting existing session: {sessionId}");
            if (!string.IsNullOrEmpty(_activeSessionId))
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager StartExistingSessionAsync] Ending previously active session: {_activeSessionId}");
                await _dbService.EndSessionAsync(_activeSessionId);
            }
            await SetActiveSessionAsync(sessionId);
            _currentAppSettings.ActiveSessionId = _activeSessionId;
            _settingsService.SaveSettings(_currentAppSettings);
            System.Diagnostics.Debug.WriteLine($"[SessionManager StartExistingSessionAsync] Session {sessionId} started and saved to settings.");
        }
        public async Task<string> CreateAndStartNewSessionAsync(string? name, string? imagePath)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionManager] Creating and starting new session: {name ?? "Unnamed"}");
            if (!string.IsNullOrEmpty(_activeSessionId))
            {
                await _dbService.EndSessionAsync(_activeSessionId);
            }
            string newSessionId = await _dbService.CreateNewSessionAsync(name, imagePath);
            await SetActiveSessionAsync(newSessionId);
            _currentAppSettings.ActiveSessionId = _activeSessionId;
            _settingsService.SaveSettings(_currentAppSettings);
            System.Diagnostics.Debug.WriteLine($"[SessionManager] New session created and started: {newSessionId}");
            return newSessionId;
        }

        public async Task StartFreeSessionAsync()
        {
            await StartExistingSessionAsync(_freeSessionId);
            System.Diagnostics.Debug.WriteLine($"[SessionManager] Explicitly started Free Session: {_freeSessionId}");
        }

        public async Task EndCurrentSessionAsync()
        {
            if (!string.IsNullOrEmpty(_activeSessionId))
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Ending current session: {_activeSessionId}");
                await _dbService.EndSessionAsync(_activeSessionId);
                _activeSessionId = null;
                _currentAppSettings.ActiveSessionId = null;
                _settingsService.SaveSettings(_currentAppSettings);
                ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Current session ended. No active session set.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] EndCurrentSessionAsync called but no session was active.");
            }
        }
        public async Task EndAndSaveCurrentSessionAsync()
        {
            if (!string.IsNullOrEmpty(_activeSessionId))
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Ending session on app close: {_activeSessionId}");
                await _dbService.EndSessionAsync(_activeSessionId);
            }
            AppSettings freshestSettings = _settingsService.LoadSettings();
            freshestSettings.ActiveSessionId = null;
            _settingsService.SaveSettings(freshestSettings);
            System.Diagnostics.Debug.WriteLine($"[SessionManager] Cleared ActiveSessionId in (freshest) settings for app close.");
            _activeSessionId = null;
        }
        public async Task AddSentenceToCurrentSessionAsync(string sentence)
        {
            if (string.IsNullOrEmpty(_activeSessionId))
            {
                System.Diagnostics.Debug.WriteLine("[SessionManager] Cannot add sentence, no active session ID!");
                return;
            }
            await _dbService.AddSessionEntryAsync(_activeSessionId, sentence);
        }
        public async Task HandleGlobalOpenTriggerAsync()
        {
            _currentAppSettings = _settingsService.LoadSettings();
            if (_currentAppSettings.AiAnalysisTriggerMode == AiAnalysisTrigger.OnGlobalOpen &&
                _currentAppSettings.UseAIMode &&
                !string.IsNullOrWhiteSpace(_currentAppSettings.GeminiApiKey) &&
                !string.IsNullOrEmpty(_activeSessionId))
            {
                System.Diagnostics.Debug.WriteLine($"[SessionManager] Handling Global Open Trigger for session {_activeSessionId}...");
                var entries = await _dbService.GetEntriesForSessionAsync(_activeSessionId);
                if (entries.Any())
                {
                    var lastEntry = entries.Last();
                    System.Diagnostics.Debug.WriteLine($"[SessionManager] Global Open Trigger confirmed. ViewModel should handle caching for last sentence of session {_activeSessionId} if needed.");
                    if (App.ViewModel != null)
                    {
                        var historyItem = App.ViewModel.CurrentSessionEntries.FirstOrDefault(h => h.Sentence == lastEntry.SentenceText);
                        if (historyItem != null)
                        {
                            _ = App.ViewModel.CacheAiAnalysisAsync(lastEntry.SentenceText, historyItem);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[SessionManager] Global Open Trigger: Could not find matching UI item for sentence.");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[SessionManager] Global Open Trigger: App.ViewModel is null.");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionManager] Global Open Trigger: No sentences found in active session {_activeSessionId}.");
                }
            }
        }
        public async Task AddTokensToCurrentSessionAsync(int tokenCount)
        {
            if (tokenCount > 0 && !string.IsNullOrEmpty(_activeSessionId))
            {
                await _dbService.UpdateSessionTokenCountAsync(_activeSessionId, tokenCount);
            }
        }
    }
}