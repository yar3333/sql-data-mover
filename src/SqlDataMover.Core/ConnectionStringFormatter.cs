namespace SqlDataMover.Core;

/// <summary>
/// Формирует краткое отображаемое имя строки подключения вида «сервер / БД»
/// (для списка истории в UI). Ключи понимаются как в MS SQL Server.
/// </summary>
public static class ConnectionStringFormatter
{
    private static readonly string[] ServerKeys =
        ["Server", "Data Source", "Addr", "Address", "Network Address", "Server Name"];

    private static readonly string[] DatabaseKeys = ["Database", "Initial Catalog"];

    /// <summary>
    /// Возвращает «сервер / БД» из строки подключения; если ключи не распознаны —
    /// исходную строку без изменений.
    /// </summary>
    public static string FormatDisplay(string connectionString)
    {
        var pairs = Parse(connectionString);
        var server = FirstNonEmpty(pairs, ServerKeys);
        var database = FirstNonEmpty(pairs, DatabaseKeys);

        if (string.IsNullOrEmpty(server) && string.IsNullOrEmpty(database))
            return connectionString;
        if (string.IsNullOrEmpty(database))
            return server!;
        if (string.IsNullOrEmpty(server))
            return database;
        return $"{server} / {database}";
    }

    private static Dictionary<string, string> Parse(string connectionString)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPart in connectionString.Split(';'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim().Trim('\'', '"');
            pairs[key] = value;
        }
        return pairs;
    }

    private static string? FirstNonEmpty(Dictionary<string, string> pairs, string[] keys) =>
        keys.Select(k => pairs.TryGetValue(k, out var v) ? v : null)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
