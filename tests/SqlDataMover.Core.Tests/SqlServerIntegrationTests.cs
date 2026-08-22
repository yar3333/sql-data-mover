using Microsoft.Data.SqlClient;
using SqlDataMover.Core.Copy;
using SqlDataMover.Core.Models;
using SqlDataMover.Core.Providers;

namespace SqlDataMover.Core.Tests;

/// <summary>
/// Интеграционные тесты против локального SQL Server (экземпляр по умолчанию, trusted connection).
/// БД sql-data-mover-src / sql-data-mover-dest со схемой tests создаются скриптом
/// tests/sql/setup-test-databases.sql. Тест сам очищает и наполняет данные, поэтому повторяем.
/// </summary>
public class SqlServerIntegrationTests
{
    private const string SourceConnectionString =
        "Server=localhost;Database=sql-data-mover-src;Trusted_Connection=True;Encrypt=Optional;TrustServerCertificate=True";

    private const string DestConnectionString =
        "Server=localhost;Database=sql-data-mover-dest;Trusted_Connection=True;Encrypt=Optional;TrustServerCertificate=True";

    private static readonly DbObjectName Customers = new("tests", "Customers");
    private static readonly DbObjectName Orders = new("tests", "Orders");
    private static readonly DbObjectName OrderItems = new("tests", "OrderItems");
    private static readonly DbObjectName Employees = new("tests", "Employees");

    [Fact]
    public async Task Copies_dependent_tables_with_composite_match_and_identity_remap()
    {
        await ResetAndSeedAsync();

        await using var source = new SqlServerProvider(SourceConnectionString);
        await using var target = new SqlServerProvider(DestConnectionString);
        await source.ConnectAsync();
        await target.ConnectAsync();

        var engine = new DataCopyEngine(source, target);
        var result = await engine.CopyAsync(
            [
                new TableCopyConfig { Table = Customers, MatchColumns = ["Region", "Code"] },
                new TableCopyConfig { Table = Orders, MatchColumns = ["Id"] },
                new TableCopyConfig { Table = OrderItems, MatchColumns = ["Id"] },
                new TableCopyConfig { Table = Employees, MatchColumns = ["Id"] },
            ],
            CancellationToken.None
        );

        Assert.True(
            result.Success,
            string.Join("; ", result.Tables.Where(t => !t.Success).Select(t => t.Error))
        );

        // Клиент (EU, X1) совпал с существующим в приёмнике → обновлён; (US, X2) вставлен.
        var customers = result.Tables.Single(t => t.Table == Customers);
        Assert.Equal(1, customers.Updated);
        Assert.Equal(1, customers.Inserted);

        // Остальные таблицы копируются целиком.
        Assert.Equal(2, result.Tables.Single(t => t.Table == Orders).Inserted);
        Assert.Equal(2, result.Tables.Single(t => t.Table == OrderItems).Inserted);
        Assert.Equal(3, result.Tables.Single(t => t.Table == Employees).Inserted);

        await using var verify = new SqlConnection(DestConnectionString);
        await verify.OpenAsync();
        await VerifyCustomersAsync(verify);
        await VerifyOrdersAsync(verify);
        await VerifyOrderItemsAsync(verify);
        await VerifyEmployeesAsync(verify);
    }

    [Fact]
    public async Task GetTables_lists_tables_without_syntax_errors()
    {
        // Регрессия: алиас RowCount в запросе GetTablesAsync — зарезервированное слово
        // T-SQL, без скобок запрос падает с "Incorrect syntax near keyword 'RowCount'".
        await using var source = new SqlServerProvider(SourceConnectionString);
        await source.ConnectAsync();

        var tables = await source.GetTablesAsync();

        var names = tables.Select(t => t.Name.ToString()).ToHashSet();
        Assert.Contains("tests.Customers", names);
        Assert.Contains("tests.Orders", names);
        Assert.Contains("tests.OrderItems", names);
        Assert.Contains("tests.Employees", names);
        Assert.All(tables, t => Assert.True(t.RowCount >= 0));
    }

    private static async Task ResetAndSeedAsync()
    {
        await using var source = new SqlConnection(SourceConnectionString);
        await using var dest = new SqlConnection(DestConnectionString);
        await source.OpenAsync();
        await dest.OpenAsync();

        await EnsureTablesExistAsync(source);
        await EnsureTablesExistAsync(dest);

        // Приёмник: очистка (дети раньше родителей) и сброс identity для детерминированных ID.
        await ExecAsync(
            dest,
            """
            DELETE FROM tests.OrderItems;
            DELETE FROM tests.Orders;
            DELETE FROM tests.Employees;
            DELETE FROM tests.Customers;
            DBCC CHECKIDENT ('tests.Customers', RESEED, 4);
            DBCC CHECKIDENT ('tests.Orders', RESEED, 0);
            DBCC CHECKIDENT ('tests.OrderItems', RESEED, 0);
            DBCC CHECKIDENT ('tests.Employees', RESEED, 0);
            """
        );

        // В приёмнике уже есть клиент с составным ключом (EU, X1) — получит Id = 5.
        await ExecAsync(
            dest,
            "INSERT INTO tests.Customers (Region, Code, Name) VALUES ('EU', 'X1', 'Old');"
        );

        // Источник: очистка и наполнение с фиксированными ID (identity-колонки заполняются вручную).
        await ExecAsync(
            source,
            """
            DELETE FROM tests.OrderItems;
            DELETE FROM tests.Orders;
            DELETE FROM tests.Employees;
            DELETE FROM tests.Customers;

            SET IDENTITY_INSERT tests.Customers ON;
            INSERT INTO tests.Customers (Id, Region, Code, Name) VALUES
                (100, 'EU', 'X1', 'New'),
                (101, 'US', 'X2', 'Other');
            SET IDENTITY_INSERT tests.Customers OFF;

            SET IDENTITY_INSERT tests.Orders ON;
            INSERT INTO tests.Orders (Id, CustomerId, OrderDate) VALUES
                (10, 100, '2026-01-01'),
                (11, 101, '2026-01-02');
            SET IDENTITY_INSERT tests.Orders OFF;

            SET IDENTITY_INSERT tests.OrderItems ON;
            INSERT INTO tests.OrderItems (Id, OrderId, Product, Qty) VALUES
                (1, 10, 'Widget', 2),
                (2, 11, 'Gadget', 1);
            SET IDENTITY_INSERT tests.OrderItems OFF;

            SET IDENTITY_INSERT tests.Employees ON;
            INSERT INTO tests.Employees (Id, Name, ManagerId) VALUES
                (100, 'CEO', NULL),
                (101, 'Dev1', 100),
                (102, 'Dev2', 100);
            SET IDENTITY_INSERT tests.Employees OFF;
            """
        );
    }

    private static async Task EnsureTablesExistAsync(SqlConnection connection)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'tests' AND t.name = 'Customers'
            """,
            connection
        );
        var count = (int)(await cmd.ExecuteScalarAsync())!;
        if (count == 0)
            throw new InvalidOperationException(
                "Тестовые БД не подготовлены: выполните sqlcmd -S localhost -E -i tests\\sql\\setup-test-databases.sql"
            );
    }

    private static async Task VerifyCustomersAsync(SqlConnection connection)
    {
        Assert.Equal(2, await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM tests.Customers"));

        // Совпавший по (Region, Code) клиент обновлён: Id 5 сохранён, Name = 'New'.
        Assert.Equal(
            "New",
            await ScalarAsync<string>(connection, "SELECT Name FROM tests.Customers WHERE Id = 5")
        );

        // Второй клиент вставлен с новым identity.
        var usId = await ScalarAsync<int>(
            connection,
            "SELECT Id FROM tests.Customers WHERE Region = 'US' AND Code = 'X2'"
        );
        Assert.NotEqual(5, usId);
    }

    private static async Task VerifyOrdersAsync(SqlConnection connection)
    {
        Assert.Equal(2, await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM tests.Orders"));

        // Оба заказа ссылаются на существующих клиентов: по одному на каждого.
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                connection,
                """
                SELECT COUNT(*) FROM tests.Orders o
                JOIN tests.Customers c ON c.Id = o.CustomerId
                WHERE c.Region = 'EU' AND c.Code = 'X1'
                """
            )
        );
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                connection,
                """
                SELECT COUNT(*) FROM tests.Orders o
                JOIN tests.Customers c ON c.Id = o.CustomerId
                WHERE c.Region = 'US' AND c.Code = 'X2'
                """
            )
        );
    }

    private static async Task VerifyOrderItemsAsync(SqlConnection connection)
    {
        Assert.Equal(
            2,
            await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM tests.OrderItems")
        );

        // Позиция 'Widget' (заказ источника 10 → клиент 100) попала к клиенту (EU, X1).
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                connection,
                """
                SELECT COUNT(*) FROM tests.OrderItems i
                JOIN tests.Orders o ON o.Id = i.OrderId
                JOIN tests.Customers c ON c.Id = o.CustomerId
                WHERE i.Product = 'Widget' AND c.Region = 'EU' AND c.Code = 'X1'
                """
            )
        );
    }

    private static async Task VerifyEmployeesAsync(SqlConnection connection)
    {
        Assert.Equal(3, await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM tests.Employees"));

        var ceoId = await ScalarAsync<int>(
            connection,
            "SELECT Id FROM tests.Employees WHERE Name = 'CEO'"
        );
        Assert.Equal(
            1,
            await ScalarAsync<int>(
                connection,
                "SELECT COUNT(*) FROM tests.Employees WHERE Name = 'CEO' AND ManagerId IS NULL"
            )
        );
        // Подчинённые (самоссылающийся FK) ссылаются на новый ID руководителя.
        Assert.Equal(
            2,
            await ScalarAsync<int>(
                connection,
                $"SELECT COUNT(*) FROM tests.Employees WHERE ManagerId = {ceoId}"
            )
        );
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        var value = await cmd.ExecuteScalarAsync();
        return (T)value!;
    }
}
