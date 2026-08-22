using SqlDataMover.Core.Providers;

namespace SqlDataMover.Core.Abstractions;

/// <summary>Описание поддерживаемой СУБД для UI.</summary>
public sealed record ProviderDescriptor(string Key, string DisplayName);

/// <summary>
/// Фабрика адаптеров СУБД. Чтобы добавить новую СУБД, достаточно реализовать <see cref="IDbProvider"/>
/// и зарегистрировать его здесь.
/// </summary>
public static class DbProviderFactory
{
    public static IReadOnlyList<ProviderDescriptor> SupportedProviders { get; } =
    [
        new("mssql", "MS SQL Server")
    ];

    public static IDbProvider Create(string key, string connectionString) => key switch
    {
        "mssql" => new SqlServerProvider(connectionString),
        _ => throw new NotSupportedException($"Неизвестный тип СУБД: {key}")
    };
}
