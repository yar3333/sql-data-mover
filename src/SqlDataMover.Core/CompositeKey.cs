namespace SqlDataMover.Core;

/// <summary>
/// Составной ключ сопоставления: комбинация значений нескольких колонок.
/// Компоненты-строки сравниваются без учёта регистра (как стандартная коллация SQL Server),
/// остальные типы — через <see cref="EqualityComparer{T}"/>.
/// </summary>
public sealed class CompositeKey : IEquatable<CompositeKey>
{
    private readonly object?[] _values;

    public CompositeKey(IEnumerable<object?> values) => _values = values.ToArray();

    /// <summary>Значения компонентов ключа в порядке полей сопоставления.</summary>
    public IReadOnlyList<object?> Values => _values;

    public bool Equals(CompositeKey? other)
    {
        if (other is null || other._values.Length != _values.Length)
            return false;

        for (var i = 0; i < _values.Length; i++)
            if (!ObjectKeyComparer.Instance.Equals(_values[i], other._values[i]))
                return false;

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as CompositeKey);

    public override int GetHashCode()
    {
        var comparer = ObjectKeyComparer.Instance;
        var hash = new HashCode();
        foreach (var value in _values)
            hash.Add(value is null ? 0 : comparer.GetHashCode(value));
        return hash.ToHashCode();
    }

    public override string ToString() =>
        string.Join(", ", _values.Select(v => v?.ToString() ?? "NULL"));
}
