using Microsoft.Data.SqlClient;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Best-effort discovery of the SQL Server-authenticated login names on a
/// server, to populate Panel 1's "SQL username" field as a dropdown once a
/// server has been picked, instead of a blank box the user has to already
/// know the answer for.
/// </summary>
/// <remarks>
/// Deployment itself always authenticates purely with the SQL login typed
/// into Panel 1 -- see the "never falls back to Windows auth" guarantee on
/// <see cref="SqlConnectionFactory.BuildConnectionString"/>. This discovery
/// step is a separate, read-only, throwaway connection using the *current
/// Windows identity* (via <see cref="SqlConnectionFactory.BuildIntegratedConnectionString"/>),
/// exactly like Panel 1's own "I don't have a login with database-creation
/// rights" bootstrap flow already does (<see cref="BootstrapLoginService"/>)
/// -- it exists purely to list names for convenience and never touches
/// anything the deployment connection itself uses.
/// <para/>
/// If the current Windows account can't connect to the picked server at
/// all, or can connect but isn't privileged enough to see other logins'
/// names (<c>sys.server_principals</c> only exposes principals the caller
/// has visibility of -- a non-admin login typically only sees its own row),
/// this comes back with an empty list rather than an error. Panel 1's
/// username field is built to stay a normal, freely-editable box either
/// way -- this only ever adds suggestions on top of that, never requires
/// them.
/// </remarks>
public static class SqlLoginDiscovery
{
    /// <summary>
    /// Lists SQL Server-authenticated login names (never Windows logins or
    /// groups -- Panel 1's username field only ever feeds a SQL-authenticated
    /// connection, per <see cref="SqlConnectionFactory.BuildConnectionString"/>,
    /// so a Windows account name wouldn't work there anyway). Disabled
    /// logins and the fixed system logins (<c>##...##</c>, used internally
    /// by certificate/asymmetric-key mappings) are excluded. Returns an
    /// empty list on any failure -- see the class remarks.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetSqlLoginNamesAsync(string server, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(server))
            return Array.Empty<string>();

        string connectionString = SqlConnectionFactory.BuildIntegratedConnectionString(server, "master");

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(token);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText =
                "SELECT name FROM sys.sql_logins WHERE is_disabled = 0 AND name NOT LIKE '##%' ORDER BY name;";

            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                names.Add(reader.GetString(0));

            return names;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort only -- see class remarks. Any failure (can't
            // connect as the current Windows identity, not privileged
            // enough to see other logins, server unreachable, instance
            // name not resolvable yet while the user is still typing it)
            // just means no suggestions, never an error shown to the user.
            return Array.Empty<string>();
        }
    }
}
