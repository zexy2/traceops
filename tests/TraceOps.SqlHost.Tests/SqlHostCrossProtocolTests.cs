using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace TraceOps.SqlHost.Tests;

public sealed class SqlHostCrossProtocolTests
{
    private static readonly XNamespace Service="urn:traceops:soap:v1";
    private static readonly XNamespace Envelope="http://www.w3.org/2003/05/soap-envelope";
    private static async Task<XElement> Soap(HttpClient client,string operation,params XElement[] fields)
    {
        var body=new XDocument(new XElement(Envelope+"Envelope",new XElement(Envelope+"Body",new XElement(Service+operation,new XElement(Service+"request",fields)))));
        using var message=new HttpRequestMessage(HttpMethod.Post,"/soap/shipments");
        message.Content=new StringContent(body.ToString(SaveOptions.DisableFormatting),Encoding.UTF8);
        message.Content.Headers.ContentType=MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"urn:traceops:soap:v1/ITraceOpsSoapService/{operation}\"");
        using var response=await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var document=XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Envelope+"Envelope",document.Root!.Name);
        var responseBody=Assert.Single(document.Root.Elements(Envelope+"Body"));
        var wrapper=Assert.Single(responseBody.Elements());Assert.Equal(Service+(operation+"Response"),wrapper.Name);
        return Assert.Single(wrapper.Elements(Service+(operation+"Result")));
    }
    [Fact,Trait("Acceptance","M5-T10"),Trait("Acceptance","M4-Host-Persistence")]
    public async Task Fresh_Kestrel_shares_SQL_across_REST_SOAP_HTML_and_persists_after_process_restart()
    {
        await using var fixture=await SqlHostFixture.Create();
        var baseline=await fixture.Snapshot();
        var tracking="HOST-"+Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var eventId=Guid.NewGuid();
        string? registeredTime=null;
        SqlHostFixture.State? expectedState=null;
        WireObservation? expectedWire=null;
        await using(var host=await HostRuntimeProbe.Start(fixture.Configuration()))
        {
            Assert.True(host.Started,"Prerequisite-valid SQL host did not start; raw diagnostics withheld.");
            // Omitted demo-seed setting must not seed the owned SQL database or migrate its ledger.
            Assert.Equal(baseline,await fixture.Snapshot());
            using(var register=await host.Client.PostAsJsonAsync("/api/v1/shipments",new{trackingNumber=tracking}))
            {
                Assert.Equal(HttpStatusCode.Created,register.StatusCode);
                using var json=JsonDocument.Parse(await register.Content.ReadAsStringAsync());
                Assert.Equal(0,json.RootElement.GetProperty("version").GetInt64());
                registeredTime=json.RootElement.GetProperty("lastOccurredAt").GetString();
            }
            var read=await Soap(host.Client,"GetShipment",new XElement(Service+"trackingNumber",tracking));
            Assert.Equal("FOUND",read.Element(Service+"code")?.Value);
            Assert.Equal("0",read.Descendants(Service+"version").Single().Value);
            var eventTime=DateTimeOffset.Parse(registeredTime!,CultureInfo.InvariantCulture).AddSeconds(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",CultureInfo.InvariantCulture);
            var applied=await Soap(host.Client,"AddShipmentEvent",
                new XElement(Service+"trackingNumber",tracking),new XElement(Service+"eventId",eventId.ToString("D")),
                new XElement(Service+"status","Accepted"),new XElement(Service+"occurredAt",eventTime),new XElement(Service+"sourceSystem","host.soap"));
            Assert.Equal("APPLIED",applied.Element(Service+"code")?.Value);
            Assert.Equal("1",applied.Descendants(Service+"version").Single().Value);
            expectedState=await fixture.ReadState(tracking);
            Assert.Equal(tracking,expectedState.Parent.Tracking);
            Assert.Equal(1,expectedState.Parent.Version);
            Assert.Equal((byte)1,expectedState.Parent.Status);
            Assert.True(expectedState.Parent.RegisteredAt.EqualsExact(DateTimeOffset.Parse(registeredTime!,CultureInfo.InvariantCulture)));
            var item=Assert.Single(expectedState.Events);
            Assert.Equal(1,item.Sequence);
            Assert.Equal(eventId,item.Id);
            Assert.Equal(tracking,item.Tracking);
            Assert.Equal((byte)1,item.Status);
            Assert.Equal("host.soap",item.Source);
            Assert.True(item.OccurredAt.EqualsExact(DateTimeOffset.Parse(eventTime,CultureInfo.InvariantCulture)));
            Assert.True(expectedState.Parent.LastOccurredAt.EqualsExact(item.OccurredAt));
            expectedWire=await AssertRestAndHtml(host.Client,expectedState);
            var soapSummary=Summary(await Soap(host.Client,"GetShipment",new XElement(Service+"trackingNumber",tracking)));
            Assert.Equal(expectedWire.Summary,soapSummary);
            Assert.True(host.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
        var persisted=await fixture.Snapshot();
        await using(var restarted=await HostRuntimeProbe.Start(fixture.Configuration()))
        {
            Assert.True(restarted.Started,"SQL host restart failed; raw diagnostics withheld.");
            Assert.Equal(persisted,await fixture.Snapshot());
            var freshState=await fixture.ReadState(tracking);
            Assert.NotNull(expectedState);
            Assert.Equal(expectedState.Parent,freshState.Parent);
            Assert.Equal(expectedState.Events,freshState.Events);
            Assert.All(expectedState.Events.Zip(freshState.Events),pair=>Assert.True(pair.First.OccurredAt.EqualsExact(pair.Second.OccurredAt)));
            Assert.True(expectedState.Parent.RegisteredAt.EqualsExact(freshState.Parent.RegisteredAt));
            Assert.True(expectedState.Parent.LastOccurredAt.EqualsExact(freshState.Parent.LastOccurredAt));
            Assert.Equal(1,Assert.Single(freshState.Events).Sequence);
            var freshWire=await AssertRestAndHtml(restarted.Client,freshState);
            Assert.Equal(expectedWire,freshWire);
            var read=await Soap(restarted.Client,"GetShipment",new XElement(Service+"trackingNumber",tracking));
            Assert.Equal("FOUND",read.Element(Service+"code")?.Value);
            Assert.Equal("Accepted",read.Descendants(Service+"status").Single().Value);
            Assert.Equal("1",read.Descendants(Service+"version").Single().Value);
            Assert.Equal(expectedWire!.Summary,Summary(read));
            Assert.True(restarted.LogsSafe,"Child diagnostic privacy failure; contents withheld.");
        }
    }
    private sealed record Projection(string Tracking,string Status,long Version,string At);
    private sealed record WireObservation(Projection Summary,string OrderedHtmlHistory);
    private static string ExactTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture).ToString("O",CultureInfo.InvariantCulture);
    private static Projection Summary(XElement result)
    {
        Assert.Equal("FOUND",result.Element(Service+"code")?.Value);
        var shipment=Assert.Single(result.Elements(Service+"shipment"));
        return new(shipment.Element(Service+"trackingNumber")!.Value,shipment.Element(Service+"status")!.Value,
            long.Parse(shipment.Element(Service+"version")!.Value,CultureInfo.InvariantCulture),ExactTime(shipment.Element(Service+"lastOccurredAt")!.Value));
    }
    private static async Task<WireObservation> AssertRestAndHtml(HttpClient client,SqlHostFixture.State state)
    {
        var tracking=state.Parent.Tracking;
        var expected=new Projection(tracking,"Accepted",state.Parent.Version,state.Parent.LastOccurredAt.ToString("O",CultureInfo.InvariantCulture));
        using var response=await client.GetAsync("/api/v1/shipments/"+tracking);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var json=document.RootElement;
        var actual=new Projection(json.GetProperty("trackingNumber").GetString()!,json.GetProperty("status").GetString()!,json.GetProperty("version").GetInt64(),ExactTime(json.GetProperty("lastOccurredAt").GetString()!));
        Assert.Equal(expected,actual);
        using var html=await client.GetAsync("/shipments/"+tracking);
        Assert.Equal(HttpStatusCode.OK,html.StatusCode);
        var raw=await html.Content.ReadAsStringAsync();
        HostRuntimeProbe.AssertVerifiedSqlBanner(raw);
        var text=WebUtility.HtmlDecode(raw);
        Assert.Contains(tracking,text,StringComparison.Ordinal);
        var version=System.Text.RegularExpressions.Regex.Match(text,@"<dt>Version</dt>\s*<dd[^>]*>(\d+)</dd>");
        Assert.True(version.Success,"HTML record version must be present.");
        Assert.Equal(state.Parent.Version,long.Parse(version.Groups[1].Value,CultureInfo.InvariantCulture));
        var rows=System.Text.RegularExpressions.Regex.Matches(text,@"<li\b[^>]*class=[""']event[""'][^>]*>(.*?)</li>",System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Equal(state.Events.Length,rows.Count);
        var ordered=new List<string>();
        for(var i=0;i<rows.Count;i++)
        {
            var row=rows[i].Groups[1].Value;
            var entry=state.Events[i];
            var time=System.Text.RegularExpressions.Regex.Match(row,@"<time\b[^>]*datetime=[""']([^""']+)[""']");
            var status=System.Text.RegularExpressions.Regex.Match(row,@"<h3>([^<]+)</h3>");
            var source=System.Text.RegularExpressions.Regex.Match(row,@"<dt>Source system</dt>\s*<dd[^>]*>([^<]+)</dd>");
            var id=System.Text.RegularExpressions.Regex.Match(row,@"<dt>Event ID</dt>\s*<dd[^>]*>([^<]+)</dd>");
            Assert.True(time.Success&&status.Success&&source.Success&&id.Success,"Each ordered HTML event row must expose timestamp/status/source/ID.");
            Assert.Equal(i+1,entry.Sequence);
            Assert.Equal(entry.Id,Guid.Parse(id.Groups[1].Value));
            Assert.Equal("Accepted",status.Groups[1].Value);
            Assert.Equal(entry.Source,source.Groups[1].Value);
            Assert.Equal(entry.OccurredAt.ToString("O",CultureInfo.InvariantCulture),ExactTime(time.Groups[1].Value));
            ordered.Add($"{entry.Sequence}|{id.Groups[1].Value}|{status.Groups[1].Value}|{source.Groups[1].Value}|{ExactTime(time.Groups[1].Value)}");
        }
        // SOAP GetShipment and REST return summaries by frozen contract; HTML + fresh SQL prove full history.
        return new(actual,string.Join("\n",ordered));
    }
}
