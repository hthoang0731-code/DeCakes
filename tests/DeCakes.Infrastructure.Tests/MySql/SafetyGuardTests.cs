namespace DeCakes.Infrastructure.Tests.MySql;

/// <summary>Chốt bảo vệ chạy không cần MySQL: cấu hình nguy hiểm bị từ chối, script schema được rà trước khi nạp.</summary>
public class SafetyGuardTests
{
    [Theory]
    [InlineData("Server=x.aivencloud.com;Port=10408;User Id=root;Password=p")]
    [InlineData("Server=10.0.0.5;User Id=root;Password=p")]
    [InlineData("Server=db.example.com;User Id=root;Password=p")]
    [InlineData("Server=127.0.0.1,db.example.com;User Id=root;Password=p")]
    [InlineData("Server=127.0.0.1;User Id=avnadmin;Password=p")]
    [InlineData("Server=127.0.0.1;User Id=decakes_app;Password=p")]
    [InlineData("Server=127.0.0.1;User Id=root;Password=p;Database=decakes_db")]
    [InlineData("Server=localhost;User Id=root;Password=p;Database=decakes_test_x")]
    public void Unsafe_admin_connection_strings_are_rejected(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() => MySqlTestSettings.ValidateAdminConnectionString(connectionString));
    }

    [Theory]
    [InlineData("Server=127.0.0.1;Port=3307;User Id=root;Password=p")]
    [InlineData("Server=localhost;User Id=root;Password=p")]
    [InlineData("Server=::1;User Id=root;Password=p")]
    public void Loopback_admin_connection_strings_are_accepted(string connectionString)
    {
        MySqlTestSettings.ValidateAdminConnectionString(connectionString);
    }

    [Theory]
    [InlineData("decakes_test_0123abcd", true)]
    [InlineData("decakes_db", false)]
    [InlineData("decakes_test_x`; DROP DATABASE decakes_db", false)]
    public void Only_generated_test_database_names_are_accepted(string name, bool expected)
    {
        Assert.Equal(expected, MySqlTestSettings.IsTestDatabaseName(name));
    }

    [Fact]
    public void Real_schema_script_loads_with_use_statement_removed()
    {
        var sanitized = SchemaScript.LoadSanitizedSchema();

        Assert.DoesNotContain("USE decakes_db", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE users", sanitized);
        Assert.Contains("CREATE TABLE schema_migrations", sanitized);
    }

    [Theory]
    [InlineData("CREATE DATABASE decakes_db;")]
    [InlineData("DROP DATABASE decakes_db;")]
    [InlineData("USE mysql;")]
    [InlineData("GRANT ALL ON *.* TO 'x'@'%';")]
    [InlineData("CREATE USER 'x'@'%';")]
    public void Schema_with_extra_database_or_account_statements_is_rejected(string extra)
    {
        var script = "SET NAMES utf8mb4;\nUSE decakes_db;\n" + extra + "\nCREATE TABLE t (id INT PRIMARY KEY);";

        Assert.Throws<InvalidOperationException>(() => SchemaScript.Sanitize(script));
    }

    [Fact]
    public void Schema_without_exactly_one_use_decakes_db_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaScript.Sanitize("CREATE TABLE t (id INT PRIMARY KEY);"));
        Assert.Throws<InvalidOperationException>(() => SchemaScript.Sanitize("USE decakes_db;\nUSE decakes_db;\n"));
    }

    [Fact]
    public void Comments_mentioning_statements_are_ignored()
    {
        var sanitized = SchemaScript.Sanitize("-- chạy USE decakes_db và GRANT ở file khác\nUSE decakes_db;\nCREATE TABLE t (id INT PRIMARY KEY);");

        Assert.Contains("CREATE TABLE t", sanitized);
    }
}
