using System;
using System.Data;
using Microsoft.Data.Sqlite;

public class DbContext : IDisposable
{
    private readonly string _connectionString;

    public DbContext(string dbFile = "trading.db")
    {
        _connectionString = $"Data Source={dbFile}";
        Initialize();
    }

    public IDbConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();

        cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Trades(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Time TEXT,
            Symbol TEXT,
            Side TEXT,
            Qty INTEGER,
            Price REAL
        );
        ";

        cmd.ExecuteNonQuery();
    }

    public void Dispose() { }
}
