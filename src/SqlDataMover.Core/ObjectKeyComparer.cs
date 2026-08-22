namespace SqlDataMover.Core;

/// <summary>
/// Сравнение значений ключей: строки — без учёта регистра (как стандартная коллация SQL Server),
/// остальные типы — через <see cref="EqualityComparer{T}"/>.
/// </summary>
public sealed class ObjectKeyComparer : IEqualityComparer<object>
{
    public static ObjectKeyComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y) =>
        x is string sx && y is string sy
            ? string.Equals(sx, sy, StringComparison.OrdinalIgnoreCase)
            : EqualityComparer<object>.Default.Equals(x, y);

    public int GetHashCode(object obj) =>
        obj is string s
            ? StringComparer.OrdinalIgnoreCase.GetHashCode(s)
            : EqualityComparer<object>.Default.GetHashCode(obj);
}
