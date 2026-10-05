namespace GraniteNiFiDeploy.Core;

/// <summary>One parameter value to write into NiFi's parameter context.</summary>
public sealed record NiFiParameter(string Name, string? Value, bool Sensitive);

/// <summary>
/// The Granite CSV Import flow's parameter context: the names the flow
/// (Resources/flow/GraniteCsvImport.json) expects and the values for a
/// deployment. The harness checks these names against the embedded flow.
/// </summary>
public static class NiFiFlowParameters
{
    public const string FlowGroupName = "Granite CSV Import";
    public const string ContextName = "Granite CSV Import";

    public const string DbUrl = "granite.db.url";
    public const string DbUser = "granite.db.user";
    public const string DbPassword = "granite.db.password";
    public const string DriverDir = "granite.jdbc.driver.dir";
    public const string Inbound = "import.inbound.dir";
    public const string Archive = "import.archive.dir";
    public const string Error = "import.error.dir";
    public const string MinFileAge = "import.min.file.age";

    public static readonly IReadOnlyList<string> AllNames = new[] { DbUrl, DbUser, DbPassword, DriverDir, Inbound, Archive, Error, MinFileAge };

    public static IReadOnlyList<NiFiParameter> Build(
        string jdbcUrl, string sqlLogin, string sqlPassword, string driverFolder,
        string inbound, string archive, string error, string minFileAge = "10 sec") => new[]
    {
        new NiFiParameter(DbUrl, jdbcUrl, false),
        new NiFiParameter(DbUser, sqlLogin, false),
        new NiFiParameter(DbPassword, sqlPassword, true),
        new NiFiParameter(DriverDir, driverFolder, false),
        new NiFiParameter(Inbound, inbound, false),
        new NiFiParameter(Archive, archive, false),
        new NiFiParameter(Error, error, false),
        new NiFiParameter(MinFileAge, minFileAge, false)
    };
}
