using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using TraceOps.Application;
using TraceOps.Domain;
using TraceOps.Web.Middleware;
using TraceOps.Web.Storage;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapNegativeTests
{
    [Theory]
    [InlineData("foreign-root")]
    [InlineData("extra-body")]
    [InlineData("extra-payload")]
    [InlineData("extra-fault-field")]
    [InlineData("extra-detail")]
    [InlineData("extra-typed-field")]
    [InlineData("unqualified-code")]
    [InlineData("wrong-code-namespace")]
    [InlineData("local-prefix-rebinding")]
    [InlineData("wrong-language")]
    [InlineData("secret-reason")]
    [InlineData("debug-detail-text")]
    [Trait("Acceptance", "M5-S03-helper-control")]
    public void Fault_helper_rejects_malformed_synthetic_responses(string defect)
    {
        var document = XDocument.Parse(SyntheticFault());
        var root = document.Root!;
        var body = root.Element(SoapTestContract.Soap12 + "Body")!;
        var fault = body.Elements().Single();
        var detail = fault.Element(SoapTestContract.Soap12 + "Detail")!;
        var value = fault.Element(SoapTestContract.Soap12 + "Code")!.Elements().Single();
        switch (defect)
        {
            case "foreign-root": root.Name = XName.Get("Foreign", "urn:foreign"); break;
            case "extra-body": root.Add(new XElement(body)); break;
            case "extra-payload": body.Add(new XElement(fault)); break;
            case "extra-fault-field": fault.Add(new XElement("debug", "raw")); break;
            case "extra-detail": detail.Add(new XElement("debug", "raw")); break;
            case "extra-typed-field": detail.Elements().Single().Add(new XElement(SoapTestContract.Service + "debug", "raw")); break;
            case "unqualified-code":
                Assert.NotEqual(SoapTestContract.Soap12, value.GetDefaultNamespace());
                value.Value = "Sender";
                break;
            case "wrong-code-namespace": value.SetAttributeValue(XNamespace.Xmlns + "evil", "urn:evil"); value.Value = "evil:Sender"; break;
            case "local-prefix-rebinding":
                // Keep Value itself in SOAP while the QName's env prefix is locally rebound.
                value.SetAttributeValue("xmlns", SoapTestContract.Soap12.NamespaceName);
                value.SetAttributeValue(XNamespace.Xmlns + "env", "urn:foreign:local-rebinding");
                Assert.Equal(SoapTestContract.Soap12, root.GetNamespaceOfPrefix("env"));
                Assert.Equal("urn:foreign:local-rebinding", value.GetNamespaceOfPrefix("env")?.NamespaceName);
                value.Value = "env:Sender";
                break;
            case "wrong-language": fault.Element(SoapTestContract.Soap12 + "Reason")!.Elements().Single().SetAttributeValue(XNamespace.Xml + "lang", "tr"); break;
            case "secret-reason": fault.Element(SoapTestContract.Soap12 + "Reason")!.Elements().Single().Value = "SECRET"; break;
            case "debug-detail-text": detail.Add(new XText("rawdebug")); break;
        }
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => SoapTestContract.AssertFaultXml(
            document.ToString(), "INVALID_REQUEST", "Sender", ControlCorrelation));
    }

    [Fact]
    [Trait("Acceptance", "M5-S03-helper-control")]
    public async Task Strict_response_helpers_accept_valid_fault_and_success_with_optional_header()
    {
        SoapTestContract.AssertFaultXml(SyntheticFault(), "INVALID_REQUEST", "Sender", ControlCorrelation);
        var document = XDocument.Parse(SoapTestContract.Envelope(
            new XElement(SoapTestContract.Service + "GetShipmentResponse",
                new XElement(SoapTestContract.Service + "GetShipmentResult",
                    new XElement(SoapTestContract.Service + "code", "FOUND"),
                    new XElement(SoapTestContract.Service + "shipment",
                        new XElement(SoapTestContract.Service + "trackingNumber", "SOAP-CONTROL"),
                        new XElement(SoapTestContract.Service + "status", "Registered"),
                        new XElement(SoapTestContract.Service + "lastOccurredAt", SoapTestFixture.FormatUtc(SoapTestFixture.Now)),
                        new XElement(SoapTestContract.Service + "version", "0")))),
            header: new XElement(XName.Get("Optional", "urn:optional"))));
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(document.ToString(), Encoding.UTF8, "application/soap+xml") };
        response.Headers.Add(SoapTestContract.CorrelationHeader, ControlCorrelation);
        await SoapTestContract.AssertSuccessResultAsync(response, "GetShipment");
        document.Root!.Name = XName.Get("Foreign", "urn:foreign");
        response.Content = new StringContent(document.ToString(), Encoding.UTF8, "application/soap+xml");
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => SoapTestContract.AssertSuccessResultAsync(response, "GetShipment"));
    }

    [Theory]
    [InlineData("unprefixed-default-SOAP")]
    [InlineData("QName-XSD-whitespace")]
    [InlineData("generic-reason-over-256")]
    [Trait("Acceptance", "M5-S03-helper-control")]
    public void Fault_helper_accepts_spec_valid_QName_forms_and_long_generic_reason(string control)
    {
        var document = XDocument.Parse(SyntheticFault());
        var fault = document.Root!.Element(SoapTestContract.Soap12 + "Body")!.Elements().Single();
        var value = fault.Element(SoapTestContract.Soap12 + "Code")!.Elements().Single();
        var text = fault.Element(SoapTestContract.Soap12 + "Reason")!.Elements().Single();
        if (control == "unprefixed-default-SOAP")
        {
            value.SetAttributeValue("xmlns", SoapTestContract.Soap12.NamespaceName);
            value.Value = "Sender";
            Assert.Equal(SoapTestContract.Soap12, value.GetDefaultNamespace());
        }
        else if (control == "QName-XSD-whitespace")
            value.Value = " \t\r\nenv:Sender\r\n\t ";
        else
        {
            text.Value = string.Concat(Enumerable.Repeat("The request could not be processed. ", 10));
            Assert.True(text.Value.Length > 256);
        }
        var result = SoapTestContract.AssertFaultXml(document.ToString(), "INVALID_REQUEST", "Sender", ControlCorrelation);
        Assert.Equal("Sender", result.EnvelopeCode);
        Assert.Equal(text.Value, result.Reason);
    }

    private const string ControlCorrelation = "abcdefab-0000-0000-0000-000000000001";
    private static string SyntheticFault() => SoapTestContract.Envelope(
        new XElement(SoapTestContract.Soap12 + "Fault",
            new XElement(SoapTestContract.Soap12 + "Code", new XElement(SoapTestContract.Soap12 + "Value", "env:Sender")),
            new XElement(SoapTestContract.Soap12 + "Reason", new XElement(SoapTestContract.Soap12 + "Text",
                new XAttribute(XNamespace.Xml + "lang", "en"), "The request is invalid.")),
            new XElement(SoapTestContract.Soap12 + "Detail", new XElement(SoapTestContract.Service + "TraceOpsFault",
                new XElement(SoapTestContract.Service + "code", "INVALID_REQUEST"),
                new XElement(SoapTestContract.Service + "correlationId", ControlCorrelation)))),
        header: new XElement(XName.Get("Optional", "urn:optional")));


    [Theory]
    [InlineData("event-id-malformed", "INVALID_REQUEST")]
    [InlineData("event-id-non-D", "INVALID_REQUEST")]
    [InlineData("event-id-empty-guid", "INVALID_EVENT_ID")]
    [InlineData("trim-event-id", "INVALID_REQUEST")]
    [InlineData("numeric-status", "INVALID_STATUS")]
    [InlineData("unknown-status", "INVALID_STATUS")]
    [InlineData("malformed-time", "INVALID_REQUEST")]
    [InlineData("timezone-absent", "INVALID_REQUEST")]
    [InlineData("trim-tracking", "INVALID_TRACKING_NUMBER")]
    [InlineData("trim-status", "INVALID_STATUS")]
    [InlineData("trim-source", "INVALID_SOURCE_SYSTEM")]
    [InlineData("trim-time", "INVALID_REQUEST")]
    [InlineData("time-regression", "EVENT_TIME_REGRESSION")]
    [Trait("Acceptance", "M5-T04/M5-T05/S01")]
    public async Task Isolated_parse_and_domain_codes_preserve_the_entire_store(string defect, string code)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var operation = fixture.ValidNextEvent(baseline);
        var fields = operation.Element(SoapTestContract.Service + "request")!;
        var field = defect switch
        {
            var d when d.StartsWith("event-id", StringComparison.Ordinal) => "eventId",
            "trim-event-id" => "eventId",
            "numeric-status" or "unknown-status" or "trim-status" => "status",
            "trim-tracking" => "trackingNumber",
            "trim-source" => "sourceSystem",
            _ => "occurredAt",
        };
        var node = fields.Element(SoapTestContract.Service + field)!;
        node.Value = defect switch
        {
            "event-id-malformed" => "not-a-guid",
            "event-id-non-D" => Guid.NewGuid().ToString("N"),
            "event-id-empty-guid" => Guid.Empty.ToString("D"),
            "numeric-status" => "2",
            "unknown-status" => "Unknown",
            "malformed-time" => "2026-99-18T10:01:00Z",
            "timezone-absent" => "2026-11-18T10:01:00",
            "time-regression" => SoapTestFixture.FormatUtc(SoapTestFixture.Now),
            _ => " " + node.Value + " ",
        };
        using var response = await fixture.SendAsync(SoapTestContract.Envelope(operation), SoapTestContract.AddEventAction);
        await SoapTestContract.AssertFaultAsync(response, 400, code);
        SoapTestContract.AssertUnchanged(baseline.Snapshot, await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T05/S01")]
    public async Task Duplicate_tracking_after_fresh_SOAP_registration_is_conflict_without_any_store_change()
    {
        using var fixture = new SoapTestFixture();
        await fixture.CreateRestBaselineAsync();
        var target = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.Envelope(SoapTestContract.RegisterOperation(target));
        using var registration = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);
        var result = await SoapTestContract.AssertSuccessResultAsync(registration, "RegisterShipment");
        Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
        Assert.NotNull(await fixture.FindAsync(target));
        using var duplicate = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);
        await SoapTestContract.AssertFaultAsync(duplicate, 400, "TRACKING_NUMBER_CONFLICT");
    }

    public static IEnumerable<object[]> RequiredFieldCases
    {
        get
        {
            foreach (var operation in new[] { "GetShipment", "AddShipmentEvent" })
            foreach (var fieldName in operation == "GetShipment" ? new[] { "trackingNumber" }
                : new[] { "trackingNumber", "eventId", "status", "occurredAt", "sourceSystem" })
            foreach (var presence in new[] { "missing", "nil", "empty" })
            {
                var code = presence != "empty" ? "INVALID_REQUEST" : fieldName switch
                {
                    "trackingNumber" => "INVALID_TRACKING_NUMBER",
                    "status" => "INVALID_STATUS",
                    "sourceSystem" => "INVALID_SOURCE_SYSTEM",
                    _ => "INVALID_REQUEST",
                };
                yield return new object[] { operation, fieldName, presence, code };
            }
        }
    }

    [Theory]
    [MemberData(nameof(RequiredFieldCases))]
    [Trait("Acceptance", "M5-T05/S02")]
    public async Task Required_Get_and_Add_field_presence_is_distinct_from_empty_content(
        string operationName, string field, string presence, string code)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var operation = operationName == "GetShipment" ? SoapTestContract.GetOperation(baseline.TrackingNumber)
            : fixture.ValidNextEvent(baseline);
        var node = operation.Element(SoapTestContract.Service + "request")!.Element(SoapTestContract.Service + field)!;
        Assert.False(string.IsNullOrEmpty(node.Value));
        if (presence == "missing") node.Remove();
        else
        {
            node.Value = string.Empty;
            if (presence == "nil") node.SetAttributeValue(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"), "true");
        }
        using var response = await fixture.SendAsync(SoapTestContract.Envelope(operation),
            operationName == "GetShipment" ? SoapTestContract.GetAction : SoapTestContract.AddEventAction);
        await SoapTestContract.AssertFaultAsync(response, 400, code);
        SoapTestContract.AssertUnchanged(baseline.Snapshot, await fixture.SnapshotAsync(baseline.TrackingNumber));
    }


    [Theory]
    [MemberData(nameof(InvalidSchemaCases))]
    [Trait("Acceptance", "M5-T05")]
    public async Task Invalid_wire_shape_returns_named_fault_and_preserves_rest_projection_and_history(string scenario)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var target = scenario == "wrong-order" ? baseline.TrackingNumber : SoapTestFixture.NewTrackingNumber();
        if (scenario == "wrong-order") fixture.ValidNextEvent(baseline);
        else Assert.Null(await fixture.FindAsync(target));
        var xml = SoapTestContract.SchemaFailureEnvelope(scenario, target);

        var action = scenario == "wrong-order"
            ? SoapTestContract.AddEventAction
            : SoapTestContract.RegisterAction;
        using var response = await fixture.SendAsync(xml, action);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    public static IEnumerable<object[]> InvalidSchemaCases =>
    [
        ["unknown-child"],
        ["duplicate-field"],
        ["missing-field"],
        ["nil-field"],
        ["wrong-field-namespace"],
        ["wrong-request-namespace"],
        ["missing-request"],
        ["wrong-order"],
        ["multiple-operations"],
        ["multiple-bodies"],
        ["malformed-xml"],
    ];

    [Fact]
    [Trait("Acceptance", "M5-T05")]
    public async Task Empty_tracking_element_is_semantic_invalid_tracking_not_missing_xml()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(string.Empty));

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_TRACKING_NUMBER");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-cannot-be-rescued-by-soapaction")]
    [InlineData("wrong-full-uri")]
    [InlineData("body-action-mismatch")]
    [Trait("Acceptance", "M5-T05")]
    public async Task MIME_action_must_match_exact_known_operation(string scenario)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var operation = scenario == "body-action-mismatch"
            ? SoapTestContract.GetOperation(baseline.TrackingNumber)
            : SoapTestContract.RegisterOperation(SoapTestFixture.NewTrackingNumber());
        var xml = SoapTestContract.Envelope(operation);
        string? action = scenario switch
        {
            "missing" or "missing-cannot-be-rescued-by-soapaction" => null,
            "wrong-full-uri" => "urn:untrusted:prefix/ITraceOpsSoapService/RegisterShipment",
            _ => SoapTestContract.RegisterAction,
        };
        using var request = SoapTestContract.CreateRequest(xml, action);
        if (scenario == "missing-cannot-be-rescued-by-soapaction")
        {
            request.Headers.TryAddWithoutValidation(
                "SOAPAction",
                "\"" + SoapTestContract.RegisterAction + "\"");
        }

        using var response = await fixture.Client.SendAsync(request);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_ACTION");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T05")]
    public async Task SOAPAction_header_cannot_override_valid_MIME_action()
    {
        using var fixture = new SoapTestFixture();
        var tracking = SoapTestFixture.NewTrackingNumber();
        using var request = SoapTestContract.CreateRequest(
            SoapTestContract.Envelope(SoapTestContract.RegisterOperation(tracking)),
            SoapTestContract.RegisterAction);
        request.Headers.TryAddWithoutValidation(
            "SOAPAction",
            "\"urn:untrusted:prefix/ITraceOpsSoapService/GetShipment\"");

        using var response = await fixture.Client.SendAsync(request);

        var result = await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
        Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
        var snapshot = await fixture.SnapshotAsync(tracking);
        Assert.Equal(0, snapshot.Version);
        Assert.Equal("Registered", snapshot.Status);
    }

    [Theory]
    [InlineData("text/xml; charset=utf-8")]
    [InlineData("application/json")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=traceops-test")]
    [Trait("Acceptance", "M5-T05")]
    public async Task Non_SOAP12_media_types_return_415_fault_without_mutation(string mediaType)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        using var request = SoapTestContract.CreateRequest(
            SoapTestContract.Envelope(
                SoapTestContract.RegisterOperation(SoapTestFixture.NewTrackingNumber())),
            SoapTestContract.RegisterAction,
            mediaType);

        using var response = await fixture.Client.SendAsync(request);

        await SoapTestContract.AssertFaultAsync(response, 415, "UNSUPPORTED_MEDIA_TYPE");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T05")]
    public async Task SOAP11_envelope_returns_VersionMismatch_fault()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(SoapTestFixture.NewTrackingNumber()),
            SoapTestContract.Soap11);

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        await SoapTestContract.AssertFaultAsync(
            response, 500, "SOAP_VERSION_MISMATCH", "VersionMismatch");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Theory]
    [MemberData(nameof(MustUnderstandCases))]
    [Trait("Acceptance", "M5-T05")]
    public async Task SOAP12_mustUnderstand_obeys_target_role_and_boolean_lexical_form(
        string scenario, bool mustFault, string? detailCode)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var tracking = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(tracking),
            header: SoapTestContract.UnknownHeader(scenario));

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        if (mustFault)
        {
            await SoapTestContract.AssertFaultAsync(
                response, 500, detailCode!, "MustUnderstand");
            SoapTestContract.AssertUnchanged(
                baseline.Snapshot,
                await fixture.SnapshotAsync(baseline.TrackingNumber));
            Assert.Null(await fixture.FindAsync(tracking));
        }
        else
        {
            var result = await SoapTestContract.AssertSuccessResultAsync(response, "RegisterShipment");
            Assert.Equal("REGISTERED", result.Element(SoapTestContract.Service + "code")?.Value);
            Assert.NotNull(await fixture.FindAsync(tracking));
            SoapTestContract.AssertUnchanged(
                baseline.Snapshot,
                await fixture.SnapshotAsync(baseline.TrackingNumber));
        }
    }

    public static IEnumerable<object[]> MustUnderstandCases =>
    [
        ["absent-true", true, "SOAP_MUST_UNDERSTAND"],
        ["ultimate-true", true, "SOAP_MUST_UNDERSTAND"],
        ["next-true", true, "SOAP_MUST_UNDERSTAND"],
        ["absent-one", true, "SOAP_MUST_UNDERSTAND"],
        ["ultimate-one", true, "SOAP_MUST_UNDERSTAND"],
        ["next-one", true, "SOAP_MUST_UNDERSTAND"],
        ["absent-false", false, ""],
        ["absent-zero", false, ""],
        ["absent-absent", false, ""],
        ["ultimate-false", false, ""],
        ["ultimate-zero", false, ""],
        ["ultimate-absent", false, ""],
        ["next-false", false, ""],
        ["next-zero", false, ""],
        ["next-absent", false, ""],
        ["none-true", false, ""],
        ["none-one", false, ""],
        ["custom-true", false, ""],
        ["custom-one", false, ""],
    ];

    [Fact]
    [Trait("Acceptance", "M5-T05")]
    public async Task Malformed_mustUnderstand_boolean_returns_INVALID_REQUEST()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.RegisterOperation(SoapTestFixture.NewTrackingNumber()),
            header: SoapTestContract.UnknownHeader("malformed"));

        using var response = await fixture.SendAsync(xml, SoapTestContract.RegisterAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_REQUEST");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Theory]
    [InlineData("unknown-status", "INVALID_STATUS")]
    [InlineData("invalid-source", "INVALID_SOURCE_SYSTEM")]
    [InlineData("invalid-tracking", "INVALID_TRACKING_NUMBER")]
    [InlineData("missing-shipment", "SHIPMENT_NOT_FOUND")]
    [Trait("Acceptance", "M5-T05")]
    public async Task Single_domain_validation_fault_does_not_change_existing_history(
        string scenario, string expectedCode)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var tracking = scenario == "invalid-tracking"
            ? "lowercase"
            : scenario == "missing-shipment"
                ? SoapTestFixture.NewTrackingNumber()
                : baseline.TrackingNumber;
        var status = scenario == "unknown-status" ? "Unknown" : "InTransit";
        var source = scenario == "invalid-source" ? "bad source" : "test.soap";
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                tracking,
                Guid.NewGuid(),
                status,
                SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(1)),
                source));

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        await SoapTestContract.AssertFaultAsync(response, 400, expectedCode);
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T04")]
    public async Task Exact_REST_event_replay_returns_DUPLICATE_and_current_projection_without_append()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                baseline.EventId,
                "Accepted",
                baseline.EventTime,
                "test.api"));

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        var result = await SoapTestContract.AssertSuccessResultAsync(response, "AddShipmentEvent");
        Assert.Equal("DUPLICATE", result.Element(SoapTestContract.Service + "code")?.Value);
        Assert.Equal(baseline.EventId.ToString("D"), result.Element(SoapTestContract.Service + "eventId")?.Value);
        Assert.Equal("true", result.Element(SoapTestContract.Service + "duplicate")?.Value);
        SoapTestContract.AssertShipmentSummary(
            result.Element(SoapTestContract.Service + "shipment")!,
            baseline.Snapshot);
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T04")]
    public async Task Same_event_id_with_different_offset_conflicts_before_UTC_rejection()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var sameInstantDifferentOffset = SoapTestFixture.Now
            .AddSeconds(1)
            .ToOffset(TimeSpan.FromHours(2));
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                baseline.EventId,
                "Accepted",
                sameInstantDifferentOffset.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
                "test.api"));

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "EVENT_ID_CONFLICT");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Theory]
    [InlineData("nonzero-offset")]
    [InlineData("zero-offset-without-Z")]
    [Trait("Acceptance", "M5-T04")]
    public async Task New_event_timestamp_requires_explicit_UTC_Z_without_mutation(string scenario)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var time = scenario == "nonzero-offset"
            ? SoapTestFixture.Now.AddMinutes(3).ToOffset(TimeSpan.FromHours(2))
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)
            : SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(3))
                .Replace("Z", "+00:00", StringComparison.Ordinal);
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber, Guid.NewGuid(), "InTransit", time, "test.soap"));

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "TIMESTAMP_MUST_BE_UTC");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T04")]
    public async Task Invalid_lifecycle_transition_is_rejected_without_projection_or_history_change()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                Guid.NewGuid(),
                "Delivered",
                SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(5)),
                "test.soap"));

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        await SoapTestContract.AssertFaultAsync(response, 400, "INVALID_TRANSITION");
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T04")]
    public async Task Valid_lifecycle_then_old_exact_replay_returns_terminal_current_projection()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var firstSoapEventId = Guid.NewGuid();
        var firstSoapEventTime = SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(1));
        var sequence = new[]
        {
            ("InTransit", firstSoapEventId, firstSoapEventTime),
            ("OutForDelivery", Guid.NewGuid(), SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(2))),
            ("Delivered", Guid.NewGuid(), SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(3))),
        };

        foreach (var item in sequence)
        {
            var operation = SoapTestContract.AddEventOperation(
                baseline.TrackingNumber, item.Item2, item.Item1, item.Item3, "test.soap");
            using var response = await fixture.SendAsync(
                SoapTestContract.Envelope(operation), SoapTestContract.AddEventAction);
            var result = await SoapTestContract.AssertSuccessResultAsync(response, "AddShipmentEvent");
            Assert.Equal("APPLIED", result.Element(SoapTestContract.Service + "code")?.Value);
        }

        using var replay = await fixture.SendAsync(
            SoapTestContract.Envelope(SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                firstSoapEventId,
                "InTransit",
                firstSoapEventTime,
                "test.soap")),
            SoapTestContract.AddEventAction);
        var duplicate = await SoapTestContract.AssertSuccessResultAsync(replay, "AddShipmentEvent");
        Assert.Equal("DUPLICATE", duplicate.Element(SoapTestContract.Service + "code")?.Value);
        Assert.Equal("true", duplicate.Element(SoapTestContract.Service + "duplicate")?.Value);
        var current = await fixture.SnapshotAsync(baseline.TrackingNumber);
        Assert.Equal("Delivered", current.Status);
        Assert.Equal(4, current.Version);
        Assert.Equal(4, current.Events.Length);
        SoapTestContract.AssertShipmentSummary(
            duplicate.Element(SoapTestContract.Service + "shipment")!,
            current);
    }
}

internal sealed class SoapTestFactory(
    IShipmentStore? store = null,
    ILogger<ExceptionHandlingMiddleware>? exceptionLogger = null)
    : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TraceOps:Soap:Enabled"] = "true",
                ["TraceOps:SeedDemoData"] = "false",
                ["TraceOps:Http:Port"] = "5081",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new SoapFixedTimeProvider());
            if (store is not null)
            {
                services.RemoveAll<IShipmentStore>();
                services.AddSingleton(store);
            }
            if (exceptionLogger is not null)
            {
                services.RemoveAll<ILogger<ExceptionHandlingMiddleware>>();
                services.AddSingleton(exceptionLogger);
            }
        });
    }

    public HttpClient CreateSoapClient() => CreateDefaultClient(new SoapStoreSnapshotHandler(
        () => Services.GetRequiredService<IShipmentStore>()));

}

internal sealed class SoapTestFixture : IDisposable
{
    private readonly SoapTestFactory _factory;

    public SoapTestFixture(
        IShipmentStore? store = null,
        ILogger<ExceptionHandlingMiddleware>? exceptionLogger = null)
    {
        _factory = new SoapTestFactory(store, exceptionLogger);
        Client = _factory.CreateSoapClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;
    public TestServer Server => _factory.Server;

    public Task<HttpResponseMessage> SendAsync(
        string xml,
        string? action,
        string contentType = "application/soap+xml; charset=utf-8") =>
        SoapTestContract.SendAsync(Client, xml, action, contentType);

    public async Task<RestSoapBaseline> CreateRestBaselineAsync()
    {
        var tracking = NewTrackingNumber();
        var eventId = Guid.NewGuid();
        var eventTime = FormatUtc(Now.AddSeconds(1));
        using (var registration = await Client.PostAsync(
            "/api/v1/shipments",
            new StringContent(
                JsonSerializer.Serialize(new { trackingNumber = tracking }),
                Encoding.UTF8,
                "application/json")))
        {
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        }

        var eventPayload = JsonSerializer.Serialize(new
        {
            eventId = eventId.ToString("D"),
            status = "Accepted",
            occurredAt = eventTime,
            sourceSystem = "test.api",
        });
        using (var eventResponse = await Client.PostAsync(
            "/api/v1/shipments/" + tracking + "/events",
            new StringContent(eventPayload, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Created, eventResponse.StatusCode);
        }

        return new RestSoapBaseline(tracking, eventId, eventTime, await SnapshotAsync(tracking));
    }

    // All valid Add controls are checked against an immutable baseline before a single wire mutation.
    public XElement ValidNextEvent(RestSoapBaseline baseline)
    {
        var id = Guid.NewGuid();
        using var scope = Services.CreateScope();
        var current = scope.ServiceProvider.GetRequiredService<IShipmentStore>().Find(baseline.TrackingNumber)!;
        var candidate = new LifecycleEvent(id, baseline.TrackingNumber, ShipmentStatus.InTransit,
            Now.AddMinutes(2), "test.soap");
        Assert.Equal(ApplyEventCode.Applied, ShipmentLifecycle.Apply(current, candidate).Code);
        return SoapTestContract.AddEventOperation(baseline.TrackingNumber, id, "InTransit",
            FormatUtc(candidate.OccurredAt), candidate.SourceSystem);
    }

    public Action CaptureStore(params string[] targets) => SoapStoreSnapshotHandler.Capture(
        Services.GetRequiredService<IShipmentStore>(), targets);

    public async Task<SoapStateSnapshot> SnapshotAsync(string tracking)
    {
        using var response = await Client.GetAsync("/api/v1/shipments/" + tracking);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var trackingNumber = root.GetProperty("trackingNumber").GetString()!;
        var status = root.GetProperty("status").GetString()!;
        var lastOccurredAt = root.GetProperty("lastOccurredAt").GetString()!;
        var version = root.GetProperty("version").GetInt64();

        using var scope = Services.CreateScope();
        var details = scope.ServiceProvider.GetRequiredService<ShipmentService>().Find(tracking);
        Assert.NotNull(details);
        Assert.Equal(trackingNumber, details.Shipment.TrackingNumber);
        Assert.Equal(status, details.Shipment.Status.ToString());
        Assert.Equal(version, details.Shipment.Version);
        return new SoapStateSnapshot(
            trackingNumber,
            status,
            lastOccurredAt,
            version,
            details.Events.ToArray());
    }

    public async Task<ShipmentDetails?> FindAsync(string tracking)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ShipmentService>().Find(tracking);
    }

    public static string NewTrackingNumber() =>
        "SOAP-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static readonly DateTimeOffset Now =
        new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
    }
}

internal sealed record RestSoapBaseline(
    string TrackingNumber,
    Guid EventId,
    string EventTime,
    SoapStateSnapshot Snapshot);

internal sealed record SoapStateSnapshot(
    string TrackingNumber,
    string Status,
    string LastOccurredAt,
    long Version,
    LifecycleEvent[] Events);

internal sealed class SoapFixedTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => SoapTestFixture.Now;
}

internal static class SoapTestContract
{
    public const string Namespace = "urn:traceops:soap:v1";
    public const string RegisterAction = Namespace + "/ITraceOpsSoapService/RegisterShipment";
    public const string GetAction = Namespace + "/ITraceOpsSoapService/GetShipment";
    public const string AddEventAction = Namespace + "/ITraceOpsSoapService/AddShipmentEvent";
    public const string CorrelationHeader = "X-Correlation-ID";
    public const string TestSecret = "SOAP-TEST-SECRET-DO-NOT-LEAK";
    public static readonly XNamespace Service = Namespace;
    public static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    public static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace WrongService = "urn:traceops:soap:wrong";

    public static XElement RegisterOperation(string tracking) =>
        RegisterOperationField(new XElement(Service + "trackingNumber", tracking));

    public static XElement RegisterOperationField(XElement trackingField) =>
        RegisterOperationRequest(new XElement(Service + "request", trackingField));

    public static XElement RegisterOperationRequest(XElement? request)
    {
        var operation = new XElement(Service + "RegisterShipment");
        operation.Add(new XAttribute(XNamespace.Xmlns + "t", Service));
        if (request is not null)
        {
            operation.Add(request);
        }
        return operation;
    }

    public static XElement GetOperation(string tracking) =>
        new(Service + "GetShipment",
            new XAttribute(XNamespace.Xmlns + "t", Service),
            new XElement(Service + "request",
                new XElement(Service + "trackingNumber", tracking)));

    public static XElement AddEventOperation(
        string tracking,
        Guid eventId,
        string status,
        string occurredAt,
        string sourceSystem) =>
        new(Service + "AddShipmentEvent",
            new XAttribute(XNamespace.Xmlns + "t", Service),
            new XElement(Service + "request",
                new XElement(Service + "trackingNumber", tracking),
                new XElement(Service + "eventId", eventId.ToString("D")),
                new XElement(Service + "status", status),
                new XElement(Service + "occurredAt", occurredAt),
                new XElement(Service + "sourceSystem", sourceSystem)));

    public static XElement UnknownHeader(string scenario)
    {
        var header = new XElement(XName.Get("Unrecognized", "urn:unknown:header"));
        string? role = scenario switch
        {
            var value when value.StartsWith("ultimate-", StringComparison.Ordinal) =>
                Soap12.NamespaceName + "/role/ultimateReceiver",
            var value when value.StartsWith("next-", StringComparison.Ordinal) =>
                Soap12.NamespaceName + "/role/next",
            var value when value.StartsWith("none-", StringComparison.Ordinal) =>
                Soap12.NamespaceName + "/role/none",
            var value when value.StartsWith("custom-", StringComparison.Ordinal) =>
                "urn:traceops:test:unhandled-role",
            _ => null,
        };
        if (role is not null)
        {
            header.Add(new XAttribute(Soap12 + "role", role));
        }

        string? lexicalValue = scenario switch
        {
            var value when value.EndsWith("-true", StringComparison.Ordinal) => "true",
            var value when value.EndsWith("-one", StringComparison.Ordinal) => "1",
            var value when value.EndsWith("-false", StringComparison.Ordinal) => "false",
            var value when value.EndsWith("-zero", StringComparison.Ordinal) => "0",
            "absent-absent" or "ultimate-absent" or "next-absent" => null,
            "malformed" => "maybe",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (lexicalValue is not null)
        {
            header.Add(new XAttribute(Soap12 + "mustUnderstand", lexicalValue));
        }
        return header;
    }

    public static string Envelope(
        XElement operation,
        XNamespace? envelope = null,
        XElement? header = null)
    {
        var soap = envelope ?? Soap12;
        var root = new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "env", soap));
        if (header is not null)
        {
            root.Add(new XElement(soap + "Header", header));
        }
        root.Add(new XElement(soap + "Body", operation));
        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    public static string SchemaFailureEnvelope(string scenario, string tracking)
    {
        var fields = new XElement(Service + "trackingNumber", tracking);
        XElement? request = null;
        XElement operation;
        switch (scenario)
        {
            case "unknown-child":
                request = new XElement(Service + "request",
                    new XElement(Service + "trackingNumber", tracking),
                    new XElement(Service + "unexpected", "x"));
                operation = RegisterOperationRequest(request);
                break;
            case "duplicate-field":
                request = new XElement(Service + "request",
                    new XElement(Service + "trackingNumber", tracking),
                    new XElement(Service + "trackingNumber", tracking));
                operation = RegisterOperationRequest(request);
                break;
            case "missing-field":
                request = new XElement(Service + "request");
                operation = RegisterOperationRequest(request);
                break;
            case "nil-field":
                fields = new XElement(Service + "trackingNumber",
                    new XAttribute(Xsi + "nil", "true"));
                operation = RegisterOperationField(fields);
                break;
            case "wrong-field-namespace":
                request = new XElement(Service + "request",
                    new XElement(WrongService + "trackingNumber", tracking));
                operation = RegisterOperationRequest(request);
                break;
            case "wrong-request-namespace":
                request = new XElement(WrongService + "request", fields);
                operation = RegisterOperationRequest(request);
                break;
            case "missing-request":
                operation = RegisterOperationRequest(null);
                break;
            case "wrong-order":
                operation = AddEventOperation(tracking, Guid.NewGuid(), "InTransit",
                    SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(1)), "test.soap");
                var requestNode = operation.Element(Service + "request")!;
                requestNode.ReplaceNodes(
                    requestNode.Element(Service + "trackingNumber")!,
                    requestNode.Element(Service + "status")!,
                    requestNode.Element(Service + "eventId")!,
                    requestNode.Element(Service + "occurredAt")!,
                    requestNode.Element(Service + "sourceSystem")!);
                break;
            case "multiple-operations":
                var root = new XElement(Soap12 + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "env", Soap12),
                    new XElement(Soap12 + "Body",
                        RegisterOperation(tracking),
                        RegisterOperation(tracking)));
                return new XDocument(root).ToString(SaveOptions.DisableFormatting);
            case "multiple-bodies":
                var duplicateBodyRoot = new XElement(Soap12 + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "env", Soap12),
                    new XElement(Soap12 + "Body", RegisterOperation(tracking)),
                    new XElement(Soap12 + "Body", RegisterOperation(tracking)));
                return new XDocument(duplicateBodyRoot).ToString(SaveOptions.DisableFormatting);
            case "malformed-xml":
                return "<env:Envelope xmlns:env=\"" + Soap12.NamespaceName
                    + "\"><env:Body><t:RegisterShipment xmlns:t=\"" + Namespace
                    + "\"><t:request>";
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return Envelope(operation);
    }

    public static HttpRequestMessage CreateRequest(
        string xml,
        string? action,
        string contentType = "application/soap+xml; charset=utf-8",
        bool chunked = false)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        HttpContent content = chunked
            ? new UnknownLengthContent(bytes)
            : new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (action is not null
            && string.Equals(
                content.Headers.ContentType.MediaType,
                "application/soap+xml",
                StringComparison.OrdinalIgnoreCase))
        {
            content.Headers.ContentType.Parameters.Add(
                new NameValueHeaderValue("action", "\"" + action + "\""));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/soap/shipments")
        {
            Content = content,
        };
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }
        try
        {
            request.Options.Set(SoapStoreSnapshotHandler.TargetsKey, XDocument.Parse(xml)
                .Descendants(Service + "trackingNumber").Select(e => e.Value).Distinct().ToArray());
        }
        catch (XmlException) { }
        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string xml,
        string? action,
        string contentType = "application/soap+xml; charset=utf-8")
    {
        using var request = CreateRequest(xml, action, contentType);
        return await client.SendAsync(request);
    }

    public static async Task<XElement> AssertSuccessResultAsync(
        HttpResponseMessage response,
        string operation)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSoapMime(response);
        Correlation(response);
        var payload = StrictPayload(await response.Content.ReadAsStringAsync());
        Assert.Equal(Service + (operation + "Response"), payload.Name);
        ExactChildren(payload, Service + (operation + "Result"));
        var result = payload.Elements().Single();
        ExactChildren(result, operation == "AddShipmentEvent"
            ? new[] { Service + "code", Service + "eventId", Service + "duplicate", Service + "shipment" }
            : new[] { Service + "code", Service + "shipment" });
        var summary = result.Element(Service + "shipment")!;
        ExactChildren(summary, Service + "trackingNumber", Service + "status", Service + "lastOccurredAt", Service + "version");
        Assert.All(summary.Elements(), leaf => Assert.Empty(leaf.Elements()));
        return new XElement(result);
    }

    public static async Task<SoapFaultResult> AssertFaultAsync(
        HttpResponseMessage response, int status, string detailCode, string envelopeCode = "Sender")
    {
        Assert.NotNull(response.RequestMessage);
        Assert.True(response.RequestMessage!.Options.TryGetValue(SoapStoreSnapshotHandler.SnapshotKey, out var unchanged),
            "Every B fault must compare the entire store and attempted targets.");
        unchanged!();
        Assert.Equal(status, (int)response.StatusCode);
        AssertSoapMime(response);
        var raw = await response.Content.ReadAsStringAsync();
        return AssertFaultXml(raw, detailCode, envelopeCode, Correlation(response));
    }

    public static void AssertSoapMime(HttpResponseMessage response)
    {
        Assert.Equal("application/soap+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet?.Trim('"').ToLowerInvariant());
    }

    public static SoapFaultResult AssertFaultXml(string raw, string detailCode, string envelopeCode, string headerId)
    {
        var fault = StrictPayload(raw, envelopeCode == "MustUnderstand");
        Assert.Equal(Soap12 + "Fault", fault.Name);
        ExactChildren(fault, Soap12 + "Code", Soap12 + "Reason", Soap12 + "Detail");
        var code = fault.Element(Soap12 + "Code")!;
        ExactChildren(code, Soap12 + "Value");
        var value = code.Elements().Single();
        Assert.Empty(value.Elements());
        Assert.All(value.Attributes(), attribute => Assert.True(attribute.IsNamespaceDeclaration));
        Assert.Equal(Soap12 + envelopeCode, ResolveQName(value, value.Value));
        var reason = fault.Element(Soap12 + "Reason")!;
        ExactChildren(reason, Soap12 + "Text");
        var text = reason.Elements().Single();
        Assert.Empty(text.Elements());
        Assert.All(text.Attributes(), attribute => Assert.True(attribute.IsNamespaceDeclaration || attribute.Name == XNamespace.Xml + "lang"));
        Assert.Equal("en", text.Attribute(XNamespace.Xml + "lang")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(text.Value));
        var detail = fault.Element(Soap12 + "Detail")!;
        ExactChildren(detail, Service + "TraceOpsFault");
        var typed = detail.Elements().Single();
        ExactChildren(typed, Service + "code", Service + "correlationId");
        Assert.All(typed.Elements(), leaf =>
        {
            Assert.Empty(leaf.Elements());
            Assert.All(leaf.Attributes(), a => Assert.True(a.IsNamespaceDeclaration));
        });
        Assert.Equal(detailCode, typed.Element(Service + "code")!.Value);
        var id = typed.Element(Service + "correlationId")!.Value;
        Assert.True(Guid.TryParseExact(id, "D", out _));
        Assert.Equal(headerId, id);
        foreach (var unsafeText in new[] { "SECRET", "StackTrace", "InvalidOperationException", "password=", "connection string", "rawdebug", " at TraceOps.", "<script", "SELECT " })
            Assert.DoesNotContain(unsafeText, raw, StringComparison.OrdinalIgnoreCase);
        return new SoapFaultResult(detailCode, envelopeCode, id, text.Value, raw);
    }

    public static XElement StrictPayload(string raw, bool allowNotUnderstood = false)
    {
        var document = XDocument.Parse(raw);
        var root = document.Root!;
        Assert.Equal(Soap12 + "Envelope", root.Name);
        Assert.All(root.Attributes(), attribute => Assert.True(attribute.IsNamespaceDeclaration));
        Assert.All(root.Nodes().OfType<XText>(), t => Assert.True(string.IsNullOrWhiteSpace(t.Value)));
        var children = root.Elements().ToArray();
        Assert.True(children.Select(e => e.Name).SequenceEqual(new[] { Soap12 + "Body" })
            || children.Select(e => e.Name).SequenceEqual(new[] { Soap12 + "Header", Soap12 + "Body" }));
        if (root.Element(Soap12 + "Header") is { } header)
        {
            // Legal optional SOAP header extensions remain allowed. NotUnderstood is fault-only metadata.
            foreach (var item in header.Elements().Where(e => e.Name == Soap12 + "NotUnderstood"))
            {
                Assert.True(allowNotUnderstood);
                Assert.Empty(item.Elements());
                var qname = item.Attribute("qname")?.Value;
                Assert.NotNull(qname);
                ResolveQName(item, qname!);
            }
        }
        var body = root.Element(Soap12 + "Body")!;
        Assert.All(body.Attributes(), attribute => Assert.True(attribute.IsNamespaceDeclaration));
        Assert.All(body.Nodes().OfType<XText>(), t => Assert.True(string.IsNullOrWhiteSpace(t.Value)));
        return Assert.Single(body.Elements());
    }

    private static void ExactChildren(XElement parent, params XName[] names)
    {
        Assert.All(parent.Attributes(), attribute => Assert.True(attribute.IsNamespaceDeclaration));
        Assert.Equal(names, parent.Elements().Select(e => e.Name).ToArray());
        Assert.All(parent.Nodes().OfType<XText>(), t => Assert.True(string.IsNullOrWhiteSpace(t.Value)));
    }

    public static void AssertShipmentSummary(XElement xml, SoapStateSnapshot snapshot)
    {
        Assert.Equal(snapshot.TrackingNumber,
            xml.Element(Service + "trackingNumber")?.Value);
        Assert.Equal(snapshot.Status,
            xml.Element(Service + "status")?.Value);
        Assert.Equal(snapshot.LastOccurredAt,
            xml.Element(Service + "lastOccurredAt")?.Value);
        var versionElement = xml.Element(Service + "version");
        Assert.NotNull(versionElement);
        Assert.Equal(snapshot.Version, XmlConvert.ToInt64(versionElement!.Value));
    }

    public static void AssertUnchanged(SoapStateSnapshot before, SoapStateSnapshot after)
    {
        Assert.Equal(before.TrackingNumber, after.TrackingNumber);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.LastOccurredAt, after.LastOccurredAt);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Events, after.Events);
    }

    public static string Correlation(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(CorrelationHeader, out var values));
        var value = Assert.Single(values!);
        Assert.True(Guid.TryParseExact(value, "D", out _));
        return value;
    }

    public static XName ResolveQName(XElement context, string lexical)
    {
        // xsd:QName whitespace is collapsed; surrounding XML whitespace is insignificant.
        // Internal whitespace remains invalid NCName syntax. Business strings are untouched.
        lexical = lexical.Trim(' ', '\t', '\r', '\n');
        var parts = lexical.Split(':');
        Assert.InRange(parts.Length, 1, 2);
        foreach (var part in parts)
        {
            var valid = true;
            try { XmlConvert.VerifyNCName(part); }
            catch (XmlException) { valid = false; }
            Assert.True(valid, "QName prefix and local name must be valid XML NCNames.");
        }
        var separator = lexical.IndexOf(':');
        if (separator < 0)
        {
            return XName.Get(lexical, context.GetDefaultNamespace().NamespaceName);
        }
        var prefix = lexical[..separator];
        var localName = lexical[(separator + 1)..];
        var namespaceName = context.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        Assert.False(string.IsNullOrEmpty(namespaceName));
        return XName.Get(localName, namespaceName!);
    }

    public static string PadToByteLength(string xml, int targetLength)
    {
        var close = "</env:Body>";
        var position = xml.LastIndexOf(close, StringComparison.Ordinal);
        Assert.True(position >= 0, "Fixture must contain an explicit SOAP Body end tag.");
        var currentLength = Encoding.UTF8.GetByteCount(xml);
        var paddingLength = targetLength - currentLength;
        Assert.True(paddingLength >= 0);
        var padded = xml.Insert(position, new string(' ', paddingLength));
        Assert.Equal(targetLength, Encoding.UTF8.GetByteCount(padded));
        return padded;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

internal sealed record SoapFaultResult(
    string DetailCode,
    string EnvelopeCode,
    string CorrelationId,
    string Reason,
    string RawXml);

internal sealed class SwitchableSoapStore : IShipmentStore
{
    private readonly InMemoryShipmentStore _inner = new();
    private int _applyCalls;

    public bool ThrowOnApply { get; set; }
    public int ApplyCalls => Volatile.Read(ref _applyCalls);
    public ShipmentState? Find(string trackingNumber) => _inner.Find(trackingNumber);
    public IReadOnlyList<ShipmentState> List() => _inner.List();
    public bool TryAdd(ShipmentState state) => _inner.TryAdd(state);

    public ApplyEventResult? ApplyAtomically(LifecycleEvent input)
    {
        Interlocked.Increment(ref _applyCalls);
        if (ThrowOnApply)
        {
            throw new InvalidOperationException(
                TestSecretAndStack());
        }
        return _inner.ApplyAtomically(input);
    }

    private static string TestSecretAndStack() =>
        SoapTestContract.TestSecret + Environment.NewLine
        + "PrivateTestException.StackTrace";
}

internal sealed class SoapStoreSnapshotHandler(Func<IShipmentStore> store) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<string[]> TargetsKey = new("B.AttemptedTargets");
    internal static readonly HttpRequestOptionsKey<Action> SnapshotKey = new("B.WholeStoreUnchanged");
    internal static Action Capture(IShipmentStore store, params string[] targets)
    {
        string All() => JsonSerializer.Serialize(store.List().OrderBy(s => s.TrackingNumber, StringComparer.Ordinal));
        var before = All();
        var attempted = targets.Distinct(StringComparer.Ordinal).ToDictionary(t => t, t => JsonSerializer.Serialize(store.Find(t)));
        return () =>
        {
            Assert.Equal(before, All());
            foreach (var target in attempted)
                Assert.Equal(target.Value, JsonSerializer.Serialize(store.Find(target.Key)));
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath == "/soap/shipments")
        {
            request.Options.TryGetValue(TargetsKey, out var targets);
            request.Options.Set(SnapshotKey, Capture(store(), targets ?? []));
        }
        var response = await base.SendAsync(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }
}
