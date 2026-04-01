using System.Data;
using Microsoft.Data.Sqlite;
using Playout.Core.Models;

namespace Playout.Core.Services;

public sealed class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(string dbPath = "playout.db")
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var commands = new[]
        {
            @"CREATE TABLE IF NOT EXISTS Media (
                Id TEXT PRIMARY KEY,
                Path TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Duration INTEGER NOT NULL,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                FpsNum INTEGER NOT NULL,
                FpsDen INTEGER NOT NULL,
                Category TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            )",
            @"CREATE TABLE IF NOT EXISTS Schedules (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                ScheduledDate TEXT NOT NULL,
                StartTimeUtc TEXT NOT NULL,
                Status INTEGER NOT NULL,
                IsLoop INTEGER NOT NULL
            )",
            @"CREATE TABLE IF NOT EXISTS PlaylistItems (
                Id TEXT PRIMARY KEY,
                ScheduleId TEXT NOT NULL,
                MediaId TEXT NOT NULL,
                MediaPath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                MarkIn INTEGER NOT NULL,
                MarkOut INTEGER NOT NULL,
                FixedStartUtc TEXT,
                StartType INTEGER NOT NULL,
                Duration INTEGER NOT NULL,
                Padding INTEGER NOT NULL,
                FOREIGN KEY(ScheduleId) REFERENCES Schedules(Id)
            )",
            @"CREATE TABLE IF NOT EXISTS CGElements (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Content TEXT NOT NULL,
                X REAL NOT NULL,
                Y REAL NOT NULL,
                Width REAL NOT NULL,
                Height REAL NOT NULL,
                IsVisible INTEGER NOT NULL,
                Style TEXT NOT NULL
            )"
        };

        foreach (var cmdText in commands)
        {
            using var command = connection.CreateCommand();
            command.CommandText = cmdText;
            command.ExecuteNonQuery();
        }
    }

    public async Task SaveMediaAsync(Media media)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Media (Id, Path, FileName, Duration, Width, Height, FpsNum, FpsDen, Category, CreatedAt)
            VALUES (@id, @path, @name, @dur, @w, @h, @fn, @fd, @cat, @ca)";
        command.Parameters.AddWithValue("@id", media.Id.ToString());
        command.Parameters.AddWithValue("@path", media.Path);
        command.Parameters.AddWithValue("@name", media.FileName);
        command.Parameters.AddWithValue("@dur", media.Duration.Ticks);
        command.Parameters.AddWithValue("@w", media.Width);
        command.Parameters.AddWithValue("@h", media.Height);
        command.Parameters.AddWithValue("@fn", media.FpsNum);
        command.Parameters.AddWithValue("@fd", media.FpsDen);
        command.Parameters.AddWithValue("@cat", media.Category);
        command.Parameters.AddWithValue("@ca", media.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveScheduleAsync(Schedule schedule)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT OR REPLACE INTO Schedules (Id, Name, ScheduledDate, StartTimeUtc, Status, IsLoop)
                VALUES (@id, @name, @date, @start, @status, @loop)";
            command.Parameters.AddWithValue("@id", schedule.Id.ToString());
            command.Parameters.AddWithValue("@name", schedule.Name);
            command.Parameters.AddWithValue("@date", schedule.ScheduledDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("@start", schedule.StartTimeUtc.ToString("O"));
            command.Parameters.AddWithValue("@status", (int)schedule.Status);
            command.Parameters.AddWithValue("@loop", schedule.IsLoop ? 1 : 0);
            await command.ExecuteNonQueryAsync();

            // Clear old items
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM PlaylistItems WHERE ScheduleId = @sid";
            deleteCmd.Parameters.AddWithValue("@sid", schedule.Id.ToString());
            await deleteCmd.ExecuteNonQueryAsync();

            // Insert new items
            foreach (var item in schedule.Items)
            {
                using var itemCmd = connection.CreateCommand();
                itemCmd.Transaction = transaction;
                itemCmd.CommandText = @"
                    INSERT INTO PlaylistItems (Id, ScheduleId, MediaId, MediaPath, FileName, SortOrder, MarkIn, MarkOut, FixedStartUtc, StartType, Duration, Padding)
                    VALUES (@id, @sid, @mid, @path, @name, @order, @mi, @mo, @start, @type, @dur, @pad)";
                itemCmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                itemCmd.Parameters.AddWithValue("@sid", schedule.Id.ToString());
                itemCmd.Parameters.AddWithValue("@mid", item.MediaId.ToString());
                itemCmd.Parameters.AddWithValue("@path", item.MediaPath);
                itemCmd.Parameters.AddWithValue("@name", item.FileName);
                itemCmd.Parameters.AddWithValue("@order", item.SortOrder);
                itemCmd.Parameters.AddWithValue("@mi", item.MarkIn.Ticks);
                itemCmd.Parameters.AddWithValue("@mo", item.MarkOut.Ticks);
                itemCmd.Parameters.AddWithValue("@start", item.FixedStartUtc?.ToString("O") ?? (object)DBNull.Value);
                itemCmd.Parameters.AddWithValue("@type", (int)item.StartType);
                itemCmd.Parameters.AddWithValue("@dur", item.Duration.Ticks);
                itemCmd.Parameters.AddWithValue("@pad", item.Padding.Ticks);
                await itemCmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<Schedule>> GetSchedulesForDateAsync(DateTime date)
    {
        var list = new List<Schedule>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Schedules WHERE ScheduledDate = @date";
        command.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Schedule
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                ScheduledDate = DateTime.Parse(reader.GetString(2)),
                StartTimeUtc = DateTimeOffset.Parse(reader.GetString(3)),
                Status = (ScheduleStatus)reader.GetInt32(4),
                IsLoop = reader.GetInt32(5) == 1
            });
        }
        return list;
    }

    public async Task<List<PlaylistItem>> GetPlaylistItemsAsync(Guid scheduleId)
    {
        var list = new List<PlaylistItem>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PlaylistItems WHERE ScheduleId = @sid ORDER BY SortOrder";
        command.Parameters.AddWithValue("@sid", scheduleId.ToString());
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new PlaylistItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                MediaId = Guid.Parse(reader.GetString(2)),
                MediaPath = reader.GetString(3),
                FileName = reader.GetString(4),
                SortOrder = reader.GetInt32(5),
                MarkIn = TimeSpan.FromTicks(reader.GetInt64(6)),
                MarkOut = TimeSpan.FromTicks(reader.GetInt64(7)),
                FixedStartUtc = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                StartType = (StartType)reader.GetInt32(9),
                Duration = TimeSpan.FromTicks(reader.GetInt64(10)),
                Padding = TimeSpan.FromTicks(reader.GetInt64(11))
            });
        }
        return list;
    }

    public async Task DeleteScheduleAsync(Guid scheduleId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = transaction;
            itemCmd.CommandText = "DELETE FROM PlaylistItems WHERE ScheduleId = @sid";
            itemCmd.Parameters.AddWithValue("@sid", scheduleId.ToString());
            await itemCmd.ExecuteNonQueryAsync();

            using var schedCmd = connection.CreateCommand();
            schedCmd.Transaction = transaction;
            schedCmd.CommandText = "DELETE FROM Schedules WHERE Id = @id";
            schedCmd.Parameters.AddWithValue("@id", scheduleId.ToString());
            await schedCmd.ExecuteNonQueryAsync();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<Media>> GetAllMediaAsync()
    {
        var list = new List<Media>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Media ORDER BY CreatedAt DESC";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Media
            {
                Id = Guid.Parse(reader.GetString(0)),
                Path = reader.GetString(1),
                FileName = reader.GetString(2),
                Duration = TimeSpan.FromTicks(reader.GetInt64(3)),
                Width = reader.GetInt32(4),
                Height = reader.GetInt32(5),
                FpsNum = reader.GetInt32(6),
                FpsDen = reader.GetInt32(7),
                Category = reader.GetString(8),
                CreatedAt = DateTime.Parse(reader.GetString(9))
            });
        }
        return list;
    }
}
