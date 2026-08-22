using SqlDataMover.Core;

namespace SqlDataMover.App.Models;

/// <summary>
/// Сохранённая строка подключения. В выпадающем списке показывается краткое имя
/// «сервер / БД», в текстовой части редактируемого ComboBox — полная строка.
/// </summary>
public sealed record ConnectionHistoryEntry(string ConnectionString)
{
    public string DisplayName => ConnectionStringFormatter.FormatDisplay(ConnectionString);

    public override string ToString() => ConnectionString;
}
