// Services/DatabaseService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Windows.Storage;

namespace JpnStudyTool.Services
{
    public record SessionInfo(string SessionId, string? Name, string? ImagePath, string StartTime, string? EndTime, int TotalTokensUsedSession, string LastModified, bool IsDefaultFreeSession);
    public record SessionEntryInfo(long EntryId, string SentenceText, string TimestampCaptured, string? TimestampDetailOpened, string? AnalysisTypeUsed, string? AnalysisResultJson, bool IsAiAnalysisCachedAtSave);


    internal class DatabaseService
    {
        private readonly string _dbPath;
        private readonly SqliteConnectionStringBuilder _connectionStringBuilder;

        private const string FreeSessionNameMarker = "##FREE_SESSION##";

        private static readonly Lazy<DatabaseService> _instance = new Lazy<DatabaseService>(() => new DatabaseService());
        public static DatabaseService Instance => _instance.Value;

        private DatabaseService()
        {
            string dbFileName = "JpnStudyToolData.db";
            _dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, dbFileName);
            _connectionStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            };
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] DB Path: {_dbPath}");
        }

        private SqliteConnection GetConnection()
        {
            var connection = new SqliteConnection(_connectionStringBuilder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
            return connection;
        }

        internal async Task ExecuteNonQueryAsync(string sql, params SqliteParameter[] parameters)
        {
            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (parameters != null)
                {
                    command.Parameters.AddRange(parameters);
                }
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] ExecuteNonQueryAsync Error: {ex.Message} | SQL: {sql}");
                throw;
            }
        }

        private async Task<T?> ExecuteScalarAsync<T>(string sql, params SqliteParameter[] parameters)
        {
            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (parameters != null)
                {
                    command.Parameters.AddRange(parameters);
                }
                var result = await command.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    if (typeof(T) == typeof(long) && result is long longResult) return (T)(object)longResult;
                    if (typeof(T) == typeof(int) && result is long intResult) return (T)(object)Convert.ToInt32(intResult);
                    if (typeof(T) == typeof(string) && result is string strResult) return (T)(object)strResult;
                    if (typeof(T) == typeof(bool) && result is long boolResult) return (T)(object)(boolResult == 1);
                    if (typeof(T) == typeof(int?) && result is long nullableIntResult) return (T)(object)Convert.ToInt32(nullableIntResult);

                    return (T)Convert.ChangeType(result, typeof(T));
                }
                return default;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] ExecuteScalarAsync Error: {ex.Message} | SQL: {sql}");
                throw;
            }
        }

        public async Task InitializeDatabaseAsync()
        {
            System.Diagnostics.Debug.WriteLine("[DatabaseService] Initializing Database...");
            string createSessionsTableSql = @"
                CREATE TABLE IF NOT EXISTS Sessions (
                    SessionId TEXT PRIMARY KEY NOT NULL,
                    Name TEXT NULL,
                    ImagePath TEXT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 0,
                    IsDefaultFreeSession INTEGER NOT NULL DEFAULT 0,
                    TotalTokensUsedSession INTEGER NOT NULL DEFAULT 0,
                    LastModified TEXT NOT NULL
                );";

            string createSessionEntriesTableSql = @"
                CREATE TABLE IF NOT EXISTS SessionEntries (
                    EntryId INTEGER PRIMARY KEY AUTOINCREMENT,
                    FK_SessionId TEXT NOT NULL,
                    SentenceText TEXT NOT NULL,
                    TimestampCaptured TEXT NOT NULL,
                    TimestampDetailOpened TEXT NULL,
                    AnalysisTypeUsed TEXT NULL,
                    AnalysisResultJson TEXT NULL,
                    IsAiAnalysisCachedAtSave INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (FK_SessionId) REFERENCES Sessions(SessionId) ON DELETE CASCADE
                );";

            string createSessionIndexSql = @"CREATE INDEX IF NOT EXISTS IX_Sessions_IsActive ON Sessions(IsActive);";
            string createEntryIndexSql = @"CREATE INDEX IF NOT EXISTS IX_SessionEntries_SessionId ON SessionEntries(FK_SessionId);";


            await ExecuteNonQueryAsync(createSessionsTableSql);
            await ExecuteNonQueryAsync(createSessionEntriesTableSql);
            await ExecuteNonQueryAsync(createSessionIndexSql);
            await ExecuteNonQueryAsync(createEntryIndexSql);

            await GetOrCreateFreeSessionAsync();
            System.Diagnostics.Debug.WriteLine("[DatabaseService] Database Initialized.");
        }
        public async Task<string> GetOrCreateFreeSessionAsync()
        {
            string sql = "SELECT SessionId FROM Sessions WHERE IsDefaultFreeSession = 1 LIMIT 1;";
            var existingId = await ExecuteScalarAsync<string>(sql);

            if (!string.IsNullOrEmpty(existingId))
            {
                return existingId;
            }
            else
            {
                string newGuid = Guid.NewGuid().ToString();
                string now = DateTime.UtcNow.ToString("o");
                string insertSql = @"
                    INSERT INTO Sessions (SessionId, StartTime, IsActive, IsDefaultFreeSession, LastModified, Name)
                    VALUES (@SessionId, @StartTime, 0, 1, @LastModified, @Name);";

                await ExecuteNonQueryAsync(insertSql,
                    new SqliteParameter("@SessionId", newGuid),
                    new SqliteParameter("@StartTime", now),
                    new SqliteParameter("@LastModified", now),
                    new SqliteParameter("@Name", FreeSessionNameMarker));

                System.Diagnostics.Debug.WriteLine($"[DatabaseService] Created Free Session with ID: {newGuid}");
                return newGuid;
            }
        }

        public async Task ClearActiveSessionMarkerAsync()
        {
            string sql = "UPDATE Sessions SET IsActive = 0 WHERE IsActive = 1;";
            await ExecuteNonQueryAsync(sql);
            System.Diagnostics.Debug.WriteLine("[DatabaseService] Cleared active session markers.");
        }

        public async Task<string?> GetActiveSessionIdAsync()
        {
            string sql = "SELECT SessionId FROM Sessions WHERE IsActive = 1 LIMIT 1;";
            return await ExecuteScalarAsync<string>(sql);
        }


        public async Task SetActiveSessionAsync(string sessionId)
        {
            await ClearActiveSessionMarkerAsync();

            string now = DateTime.UtcNow.ToString("o");
            string sql = "UPDATE Sessions SET IsActive = 1, LastModified = @LastModified WHERE SessionId = @SessionId;";
            await ExecuteNonQueryAsync(sql,
               new SqliteParameter("@SessionId", sessionId),
               new SqliteParameter("@LastModified", now));
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] Set Active Session: {sessionId}");
        }

        public async Task EndSessionAsync(string sessionId)
        {
            string now = DateTime.UtcNow.ToString("o");
            string sql = @"
                UPDATE Sessions
                SET IsActive = 0, EndTime = @EndTime, LastModified = @LastModified
                WHERE SessionId = @SessionId AND IsActive = 1;";

            int rowsAffected = 0;
            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@EndTime", now);
                command.Parameters.AddWithValue("@LastModified", now);
                command.Parameters.AddWithValue("@SessionId", sessionId);
                rowsAffected = await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] EndSessionAsync Error: {ex.Message} | SQL: {sql}");
                throw;
            }

            if (rowsAffected > 0)
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] Ended Session: {sessionId}");
            else
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] Attempted to end session {sessionId}, but it was not active or not found.");
        }

        public async Task<string> CreateNewSessionAsync(string? name, string? imagePath)
        {
            string newGuid = Guid.NewGuid().ToString();
            string now = DateTime.UtcNow.ToString("o");
            string insertSql = @"
                INSERT INTO Sessions (SessionId, Name, ImagePath, StartTime, IsActive, IsDefaultFreeSession, LastModified)
                VALUES (@SessionId, @Name, @ImagePath, @StartTime, 0, 0, @LastModified);";

            await ExecuteNonQueryAsync(insertSql,
                new SqliteParameter("@SessionId", newGuid),
                new SqliteParameter("@Name", (object)name ?? DBNull.Value),
                new SqliteParameter("@ImagePath", (object)imagePath ?? DBNull.Value),
                new SqliteParameter("@StartTime", now),
                new SqliteParameter("@LastModified", now));

            System.Diagnostics.Debug.WriteLine($"[DatabaseService] Created New Session: {name ?? "Unnamed"} (ID: {newGuid})");
            return newGuid;
        }

        public async Task<List<SessionInfo>> GetAllNamedSessionsAsync()
        {
            var sessions = new List<SessionInfo>();
            string sql = @"
                SELECT SessionId, Name, ImagePath, StartTime, EndTime, TotalTokensUsedSession, LastModified, IsDefaultFreeSession
                FROM Sessions
                WHERE IsDefaultFreeSession = 0
                ORDER BY LastModified DESC;";

            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sessions.Add(new SessionInfo(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt32(5),
                        reader.GetString(6),
                        reader.GetInt64(7) == 1
                    ));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] GetAllNamedSessionsAsync Error: {ex.Message} | SQL: {sql}");
                throw;
            }
            return sessions;
        }

        public async Task UpdateSessionTokenCountAsync(string sessionId, int additionalTokens)
        {
            if (additionalTokens <= 0) return;
            string sql = "UPDATE Sessions SET TotalTokensUsedSession = TotalTokensUsedSession + @Tokens WHERE SessionId = @SessionId;";
            await ExecuteNonQueryAsync(sql,
               new SqliteParameter("@Tokens", additionalTokens),
               new SqliteParameter("@SessionId", sessionId));
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] Updated token count for Session {sessionId} by {additionalTokens}.");
        }

        public async Task DeleteSessionAsync(string sessionId)
        {
            string sql = "DELETE FROM Sessions WHERE SessionId = @SessionId AND IsDefaultFreeSession = 0;";
            await ExecuteNonQueryAsync(sql, new SqliteParameter("@SessionId", sessionId));
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] Deleted Session (if exists and not Free): {sessionId}");
        }

        public async Task AddSessionEntryAsync(string sessionId, string sentenceText)
        {
            string now = DateTime.UtcNow.ToString("o");
            string insertSql = @"
                INSERT INTO SessionEntries (FK_SessionId, SentenceText, TimestampCaptured)
                VALUES (@SessionId, @SentenceText, @TimestampCaptured);";

            await ExecuteNonQueryAsync(insertSql,
                new SqliteParameter("@SessionId", sessionId),
                new SqliteParameter("@SentenceText", sentenceText),
                new SqliteParameter("@TimestampCaptured", now));
        }

        public async Task<List<SessionEntryInfo>> GetEntriesForSessionAsync(string sessionId)
        {
            var entries = new List<SessionEntryInfo>();
            string sql = @"
                SELECT EntryId, SentenceText, TimestampCaptured, TimestampDetailOpened, AnalysisTypeUsed, AnalysisResultJson, IsAiAnalysisCachedAtSave
                FROM SessionEntries
                WHERE FK_SessionId = @SessionId
                ORDER BY TimestampCaptured ASC;";

            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@SessionId", sessionId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    entries.Add(new SessionEntryInfo(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetInt64(6) == 1
                    ));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] GetEntriesForSessionAsync Error: {ex.Message} | SQL: {sql}");
                throw;
            }
            return entries;
        }

        public async Task UpdateSessionEntryDetailsAsync(long entryId, string analysisType, string analysisResultJson, bool isAiCached)
        {
            string now = DateTime.UtcNow.ToString("o");
            string sql = @"
                UPDATE SessionEntries
                SET TimestampDetailOpened = @TimestampDetailOpened,
                    AnalysisTypeUsed = @AnalysisTypeUsed,
                    AnalysisResultJson = @AnalysisResultJson,
                    IsAiAnalysisCachedAtSave = @IsAiAnalysisCachedAtSave
                WHERE EntryId = @EntryId;";

            await ExecuteNonQueryAsync(sql,
               new SqliteParameter("@TimestampDetailOpened", now),
               new SqliteParameter("@AnalysisTypeUsed", analysisType),
               new SqliteParameter("@AnalysisResultJson", analysisResultJson),
               new SqliteParameter("@IsAiAnalysisCachedAtSave", isAiCached ? 1 : 0),
               new SqliteParameter("@EntryId", entryId));
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] Updated details for Session Entry: {entryId}");
        }


        public async Task DeleteSessionEntryAsync(long entryId)
        {
            string sql = "DELETE FROM SessionEntries WHERE EntryId = @EntryId;";
            await ExecuteNonQueryAsync(sql, new SqliteParameter("@EntryId", entryId));
            System.Diagnostics.Debug.WriteLine($"[DatabaseService] Deleted Session Entry: {entryId}");
        }

        public async Task<(int today, int last30, int total)> GetGlobalTokenCountsAsync()
        {
            string todaySql = "SELECT SUM(TotalTokensUsedSession) FROM Sessions WHERE DATE(StartTime) = DATE('now');";
            string last30Sql = "SELECT SUM(TotalTokensUsedSession) FROM Sessions WHERE DATE(StartTime) >= DATE('now', '-30 days');";
            string totalSql = "SELECT SUM(TotalTokensUsedSession) FROM Sessions;";

            int todayCount = await ExecuteScalarAsync<int?>(todaySql) ?? 0;
            int last30Count = await ExecuteScalarAsync<int?>(last30Sql) ?? 0;
            int totalCount = await ExecuteScalarAsync<int?>(totalSql) ?? 0;

            return (todayCount, last30Count, totalCount);
        }

        public async Task<Dictionary<DateTime, int>> GetActivityCountsAsync(int days = 365)
        {
            var activity = new Dictionary<DateTime, int>();
            string sql = $@"
                SELECT DATE(TimestampDetailOpened) as ActivityDay, COUNT(EntryId) as ActivityCount
                FROM SessionEntries
                WHERE TimestampDetailOpened IS NOT NULL
                  AND DATE(TimestampDetailOpened) >= DATE('now', '-{days} days')
                GROUP BY ActivityDay;";

            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                    {
                        if (DateTime.TryParse(reader.GetString(0), out var date))
                        {
                            activity[date.Date] = reader.GetInt32(1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] GetActivityCountsAsync Error: {ex.Message} | SQL: {sql}");
            }
            return activity;
        }

        public async Task<SessionInfo?> GetSessionByIdOrDefaultAsync(string sessionId)
        {
            SessionInfo? session = null;
            string sql = @"
        SELECT SessionId, Name, ImagePath, StartTime, EndTime, TotalTokensUsedSession, LastModified, IsDefaultFreeSession 
        FROM Sessions
        WHERE SessionId = @SessionId
        LIMIT 1;";

            try
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@SessionId", sessionId);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    session = new SessionInfo(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt32(5),
                        reader.GetString(6),
                        reader.GetInt64(7) == 1
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] GetSessionByIdOrDefaultAsync Error for ID {sessionId}: {ex.Message}");
            }
            return session;
        }


    }
}