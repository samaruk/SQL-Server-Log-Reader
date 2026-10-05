using Microsoft.Data.SqlClient;

namespace SqlLogReader.Server;

/// <summary>How to connect to the SQL Server instance.</summary>
public sealed class ConnectionSettings
{
    /// <summary>Full connection string; when set, the other properties are ignored (except <see cref="Database"/>).</summary>
    public string? ConnectionString { get; set; }

    public string Server { get; set; } = "localhost";
    public string? Database { get; set; }

    /// <summary>Use Windows authentication when true, SQL Server authentication otherwise.</summary>
    public bool IntegratedSecurity { get; set; } = true;

    public string? UserName { get; set; }
    public string? Password { get; set; }

    /// <summary>Accept the server certificate without validation (typical for local / self-signed setups).</summary>
    public bool TrustServerCertificate { get; set; } = true;

    public bool Encrypt { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public string BuildConnectionString(string? database = null)
    {
        var builder = string.IsNullOrWhiteSpace(ConnectionString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = Server,
                IntegratedSecurity = IntegratedSecurity,
                TrustServerCertificate = TrustServerCertificate,
                Encrypt = Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                ConnectTimeout = ConnectTimeoutSeconds,
                ApplicationName = "SQL Server Log Reader",
            }
            : new SqlConnectionStringBuilder(ConnectionString);

        if (string.IsNullOrWhiteSpace(ConnectionString) && !IntegratedSecurity)
        {
            builder.UserID = UserName ?? "";
            builder.Password = Password ?? "";
        }

        var db = database ?? Database;
        if (!string.IsNullOrWhiteSpace(db))
            builder.InitialCatalog = db;
        // The reader runs one command at a time; pooling keeps reconnects cheap.
        builder.MultipleActiveResultSets = false;
        return builder.ConnectionString;
    }

    public async Task<SqlConnection> OpenAsync(string? database = null, CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(BuildConnectionString(database));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
