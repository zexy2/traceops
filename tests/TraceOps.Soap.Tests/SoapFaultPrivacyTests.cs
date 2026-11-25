using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using TraceOps.Web.Middleware;
using System.Xml.Linq;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapFaultPrivacyTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("valid")]
    [InlineData("malformed")]
    [InlineData("multiple")]
    [Trait("Acceptance", "M5-T07")]
    public async Task Correlation_forms_return_SOAP_fault_with_matching_safe_header_and_no_mutation(string kind)
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var target = SoapTestFixture.NewTrackingNumber();
        var xml = SoapTestContract.Envelope(kind is "malformed" or "multiple"
            ? SoapTestContract.RegisterOperation(target) : SoapTestContract.RegisterOperationRequest(null));
        using var request = SoapTestContract.CreateRequest(xml, SoapTestContract.RegisterAction);
        const string validId = "ABCDEFAB-0000-0000-0000-000000000001";
        var invalidIds = new[] { "malformed-correlation", "not-a-guid" };
        var expectedCode = "INVALID_REQUEST";

        switch (kind)
        {
            case "valid":
                request.Headers.TryAddWithoutValidation(
                    SoapTestContract.CorrelationHeader,
                    validId);
                break;
            case "malformed":
                expectedCode = "INVALID_CORRELATION_ID";
                request.Headers.TryAddWithoutValidation(
                    SoapTestContract.CorrelationHeader,
                    invalidIds[0]);
                break;
            case "multiple":
                expectedCode = "INVALID_CORRELATION_ID";
                request.Headers.TryAddWithoutValidation(
                    SoapTestContract.CorrelationHeader,
                    new[] { validId, invalidIds[1] });
                break;
        }

        using var response = await fixture.Client.SendAsync(request);

        var fault = await SoapTestContract.AssertFaultAsync(response, 400, expectedCode);
        if (kind == "valid")
        {
            Assert.Equal(validId, fault.CorrelationId);
        }
        if (kind is "malformed" or "multiple")
        {
            Assert.DoesNotContain(fault.CorrelationId, invalidIds);
            Assert.NotEqual(validId, fault.CorrelationId);
        }
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }

    [Fact]
    [Trait("Acceptance", "M5-T07")]
    public async Task Present_empty_header_is_injected_into_DefaultHttpContext_and_observed_as_arrived()
    {
        using var fixture = new SoapTestFixture();
        var baseline = await fixture.CreateRestBaselineAsync();
        var target = SoapTestFixture.NewTrackingNumber();
        var unchanged = fixture.CaptureStore(baseline.TrackingNumber, target);
        var requestXml = SoapTestContract.Envelope(SoapTestContract.RegisterOperation(target));
        var requestBytes = Encoding.UTF8.GetBytes(requestXml);
        var directContext = new DefaultHttpContext();
        directContext.Request.Headers[SoapTestContract.CorrelationHeader] =
            new StringValues(string.Empty);
        var explicitEmptyValue = directContext.Request.Headers[SoapTestContract.CorrelationHeader];
        Assert.Equal(string.Empty, Assert.Single(explicitEmptyValue));
        using var responseBody = new MemoryStream();
        StringValues arrivedValues = StringValues.Empty;
        var headerWasPresent = false;

        // TestServer's HttpClient drops blank headers; inject into its live context.
        var context = await fixture.Server.SendAsync(requestContext =>
        {
            requestContext.Request.Method = HttpMethods.Post;
            requestContext.Request.Path = "/soap/shipments";
            requestContext.Request.ContentType = "application/soap+xml; charset=utf-8; action=\"" + SoapTestContract.RegisterAction + "\"";
            requestContext.Request.ContentLength = requestBytes.Length;
            requestContext.Request.Body = new MemoryStream(requestBytes);
            requestContext.Request.Headers[SoapTestContract.CorrelationHeader] =
                explicitEmptyValue;
            requestContext.Response.Body = responseBody;
            headerWasPresent = requestContext.Request.Headers.TryGetValue(
                SoapTestContract.CorrelationHeader,
                out arrivedValues);
        });

        unchanged();
        Assert.True(headerWasPresent);
        Assert.Equal(1, arrivedValues.Count);
        Assert.Equal(string.Empty, arrivedValues[0]);
        var raw = Encoding.UTF8.GetString(responseBody.ToArray());
        var actualMediaType = context.Response.ContentType?.Split(';')[0];
        var actualStatus = context.Response.StatusCode;
        var actualCorrelation = context.Response.Headers[SoapTestContract.CorrelationHeader].ToString();
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));

        Assert.Equal(StatusCodes.Status400BadRequest, actualStatus);
        Assert.Equal("application/soap+xml", actualMediaType);
        Assert.Equal("utf-8", System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Response.ContentType!).CharSet);
        SoapTestContract.AssertFaultXml(raw, "INVALID_CORRELATION_ID", "Sender", actualCorrelation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "M5-T07")]
    public async Task Development_provider_exception_and_error_logger_failure_stay_safe_SOAP_faults(
        bool errorLoggerThrows)
    {
        var store = new SwitchableSoapStore();
        var logger = new TrackingExceptionLogger(errorLoggerThrows);
        using var fixture = new SoapTestFixture(store, logger);
        var baseline = await fixture.CreateRestBaselineAsync();
        var applyCallsBefore = store.ApplyCalls;
        var errorLogsBefore = logger.ErrorCount;
        Assert.Equal(1, applyCallsBefore);
        Assert.Equal(0, errorLogsBefore);
        var xml = SoapTestContract.Envelope(
            SoapTestContract.AddEventOperation(
                baseline.TrackingNumber,
                Guid.NewGuid(),
                "InTransit",
                SoapTestFixture.FormatUtc(SoapTestFixture.Now.AddMinutes(2)),
                "test.soap"));
        store.ThrowOnApply = true;

        using var response = await fixture.SendAsync(xml, SoapTestContract.AddEventAction);

        var fault = await SoapTestContract.AssertFaultAsync(
            response, 500, "INTERNAL_ERROR", "Receiver");
        Assert.DoesNotContain(SoapTestContract.TestSecret, fault.RawXml, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateTestException.StackTrace", fault.RawXml, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", fault.RawXml, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", fault.RawXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", fault.RawXml, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.ApplyCalls > applyCallsBefore,
            "Fault injection must reach the selected store operation after the REST baseline.");
        Assert.True(logger.ErrorCount > errorLogsBefore,
            "SOAP provider failure must call the injected error logger.");
        if (errorLoggerThrows)
        {
            Assert.True(logger.ErrorThrowCount > 0,
                "Throwing error logger must be invoked and its failure path exercised.");
        }
        else
        {
            Assert.Equal(0, logger.ErrorThrowCount);
        }
        SoapTestContract.AssertUnchanged(
            baseline.Snapshot,
            await fixture.SnapshotAsync(baseline.TrackingNumber));
    }
}

internal sealed class TrackingExceptionLogger(bool throwOnError)
    : ILogger<ExceptionHandlingMiddleware>
{
    private int _errorCount;
    private int _errorThrowCount;

    public int ErrorCount => Volatile.Read(ref _errorCount);
    public int ErrorThrowCount => Volatile.Read(ref _errorThrowCount);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel != LogLevel.Error)
        {
            return;
        }

        Interlocked.Increment(ref _errorCount);
        if (throwOnError)
        {
            Interlocked.Increment(ref _errorThrowCount);
            throw new InvalidOperationException(
                SoapTestContract.TestSecret + Environment.NewLine + "ErrorLogger.StackTrace");
        }
    }
}
