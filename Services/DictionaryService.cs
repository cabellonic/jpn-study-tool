// Services/DictionaryService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using JpnStudyTool.Models;
using Microsoft.Data.Sqlite;
using Windows.ApplicationModel;

namespace JpnStudyTool.Services;

public class DictionaryService
{
    private readonly string _dbConnectionString;

    public DictionaryService()
    {
        string dbFileName = "JMDict.db";
        string packagePath = Package.Current.InstalledLocation.Path;
        string dbPath = Path.Combine(packagePath, "Data", dbFileName);

        if (!File.Exists(dbPath))
        {
            System.Diagnostics.Debug.WriteLine($"[DictionaryService] CRITICAL ERROR: Database file not found at {dbPath}");
            _dbConnectionString = string.Empty;
        }
        else
        {
            _dbConnectionString = $"Data Source={dbPath}";
            System.Diagnostics.Debug.WriteLine($"[DictionaryService] Database connection string set to: {_dbConnectionString}");
        }
    }

    public async Task<List<DictionaryEntry>> FindEntriesAsync(string term, string? reading)
    {
        var results = new List<DictionaryEntry>();
        if (string.IsNullOrEmpty(_dbConnectionString))
        {
            System.Diagnostics.Debug.WriteLine("[DictionaryService] Cannot search, connection string is invalid.");
            return results;
        }

        const string query = @"
            SELECT EntryID, Term, Reading, SequenceID, PopularityScore, DefinitionText, DefinitionHtml
            FROM Entries
            WHERE Term = @term OR Reading = @reading OR (Term = @reading AND Reading IS NULL)
            ORDER BY PopularityScore DESC
            LIMIT 10;";

        try
        {
            using var connection = new SqliteConnection(_dbConnectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = query;
            command.Parameters.AddWithValue("@term", term ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@reading", reading ?? term ?? (object)DBNull.Value);

            System.Diagnostics.Debug.WriteLine($"[DictionaryService] Executing query with Term='{term}', Reading='{reading}'");

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var entry = new DictionaryEntry
                {
                    EntryId = reader.GetInt64(reader.GetOrdinal("EntryID")),
                    Term = reader.GetString(reader.GetOrdinal("Term")),
                    Reading = reader.IsDBNull(reader.GetOrdinal("Reading")) ? null : reader.GetString(reader.GetOrdinal("Reading")),
                    SequenceId = reader.GetInt32(reader.GetOrdinal("SequenceID")),
                    PopularityScore = reader.GetDouble(reader.GetOrdinal("PopularityScore")),
                    DefinitionText = reader.IsDBNull(reader.GetOrdinal("DefinitionText")) ? null : reader.GetString(reader.GetOrdinal("DefinitionText")),
                    DefinitionHtml = reader.IsDBNull(reader.GetOrdinal("DefinitionHtml")) ? null : reader.GetString(reader.GetOrdinal("DefinitionHtml")),
                    DefinitionTags = new List<TagInfo>(),
                    TermTags = new List<TagInfo>()
                };

                results.Add(entry);

                entry.DefinitionTags = await GetTagsForEntryAsync(connection, entry.EntryId, "EntryDefinitionTags");
                entry.TermTags = await GetTagsForEntryAsync(connection, entry.EntryId, "EntryTermTags");
            }
            System.Diagnostics.Debug.WriteLine($"[DictionaryService] Found {results.Count} entries.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DictionaryService] Error finding entries: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
        }

        return results;
    }

    private async Task<List<TagInfo>> GetTagsForEntryAsync(SqliteConnection connection, long entryId, string junctionTableName)
    {
        var tags = new List<TagInfo>();
        if (junctionTableName != "EntryDefinitionTags" && junctionTableName != "EntryTermTags")
        {
            throw new ArgumentException("Invalid junction table name specified.", nameof(junctionTableName));
        }

        string tagQuery = $@"
            SELECT t.TagName, t.Category, t.Notes
            FROM Tags t
            JOIN {junctionTableName} et ON t.TagName = et.TagName
            WHERE et.EntryID = @entryId;";

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = tagQuery;
            command.Parameters.AddWithValue("@entryId", entryId);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tags.Add(new TagInfo
                {
                    TagName = reader.GetString(reader.GetOrdinal("TagName")),
                    Category = reader.IsDBNull(reader.GetOrdinal("Category")) ? null : reader.GetString(reader.GetOrdinal("Category")),
                    Notes = reader.IsDBNull(reader.GetOrdinal("Notes")) ? null : reader.GetString(reader.GetOrdinal("Notes")),
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DictionaryService] Error getting tags for EntryID {entryId} from {junctionTableName}: {ex.Message}");
        }
        return tags;
    }

}