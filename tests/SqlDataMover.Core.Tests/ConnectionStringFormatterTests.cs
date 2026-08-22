using SqlDataMover.Core;

namespace SqlDataMover.Core.Tests;

public class ConnectionStringFormatterTests
{
    [Fact]
    public void Formats_server_and_database()
    {
        Assert.Equal("localhost / SourceDb", ConnectionStringFormatter.FormatDisplay(
            "Server=localhost;Database=SourceDb;Trusted_Connection=True;Encrypt=False"));
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        Assert.Equal("localhost / SourceDb", ConnectionStringFormatter.FormatDisplay(
            "SERVER=localhost;DATABASE=SourceDb"));
    }

    [Fact]
    public void Supports_data_source_and_initial_catalog_aliases()
    {
        Assert.Equal("(localdb)\\MSSQLLocalDB / TestDb", ConnectionStringFormatter.FormatDisplay(
            "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=TestDb"));
    }

    [Fact]
    public void Quoted_values_are_trimmed()
    {
        Assert.Equal("db-host / my db", ConnectionStringFormatter.FormatDisplay(
            "Server='db-host';Database=\"my db\""));
    }

    [Fact]
    public void Value_with_equals_sign_is_parsed_correctly()
    {
        Assert.Equal("localhost / SourceDb", ConnectionStringFormatter.FormatDisplay(
            "Server=localhost;Database=SourceDb;Password=a=b=c"));
    }

    [Fact]
    public void Server_without_database_returns_server()
    {
        Assert.Equal("localhost", ConnectionStringFormatter.FormatDisplay("Server=localhost"));
    }

    [Fact]
    public void Database_without_server_returns_database()
    {
        Assert.Equal("SourceDb", ConnectionStringFormatter.FormatDisplay("Database=SourceDb"));
    }

    [Fact]
    public void Unrecognized_string_falls_back_to_original()
    {
        Assert.Equal("something else", ConnectionStringFormatter.FormatDisplay("something else"));
        Assert.Equal("", ConnectionStringFormatter.FormatDisplay(""));
    }
}
