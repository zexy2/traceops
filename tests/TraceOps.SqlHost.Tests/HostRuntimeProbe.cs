using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.SqlClient;
using TraceOps.SqlServer.Tests;

namespace TraceOps.SqlHost.Tests;

internal sealed class HostRuntimeProbe : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly string[] _privateValues;
    internal HttpClient Client { get; }
    internal bool Started { get; private set; }
    internal bool Exited => _process.HasExited;
    internal int Port { get; }
    private HostRuntimeProbe(Dictionary<string,string?> settings)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var executable=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if(string.IsNullOrWhiteSpace(executable))executable=Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")??throw new SqlFixturePrerequisiteException("DOTNET_ROOT prerequisite missing."),"dotnet");
        var output=Path.GetFullPath(SqlHostFixture.Metadata("HostWebOutput"));
        var dll=Path.Combine(output,"TraceOps.Web.dll");
        if(!File.Exists(dll)||!File.Exists(Path.ChangeExtension(dll,"runtimeconfig.json")))throw new SqlFixturePrerequisiteException("Built Web runtime artifacts absent; build host test project with external artifacts path first.");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetFullPath(Path.Combine(SqlHostFixture.Metadata("HostRepository"),"src/TraceOps.Web"))};
        start.ArgumentList.Add(dll);
        foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("TraceOps",StringComparison.OrdinalIgnoreCase)||k.StartsWith("ConnectionStrings",StringComparison.OrdinalIgnoreCase)||k.StartsWith("M4_SQLSERVER",StringComparison.OrdinalIgnoreCase)||k=="MSSQL_SA_PASSWORD").ToArray())start.Environment.Remove(key);
        start.Environment["ASPNETCORE_ENVIRONMENT"]="Production";
        start.Environment.Remove("ASPNETCORE_URLS");start.Environment.Remove("ASPNETCORE_HTTP_PORTS");start.Environment.Remove("ASPNETCORE_HTTPS_PORTS");
        foreach(var pair in settings)if(pair.Value is not null)start.Environment[pair.Key]=pair.Value;
        start.Environment["TraceOps__Http__Port"]=Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _privateValues=settings.Where(p=>p.Key.StartsWith("ConnectionStrings",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(p.Value)).Select(p=>p.Value!).ToArray();
        var passwords=new List<string>();foreach(var value in _privateValues){try{var b=new SqlConnectionStringBuilder(value);if(!string.IsNullOrEmpty(b.Password))passwords.Add(b.Password);}catch(ArgumentException){}}
        _privateValues=_privateValues.Concat(passwords).ToArray();
        _process=new Process{StartInfo=start};
        _process.OutputDataReceived+=Capture;_process.ErrorDataReceived+=Capture;
        if(!_process.Start())throw new SqlFixturePrerequisiteException("Owned host child launch failed.");
        _process.BeginOutputReadLine();_process.BeginErrorReadLine();
        Client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{Port}"),Timeout=TimeSpan.FromSeconds(2)};
    }
    private void Capture(object sender,DataReceivedEventArgs args){if(args.Data is not null)lock(_output)_output.AppendLine(args.Data);}
    internal bool LogsSafe { get { lock(_output)return !_privateValues.Any(v=>_output.ToString().Contains(v,StringComparison.Ordinal)); } }
    internal bool FalseConnectedClaim { get { lock(_output)return _output.ToString().Contains("SQL Server connected",StringComparison.OrdinalIgnoreCase); } }
    internal static async Task<HostRuntimeProbe> Start(Dictionary<string,string?> settings)
    {
        var probe=new HostRuntimeProbe(settings);
        try
        {
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(12)&&!probe.Exited)
            {
                try{using var response=await probe.Client.GetAsync("/shipments");probe.Started=true;break;}
                catch(HttpRequestException){}catch(TaskCanceledException){}
                await Task.Delay(100);
            }
            return probe;
        }
        catch{await probe.DisposeAsync();throw;}
    }
    internal static void AssertVerifiedSqlBanner(string html)
    {
        var match=System.Text.RegularExpressions.Regex.Match(html,@"<p\b[^>]*class=[""']demo-banner[""'][^>]*>(.*?)</p>",System.Text.RegularExpressions.RegexOptions.Singleline|System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Xunit.Assert.True(match.Success,"Successful SQL page must have its semantic runtime banner.");
        var text=WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value,"<[^>]*>"," "));
        Xunit.Assert.Contains("SQL",text,StringComparison.OrdinalIgnoreCase);
        // Independent semantic tokens, not an invented exact English sentence.
        Xunit.Assert.Matches(@"(?i)\b(verified|validated|connected|doğrulan(?:dı|mış)|bağlı)\b",text);
        Xunit.Assert.DoesNotMatch(@"(?i)\b(not\s+(?:yet\s+)?(?:connected|verified|validated)|unverified|disconnected)\b",text);
        Xunit.Assert.DoesNotMatch(@"(?i)(in[ -]?memory|volatile|restart(?:ing)?.*los(?:e|es|ing)|uçucu)",text);
    }
    internal static string ChangeConnection(string value,Action<SqlConnectionStringBuilder> mutate)
    {
        var builder=new SqlConnectionStringBuilder(value);mutate(builder);return builder.ConnectionString;
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if(!_process.HasExited){_process.Kill(entireProcessTree:true);await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
        _process.WaitForExit(); // drain both redirected streams; raw output stays only in memory.
        _process.Dispose();
    }
}
