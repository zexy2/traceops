namespace TraceOps.SqlServer.Tests;

internal static class SqlSecretEnvironment
{
    // Values are parsed as data, never executed or expanded by a shell.
    internal static IReadOnlyDictionary<string, string?> Read()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in new[] { SqlServerFixture.RuntimeConnectionKey, SqlServerFixture.FixtureConnectionKey })
            result[key] = Environment.GetEnvironmentVariable(key);

        var path = Environment.GetEnvironmentVariable("M4_SQLSERVER_ENV_FILE");
        if (string.IsNullOrWhiteSpace(path)) return result;
        try
        {
            var file = new FileInfo(path);
            if (!Path.IsPathFullyQualified(path) || !file.Exists || file.LinkTarget is not null
                || file.Length > 32768 || OperatingSystem.IsWindows()
                || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                throw new InvalidDataException();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var separator = line.IndexOf('=');
                if (separator < 1) throw new InvalidDataException();
                var key = line[..separator].Trim();
                if (key is not (SqlServerFixture.RuntimeConnectionKey or SqlServerFixture.FixtureConnectionKey
                    or "MSSQL_SA_PASSWORD") || !seen.Add(key)) throw new InvalidDataException();
                var value = line[(separator + 1)..].Trim();
                if (value.StartsWith('\''))
                {
                    if (value.Length < 2 || !value.EndsWith('\'') || value[1..^1].Contains('\''))
                        throw new InvalidDataException();
                    value = value[1..^1];
                }
                else if (value.Contains('\'') || value.Contains('"')) throw new InvalidDataException();
                if (key != "MSSQL_SA_PASSWORD") result[key] = value;
            }
        }
        catch (Exception)
        {
            // No inner exception: parsing and filesystem diagnostics can contain secrets or paths.
            throw new SqlFixturePrerequisiteException("Private SQL environment file rejected; require absolute regular 0600 file and known literal keys. Values redacted.");
        }
        return result;
    }
}
