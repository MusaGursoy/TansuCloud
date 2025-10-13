// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using Microsoft.Data.Sqlite;

var databasePath = args.Length > 0
	? args[0]
	: Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TansuCloud.Telemetry", "App_Data", "telemetry", "telemetry.dev.db");

databasePath = Path.GetFullPath(databasePath);

if (!File.Exists(databasePath))
{
	Console.Error.WriteLine($"Database file not found: {databasePath}");
	return 1;
}

using var connection = new SqliteConnection($"Data Source={databasePath}");
await connection.OpenAsync();

using var command = connection.CreateCommand();
command.CommandText = @"SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";

using var reader = await command.ExecuteReaderAsync();

var tableNames = new List<string>();
while (await reader.ReadAsync())
{
	var name = reader.GetString(0);
	tableNames.Add(name);
}

Console.WriteLine($"Database: {databasePath}");
Console.WriteLine($"Total tables: {tableNames.Count}");

foreach (var name in tableNames)
{
	Console.WriteLine($" - {name}");
}

return 0;
