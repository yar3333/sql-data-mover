using Microsoft.Data.SqlClient;
using SqlDataMover.Core.Providers;

namespace SqlDataMover.Core.Tests;

/// <summary>
/// Нормализация строки подключения SqlServerProvider: дефолты Encrypt/TrustServerCertificate
/// и Trusted_Connection (Windows-аутентификация), если аутентификация не задана явно.
/// </summary>
public class SqlServerProviderTests
{
    private static SqlConnectionStringBuilder Normalize(string connectionString) =>
        new(SqlServerProvider.NormalizeConnectionString(connectionString));

    [Fact]
    public void Defaults_to_windows_authentication_when_not_specified()
    {
        var builder = Normalize("Server=localhost;Database=Db");

        Assert.True(builder.IntegratedSecurity);
    }

    [Fact]
    public void Keeps_explicit_trusted_connection_false()
    {
        var builder = Normalize("Server=localhost;Database=Db;Trusted_Connection=False");

        Assert.False(builder.IntegratedSecurity);
    }

    [Fact]
    public void Keeps_explicit_trusted_connection_true()
    {
        var builder = Normalize("Server=localhost;Database=Db;Trusted_Connection=True");

        Assert.True(builder.IntegratedSecurity);
    }

    [Fact]
    public void Keeps_explicit_integrated_security_sspi()
    {
        var builder = Normalize("Server=localhost;Database=Db;Integrated Security=SSPI");

        Assert.True(builder.IntegratedSecurity);
    }

    [Theory]
    [InlineData("User ID=sa;Password=secret")]
    [InlineData("UID=sa;PWD=secret")]
    public void Does_not_override_sql_login(string credentials)
    {
        var builder = Normalize($"Server=localhost;Database=Db;{credentials}");

        Assert.False(builder.IntegratedSecurity);
    }

    [Fact]
    public void Adds_encrypt_and_trust_server_certificate_defaults()
    {
        var builder = Normalize("Server=localhost;Database=Db");

        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void Keeps_explicit_encrypt_and_certificate_settings()
    {
        var builder = Normalize(
            "Server=localhost;Database=Db;Encrypt=True;TrustServerCertificate=False"
        );

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
    }
}
