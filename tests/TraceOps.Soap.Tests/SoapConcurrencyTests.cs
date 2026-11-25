using System.Xml;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapConcurrencyTests
{
    [Fact]
    [Trait("Acceptance", "M5-T04")]
    public async Task Sixteen_identical_parallel_SOAP_events_append_once_and_all_return_current_projection()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var eventId = Guid.NewGuid();
        var occurredAt = SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(2));
        var envelope = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                eventId,
                "InTransit",
                occurredAt,
                "test.soap"));

        var pending = Enumerable.Range(0, 16)
            .Select(_ => fixture.SendAsync(envelope, SoapTestContract.AddEventAction))
            .ToArray();
        var responses = await Task.WhenAll(pending);
        try
        {
            Assert.All(responses, response => Assert.Equal(200, (int)response.StatusCode));
            var results = new List<System.Xml.Linq.XElement>(responses.Length);
            foreach (var response in responses)
            {
                results.Add(await SoapTestContract.AssertSuccessResultAsync(
                    response, "AddShipmentEvent"));
            }

            var codes = results
                .Select(result => result.Element(SoapTestContract.Service + "code")?.Value)
                .ToArray();
            Assert.Equal(1, codes.Count(code => code == "APPLIED"));
            Assert.Equal(15, codes.Count(code => code == "DUPLICATE"));
            Assert.All(results, result =>
            {
                Assert.Equal(eventId.ToString("D"),
                    result.Element(SoapTestContract.Service + "eventId")?.Value);
                var shipment = result.Element(SoapTestContract.Service + "shipment")!;
                Assert.Equal("InTransit",
                    shipment.Element(SoapTestContract.Service + "status")?.Value);
                Assert.Equal("2",
                    shipment.Element(SoapTestContract.Service + "version")?.Value);
            });

            var after = await fixture.SnapshotAsync(baseline.TrackingNumber);
            Assert.Equal("InTransit", after.Status);
            Assert.Equal(baseline.Snapshot.Version + 1, after.Version);
            Assert.Equal(baseline.Snapshot.Events.Length + 1, after.Events.Length);
            Assert.Equal(1, after.Events.Count(item => item.EventId == eventId));
            Assert.Equal(eventId, after.Events[^1].EventId);
            Assert.Equal(occurredAt,
                SoapTestFixture.FormatUtc(after.Events[^1].OccurredAt));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
