using Microsoft.AspNetCore.Http;
using System.Xml;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapXmlSecurityTests
{
    public static IEnumerable<object[]> NonUtf8WireCases
    {
        get
        {
            foreach (var encoding in new[] { "utf16le", "utf16be", "utf32le", "utf32be" })
            foreach (var bom in new[] { false, true })
            foreach (var declaration in new[] { false, true })
                yield return new object[] { encoding, bom, declaration };
        }
    }

    [Theory]
    [MemberData(nameof(NonUtf8WireCases))]
    [Trait("Acceptance", "M5-T06-UTF8-REGRESSION")]
    public async Task Non_UTF8_actual_wire_bytes_are_INVALID_REQUEST_without_store_mutation(
        string wireEncoding, bool bom, bool declaration)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var absentTarget = SoapTestFixture.NewTrackingNumber();
        Assert.Null(await fixture.FindAsync(absentTarget));
        var unchanged = fixture.CaptureStore(baseline.TrackingNumber, absentTarget);
        var encoding = WireEncoding(wireEncoding);
        var xml = EncodingControlXml(baseline.TrackingNumber, declaration ? encoding.WebName : null);
        var bytes = EncodeWire(xml, encoding, bom);
        Assert.True(bytes.Length < 16384);
        Assert.Contains((byte)0, bytes);
        Assert.Equal(xml, encoding.GetString(bytes.AsSpan(bom ? encoding.GetPreamble().Length : 0)));
        // Independently decode the exact bytes: only transport encoding differs from the valid UTF-8 control.
        Assert.Equal(SoapTestContract.Soap12 + "Envelope", XDocument.Parse(xml).Root!.Name);
        using var request = RawEncodingRequest(bytes, baseline.TrackingNumber, absentTarget);
        using var response = await fixture.Client.SendAsync(request);
        unchanged();
        Assert.Null(await fixture.FindAsync(absentTarget));
        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Acceptance", "M5-T06-UTF8-REGRESSION")]
    public async Task Equivalent_UTF8_actual_wire_bytes_are_valid_readonly_control(bool bom, bool declaration)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var absentTarget = SoapTestFixture.NewTrackingNumber();
        var unchanged = fixture.CaptureStore(baseline.TrackingNumber, absentTarget);
        var xml = EncodingControlXml(baseline.TrackingNumber, declaration ? "utf-8" : null);
        var bytes = EncodeWire(xml, new UTF8Encoding(true, true), bom);
        Assert.True(bytes.Length < 16384);
        Assert.Contains("é", xml);
        using var request = RawEncodingRequest(bytes, baseline.TrackingNumber, absentTarget);
        using var response = await fixture.Client.SendAsync(request);
        unchanged();
        Assert.Null(await fixture.FindAsync(absentTarget));
        var result = await SoapTestContract.AssertSuccessResultAsync(response, "GetShipment");
        Assert.Equal("FOUND", result.Element(SoapTestContract.Service + "code")?.Value);
        SoapTestContract.AssertShipmentSummary(result.Element(SoapTestContract.Service + "shipment")!, baseline.Snapshot);
    }

    private static Encoding WireEncoding(string name) => name switch
    {
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        "utf32le" => new UTF32Encoding(false, true, true),
        "utf32be" => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string EncodingControlXml(string tracking, string? declaration)
    {
        // Business fields allow only ASCII. Non-ASCII belongs to a legal optional Header block.
        var xml = SoapTestContract.Envelope(SoapTestContract.GetOperation(tracking),
            header: new XElement(XName.Get("EncodingControl", "urn:traceops:optional-header"), "é"));
        return declaration is null ? xml : "<?xml version=\"1.0\" encoding=\"" + declaration + "\"?>" + xml;
    }

    private static byte[] EncodeWire(string xml, Encoding encoding, bool bom) =>
        (bom ? encoding.GetPreamble() : Array.Empty<byte>()).Concat(encoding.GetBytes(xml)).ToArray();

    private static HttpRequestMessage RawEncodingRequest(byte[] bytes, params string[] targets)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/soap/shipments")
        { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
            "application/soap+xml; charset=utf-8; action=\"" + SoapTestContract.GetAction + "\"");
        request.Options.Set(SoapStoreSnapshotHandler.TargetsKey, targets);
        Assert.Equal(bytes.Length, request.Content.Headers.ContentLength);
        return request;
    }


    [Fact]
    [Trait("Acceptance", "M5-S04-helper-control")]
    public async Task Counting_nonseekable_stream_control_reports_actual_bytes_without_seek_support()
    {
        using var stream = new CountingNonseekableStream(Encoding.UTF8.GetBytes("éabc"));
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        var buffer = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(buffer.AsMemory()));
        Assert.Equal(2, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, await stream.ReadAsync(buffer, 0, buffer.Length));
        Assert.Equal(5, stream.BytesRead);
        Assert.Equal(3, stream.LargestRead);
    }


    [Theory]
    [InlineData(16384)]
    [InlineData(16385)]
    [InlineData(65536)]
    [Trait("Acceptance", "M5-T06/S04")]
    public async Task Server_unknown_length_nonseekable_stream_consumes_only_the_bounded_probe(int actualBytes)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var target = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.PadToByteLength(SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(target)), actualBytes);
        Assert.Equal(actualBytes, Encoding.UTF8.GetByteCount(xml));
        Assert.Equal(SoapTestContract.Soap12 + "Envelope", XDocument.Parse(xml).Root!.Name);
        using var stream = new CountingNonseekableStream(Encoding.UTF8.GetBytes(xml));
        Assert.False(stream.CanSeek);
        var unchanged = fixture.CaptureStore(target, baseline.TrackingNumber);
        using var responseBody = new MemoryStream();
        long? arrivedLength = -1;
        bool arrivedNonseekable = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var context = await fixture.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/soap/shipments";
            ctx.Request.ContentType = "application/soap+xml; charset=utf-8; action=\"" + SoapTestContract.RegisterAction + "\"";
            ctx.Request.ContentLength = null;
            ctx.Request.Body = stream;
            ctx.Response.Body = responseBody;
            arrivedLength = ctx.Request.ContentLength;
            arrivedNonseekable = !ctx.Request.Body.CanSeek;
        }, timeout.Token);
        Assert.Null(arrivedLength);
        Assert.True(arrivedNonseekable);
        if (actualBytes > 16384) unchanged();
        using var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode)
        { Content = new ByteArrayContent(responseBody.ToArray()), RequestMessage = new HttpRequestMessage() };
        response.RequestMessage.Options.Set(SoapStoreSnapshotHandler.SnapshotKey, unchanged);
        if (context.Response.ContentType is { } mime)
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mime);
        response.Headers.TryAddWithoutValidation(SoapTestContract.CorrelationHeader,
            context.Response.Headers[SoapTestContract.CorrelationHeader].ToArray());
        if (actualBytes == 16384)
            await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
        else
            await SoapTestContract.AssertFaultAsync(response, 413, "PAYLOAD_TOO_LARGE");
        Assert.Equal(Math.Min(actualBytes, 16385), stream.BytesRead);
        Assert.True(stream.BytesRead <= 16385, "HTTP body buffering must stop after the overflow probe.");
    }

    [Theory]
    [InlineData("declared-utf16", "INVALID_REQUEST")]
    [InlineData("dtd-expansion", "INVALID_REQUEST")]
    [InlineData("xinclude-child", "INVALID_REQUEST")]
    [InlineData("escaped-injection", "INVALID_SOURCE_SYSTEM")]
    [Trait("Acceptance", "M5-T06/S04")]
    public async Task XML_security_defects_are_isolated_with_owned_network_observer(string defect, string expectedCode)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        using var canary = new OwnedLoopbackCanary(baseline.TrackingNumber);
        await canary.ProveObservableAndResetAsync();
        var operation = defect == "escaped-injection" ? fixture.ValidNextEvent(baseline)
            : SoapTestContract.GetOperation(baseline.TrackingNumber);
        var action = defect == "escaped-injection" ? SoapTestContract.AddEventAction : SoapTestContract.GetAction;
        if (defect == "xinclude-child")
            operation.Element(SoapTestContract.Service + "request")!.Add(new XElement(
                XName.Get("include", "http://www.w3.org/2001/XInclude"), new XAttribute("href", canary.Url)));
        if (defect == "escaped-injection")
        {
            var text = "<xi:include href=\"" + canary.Url + "\"/>";
            operation.Element(SoapTestContract.Service + "request")!.Element(SoapTestContract.Service + "sourceSystem")!.Value = text;
            Assert.Equal(text, XDocument.Parse(SoapTestContract.Envelope(operation))
                .Descendants(SoapTestContract.Service + "sourceSystem").Single().Value);
        }
        var xml = SoapTestContract.Envelope(operation);
        Assert.Equal(SoapTestContract.Soap12 + "Envelope", XDocument.Parse(xml).Root!.Name);
        if (defect == "declared-utf16") xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?>" + xml;
        if (defect == "dtd-expansion") xml = DtdEnvelope(
            "<!ENTITY a \"" + baseline.TrackingNumber + "\"><!ENTITY b \"&a;\">", "&b;");
        using var response = await SendObservedAsync(fixture, canary, xml, action);
        await SoapTestContract.AssertFaultAsync(response, 400, expectedCode);
    }

    [Fact]
    [Trait("Acceptance", "M5-T06/S04")]
    public async Task Legal_external_schema_hint_is_never_fetched_and_cannot_change_readonly_Get_result()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        using var canary = new OwnedLoopbackCanary(baseline.TrackingNumber);
        await canary.ProveObservableAndResetAsync();
        var operation = SoapTestContract.GetOperation(baseline.TrackingNumber);
        operation.SetAttributeValue(XName.Get("schemaLocation", "http://www.w3.org/2001/XMLSchema-instance"),
            SoapTestContract.Namespace + " " + canary.Url);
        var unchanged = fixture.CaptureStore(baseline.TrackingNumber);
        using var response = await SendObservedAsync(fixture, canary, SoapTestContract.Envelope(operation), SoapTestContract.GetAction);
        unchanged();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var result = await SoapTestContract.AssertSuccessResultAsync(response, "GetShipment");
            Assert.Equal("FOUND", result.Element(SoapTestContract.Service + "code")?.Value);
            SoapTestContract.AssertShipmentSummary(result.Element(SoapTestContract.Service + "shipment")!, baseline.Snapshot);
        }
        else await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
    }

    private static async Task<HttpResponseMessage> SendObservedAsync(SoapTestFixture fixture, OwnedLoopbackCanary canary,
        string xml, string action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = SoapTestContract.CreateRequest(xml, action);
        HttpResponseMessage response;
        try { response = await fixture.Client.SendAsync(request, timeout.Token); }
        finally { await Task.Delay(150); } // Observer remains live through completion and a short settlement window.
        Assert.Equal(0, canary.Connections);
        return response;
    }


    [Fact]
    [Trait("Acceptance", "M5-T06")]
    public async Task Valid_SOAP_XML_below_body_limit_is_control_success()
    {
        using var fixture = new SoapTestFixture();
        var tracking = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(tracking));
        Assert.True(Encoding.UTF8.GetByteCount(xml) < 16_384);

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        var result = await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
        Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
        var snapshot = await fixture.SnapshotAsync(tracking);
        Assert.Equal("Registered", snapshot.Status);
        Assert.Equal(0, snapshot.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M5-T06")]
    public async Task Valid_XML_at_exact_16384_byte_limit_is_accepted(bool chunked)
    {
        using var fixture = new SoapTestFixture();
        var tracking = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.PadToByteLength(
            SoapTestContract.Envelope(SoapTestContract.RegisterOperation(tracking)),
            16_384);
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.Equal(16_384, bytes.Length);

        using var request = SoapTestContract.CreateRequest(
            xml,
            SoapTestContract.RegisterAction,
            chunked: chunked);
        if (chunked)
        {
            Assert.Null(request.Content!.Headers.ContentLength);
            Assert.True(request.Headers.TransferEncodingChunked);
        }
        else
        {
            Assert.Equal(16_384, request.Content!.Headers.ContentLength);
        }

        using var response = await fixture.Client.SendAsync(request);

        var result = await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
        Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
        var snapshot = await fixture.SnapshotAsync(tracking);
        Assert.Equal("Registered", snapshot.Status);
        Assert.Equal(0, snapshot.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M5-T06")]
    public async Task Multibyte_UTF8_comment_counts_as_bytes_at_exact_16384_limit(bool chunked)
    {
        using var fixture = new SoapTestFixture();
        var tracking = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.PadToByteLength(
            SoapTestContract.Envelope(
                SoapTestContract.RegisterOperation(tracking),
                header: MultibyteCommentHeader()),
            16_384);
        Assert.Equal(16_384, Encoding.UTF8.GetByteCount(xml));
        Assert.Equal(1, Encoding.UTF8.GetByteCount(xml) - xml.Length);
        Assert.NotEqual(16_384, xml.Length);
        Assert.Equal(SoapTestContract.Soap12 + "Envelope",
            XDocument.Parse(xml).Root!.Name);

        using var request = SoapTestContract.CreateRequest(
            xml,
            SoapTestContract.RegisterAction,
            chunked: chunked);
        if (chunked)
        {
            Assert.Null(request.Content!.Headers.ContentLength);
            Assert.True(request.Headers.TransferEncodingChunked);
        }
        else
        {
            Assert.Equal(16_384, request.Content!.Headers.ContentLength);
        }

        using var response = await fixture.Client.SendAsync(request);

        var result = await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
        Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
        var snapshot = await fixture.SnapshotAsync(tracking);
        Assert.Equal("Registered", snapshot.Status);
        Assert.Equal(0, snapshot.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M5-T06")]
    public async Task Valid_XML_at_16385_bytes_is_413_fault_before_mutation(bool chunked)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.PadToByteLength(
            SoapTestContract.Envelope(SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                Guid.NewGuid(),
                "InTransit",
                SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(2)),
                "test.soap")),
            16_385);
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.Equal(16_385, bytes.Length);

        using var request = SoapTestContract.CreateRequest(
            xml,
            SoapTestContract.AddEventAction,
            chunked: chunked);
        if (chunked)
        {
            Assert.Null(request.Content!.Headers.ContentLength);
            Assert.True(request.Headers.TransferEncodingChunked);
        }
        else
        {
            Assert.Equal(16_385, request.Content!.Headers.ContentLength);
        }

        using var response = await fixture.Client.SendAsync(request);

        await SoapTestContract.AssertFaultAsync(response, 413, "PAYLOAD_TOO_LARGE");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M5-T06")]
    public async Task Multibyte_UTF8_comment_at_16385_bytes_is_rejected_by_actual_byte_count(
        bool chunked)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.PadToByteLength(
            SoapTestContract.Envelope(
                SoapTestContract.AddEventOperation(
                    baseline.TrackingNumber,
                    Guid.NewGuid(),
                    "InTransit",
                    SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(2)),
                    "test.soap"),
                header: MultibyteCommentHeader()),
            16_385);
        Assert.Equal(16_385, Encoding.UTF8.GetByteCount(xml));
        Assert.Equal(1, Encoding.UTF8.GetByteCount(xml) - xml.Length);
        Assert.Equal(16_384, xml.Length);
        Assert.Equal(SoapTestContract.Soap12 + "Envelope",
            XDocument.Parse(xml).Root!.Name);

        using var request = SoapTestContract.CreateRequest(
            xml,
            SoapTestContract.AddEventAction,
            chunked: chunked);
        if (chunked)
        {
            Assert.Null(request.Content!.Headers.ContentLength);
            Assert.True(request.Headers.TransferEncodingChunked);
        }
        else
        {
            Assert.Equal(16_385, request.Content!.Headers.ContentLength);
        }

        using var response = await fixture.Client.SendAsync(request);

        await SoapTestContract.AssertFaultAsync(response, 413, "PAYLOAD_TOO_LARGE");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T06")]
    public async Task Root_Envelope_depth_32_is_accepted_with_ignorable_header_extension()
    {
        using var fixture = new SoapTestFixture();
        var tracking = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(tracking),
            header: DepthHeader(32));
        Assert.Equal(32, MaximumElementDepth(XDocument.Parse(xml)));

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        var result = await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
        Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
        Assert.NotNull(await fixture.FindAsync(tracking));
    }

    [Fact]
    [Trait("Acceptance", "M5-T06")]
    public async Task Root_Envelope_depth_33_is_INVALID_REQUEST_and_leaves_history_unchanged()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var attemptedTracking = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(attemptedTracking),
            header: DepthHeader(33));
        Assert.Equal(33, MaximumElementDepth(XDocument.Parse(xml)));

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
        Assert.Null(await fixture.FindAsync(attemptedTracking));
    }

    [Fact]
    [Trait("Acceptance", "M5-T06")]
    public async Task Small_internal_DTD_entity_is_rejected_without_mutation()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = DtdEnvelope(
            "<!ENTITY benign \"" + baseline.TrackingNumber + "\">",
            "&benign;");

        using var response = await fixture.SendAsync(xml, SoapTestContract.GetAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T06")]
    public async Task External_entity_cannot_reach_owned_loopback_canary_or_mutate_state()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        using var canary = new OwnedLoopbackCanary(baseline.TrackingNumber);
        await canary.ProveObservableAndResetAsync();
        var xml = DtdEnvelope(
            "<!ENTITY owned SYSTEM \"" + canary.Url + "\">",
            "&owned;");

        using var response = await SendObservedAsync(fixture, canary, xml, SoapTestContract.GetAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T06")]
    public async Task Large_single_text_value_under_body_budget_reaches_field_validation()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var longSource = new string('a', 15_000);
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                Guid.NewGuid(),
                "InTransit",
                SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(2)),
                longSource));
        Assert.True(Encoding.UTF8.GetByteCount(xml) < 16_384);

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_SOURCE_SYSTEM");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    private static XElement DepthHeader(int totalDepth)
    {
        var extension = new XElement(XName.Get("Extension", "urn:traceops:optional-header"));
        var nestedCount = totalDepth - 2;
        for (var index = 1; index < nestedCount; index++)
        {
            extension = new XElement(XName.Get("Extension", "urn:traceops:optional-header"), extension);
        }
        return extension;
    }

    private static XElement MultibyteCommentHeader() =>
        new(XName.Get("OptionalText", "urn:traceops:optional-header"),
            new XComment("UTF-8 byte boundary: é"));

    private static int MaximumElementDepth(XDocument document)
    {
        static int Depth(XElement element, int current) =>
            element.Elements().Select(child => Depth(child, current + 1)).DefaultIfEmpty(current).Max();
        return Depth(document.Root!, 1);
    }

    private static string DtdEnvelope(string declaration, string entityReference)
    {
        var ns = SoapTestContract.Soap12.NamespaceName;
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<!DOCTYPE env:Envelope [" + declaration + "]>"
            + "<env:Envelope xmlns:env=\"" + ns + "\" xmlns:t=\""
            + SoapTestContract.Namespace + "\"><env:Body><t:GetShipment><t:request>"
            + "<t:trackingNumber>" + entityReference + "</t:trackingNumber>"
            + "</t:request></t:GetShipment></env:Body></env:Envelope>";
    }

    private sealed class CountingNonseekableStream(byte[] bytes) : Stream
    {
        private int _position;
        public int BytesRead => _position;
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private sealed class OwnedLoopbackCanary : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _observer;
        private int _connections;
        private readonly string _payload;
        public OwnedLoopbackCanary(string payload)
        {
            _payload = payload;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/owned-canary";
            _observer = ObserveAsync();
        }
        public string Url { get; }
        public int Connections => Volatile.Read(ref _connections);
        public async Task ProveObservableAndResetAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Assert.Equal(_payload, await client.GetStringAsync(Url));
            Assert.Equal(1, Connections);
            Interlocked.Exchange(ref _connections, 0);
            Assert.Equal(0, Connections);
        }
        private async Task ObserveAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    using var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) continue;
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + Encoding.ASCII.GetByteCount(_payload) + "\r\nConnection: close\r\n\r\n" + _payload);
                    await stream.WriteAsync(reply, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _observer.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
