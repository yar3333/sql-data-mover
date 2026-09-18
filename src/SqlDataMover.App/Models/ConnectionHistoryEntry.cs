using SqlDataMover.Core;

namespace SqlDataMover.App.Models;

/// <summary>
/// Сохранённая строка подключения. В выпадающем списке показывается краткое имя
/// «сервер / БД», в текстовой части редактируемого ComboBox — полная строка.
/// </summary>
/// <remarks>
/// Обычный класс (не record): для привязки SelectedItem важна ссылочная идентичность
/// объекта, а не сравнение по значению, — иначе ObservableProperty-сеттер заметит
/// «равное» значение и не обновит выделение при пересоздании записи истории.
/// </remarks>
public sealed class ConnectionHistoryEntry
{
    public ConnectionHistoryEntry(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public string DisplayName => ConnectionStringFormatter.FormatDisplay(ConnectionString);

    public override string ToString() => ConnectionString;
}
