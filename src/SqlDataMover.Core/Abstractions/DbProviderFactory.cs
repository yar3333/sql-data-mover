using SqlDataMover.Core.Providers;

namespace SqlDataMover.Core.Abstractions;

/// <summary>Описание поддерживаемой СУБД для UI.</summary>
public sealed record ProviderDescriptor(string Key, string DisplayName);

/// <summary>
/// Фабрика адаптеров СУБД. Чтобы добавить новую СУБД, достаточно реализовать <see cref="IDbProvider"/>
/// и зарегистрировать его здесь (или через <see cref="Register"/> из тестов/плагинов).
/// </summary>
public static class DbProviderFactory
{
    private static readonly List<ProviderDescriptor> Providers = [new("mssql", "MS SQL Server")];
    private static readonly Dictionary<string, Func<string, IDbProvider>> Factories = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["mssql"] = cs => new SqlServerProvider(cs),
    };
    private static readonly object Sync = new();

    public static IReadOnlyList<ProviderDescriptor> SupportedProviders
    {
        get
        {
            lock (Sync)
                return Providers.ToList();
        }
    }

    public static IDbProvider Create(string key, string connectionString)
    {
        lock (Sync)
        {
            if (!Factories.TryGetValue(key, out var factory))
                throw new NotSupportedException($"Неизвестный тип СУБД: {key}");
            return factory(connectionString);
        }
    }

    /// <summary>Регистрирует СУБД. Используется тестами (in-memory провайдер) и будущими плагинами.</summary>
    public static void Register(string key, string displayName, Func<string, IDbProvider> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        lock (Sync)
        {
            Factories[key] = factory;
            if (Providers.All(p => !string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)))
                Providers.Add(new ProviderDescriptor(key, displayName));
        }
    }
}
