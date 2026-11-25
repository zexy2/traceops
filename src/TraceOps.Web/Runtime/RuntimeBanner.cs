using Microsoft.AspNetCore.Http;

namespace TraceOps.Web.Runtime;

public sealed class RuntimeBanner
{
    public const string LegacyText =
        "Synthetic demo — volatile in-memory storage. Restarting loses changes. SQL Server and SOAP are not connected.";

    public const string SoapEnabledText =
        "Synthetic demo — volatile in-memory storage. Restarting loses changes. SOAP 1.2 enabled; SQL Server is not connected.";

    private const string SqlServerValidatedText = "SQL Server startup verified. SOAP is disabled.";

    private const string SqlServerSoapEnabledText =
        "Synthetic SQL Server lab — persistent owned test data. SOAP 1.2 enabled. No production affiliation.";

    private const string SqlServerStartupStatusText = "SQL Server startup verified.";

    public RuntimeBanner(bool soapEnabled)
        : this(soapEnabled, sqlServerValidated: false)
    {
    }

    internal RuntimeBanner(bool soapEnabled, bool sqlServerValidated)
    {
        SoapEnabled = soapEnabled;
        Text = sqlServerValidated
            ? (soapEnabled ? SqlServerSoapEnabledText : SqlServerValidatedText)
            : (soapEnabled ? SoapEnabledText : LegacyText);
        StartupStatusText = sqlServerValidated && soapEnabled
            ? SqlServerStartupStatusText
            : null;
    }

    public bool SoapEnabled { get; }

    public string Text { get; }

    public string? StartupStatusText { get; }

    public static RuntimeBanner For(HttpContext context)
    {
        var services = context.RequestServices;
        return services?.GetService<RuntimeBanner>() ?? new RuntimeBanner(false);
    }
}
