using System.Text;
using System.Xml;
using System.Xml.Linq;
using TraceOps.Web.Middleware;

namespace TraceOps.Web.Soap;

internal sealed record ValidatedSoapRequest(string OperationName);

internal sealed class SoapRequestValidationException(
    int statusCode,
    string detailCode,
    string envelopeCode) : Exception
{
    public int StatusCode { get; } = statusCode;
    public string DetailCode { get; } = detailCode;
    public string EnvelopeCode { get; } = envelopeCode;
}

internal static class SoapRequestValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly XNamespace Soap12 = SoapFaultWriter.Soap12Namespace;
    private static readonly XNamespace Soap11 = SoapFaultWriter.Soap11Namespace;
    private static readonly XNamespace Service = SoapContractMetadata.Namespace;
    private static readonly XNamespace XmlSchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";
    private const string UltimateReceiver = SoapFaultWriter.Soap12Namespace + "/role/ultimateReceiver";
    private const string NextRole = SoapFaultWriter.Soap12Namespace + "/role/next";

    private static readonly IReadOnlyDictionary<string, string[]> Operations =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["RegisterShipment"] = ["trackingNumber"],
            ["GetShipment"] = ["trackingNumber"],
            ["AddShipmentEvent"] = ["trackingNumber", "eventId", "status", "occurredAt", "sourceSystem"],
        };

    public static ValidatedSoapRequest Validate(byte[] bytes, string action)
    {
        if (HasNonUtf8Bom(bytes))
        {
            throw InvalidRequest();
        }

        XDocument document;
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            var xml = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            // Parse decoded UTF-8 text so XML encoding autodetection cannot accept other wire encodings.
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = RequestBodyLimitMiddleware.MaximumBodyBytes,
                MaxCharactersFromEntities = 0,
                IgnoreWhitespace = false,
                IgnoreComments = false,
                CheckCharacters = true,
                CloseInput = false,
                ConformanceLevel = ConformanceLevel.Document,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidRequest();
        }
        catch (XmlException)
        {
            throw InvalidRequest();
        }
        catch (InvalidOperationException)
        {
            throw InvalidRequest();
        }

        if (document.Declaration?.Encoding is { Length: > 0 } declaredEncoding
            && !string.Equals(declaredEncoding, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidRequest();
        }

        var root = document.Root;
        if (root?.Name == Soap11 + "Envelope")
        {
            throw new SoapRequestValidationException(
                StatusCodes.Status500InternalServerError,
                "SOAP_VERSION_MISMATCH",
                "VersionMismatch");
        }

        if (root?.Name != Soap12 + "Envelope")
        {
            throw InvalidRequest();
        }

        EnsureDepthAtMost(root, 1);
        EnsureNoUnexpectedAttributes(root);
        EnsureContainerText(root);

        var envelopeChildren = root.Elements().ToArray();
        XElement? header = null;
        XElement body;
        if (envelopeChildren.Length == 1 && envelopeChildren[0].Name == Soap12 + "Body")
        {
            body = envelopeChildren[0];
        }
        else if (envelopeChildren.Length == 2
            && envelopeChildren[0].Name == Soap12 + "Header"
            && envelopeChildren[1].Name == Soap12 + "Body")
        {
            header = envelopeChildren[0];
            body = envelopeChildren[1];
        }
        else
        {
            throw InvalidRequest();
        }

        if (header is not null)
        {
            EnsureNoUnexpectedAttributes(header);
            EnsureContainerText(header);
            ValidateHeaders(header);
        }

        EnsureNoUnexpectedAttributes(body);
        EnsureContainerText(body);
        var bodyElements = body.Elements().ToArray();
        if (bodyElements.Length != 1)
        {
            throw InvalidRequest();
        }

        var operation = bodyElements[0];
        if (operation is null
            || operation.Name.Namespace != Service
            || !Operations.TryGetValue(operation.Name.LocalName, out var expectedFields))
        {
            throw InvalidRequest();
        }

        var expectedAction = operation.Name.LocalName switch
        {
            "RegisterShipment" => SoapContractMetadata.RegisterAction,
            "GetShipment" => SoapContractMetadata.GetAction,
            "AddShipmentEvent" => SoapContractMetadata.AddEventAction,
            _ => string.Empty,
        };
        if (!string.Equals(action, expectedAction, StringComparison.Ordinal))
        {
            throw new SoapRequestValidationException(
                StatusCodes.Status400BadRequest,
                "INVALID_ACTION",
                "Sender");
        }

        EnsureNoUnexpectedAttributes(operation);
        EnsureContainerText(operation);
        var operationElements = operation.Elements().ToArray();
        if (operationElements.Length != 1)
        {
            throw InvalidRequest();
        }

        var request = operationElements[0];
        if (request?.Name != Service + "request")
        {
            throw InvalidRequest();
        }

        EnsureNoUnexpectedAttributes(request);
        EnsureContainerText(request);
        var fields = request.Elements().ToArray();
        if (fields.Length != expectedFields.Length)
        {
            throw InvalidRequest();
        }

        for (var index = 0; index < expectedFields.Length; index++)
        {
            var field = fields[index];
            if (field.Name != Service + expectedFields[index]
                || field.Attribute(XmlSchemaInstance + "nil") is not null)
            {
                throw InvalidRequest();
            }

            EnsureNoUnexpectedAttributes(field);
            if (field.Elements().Any())
            {
                throw InvalidRequest();
            }

            EnsureScalarNodes(field);
        }

        return new ValidatedSoapRequest(operation.Name.LocalName);
    }

    private static void ValidateHeaders(XElement header)
    {
        foreach (var block in header.Elements())
        {
            if (block.Attributes().Any(attribute =>
                !attribute.IsNamespaceDeclaration
                && attribute.Name != Soap12 + "mustUnderstand"
                && attribute.Name != Soap12 + "role"))
            {
                throw InvalidRequest();
            }

            var mustUnderstand = block.Attribute(Soap12 + "mustUnderstand");
            var role = (string?)block.Attribute(Soap12 + "role");
            var targetsThisNode = role is null
                || string.Equals(role, UltimateReceiver, StringComparison.Ordinal)
                || string.Equals(role, NextRole, StringComparison.Ordinal);

            if (mustUnderstand is null)
            {
                continue;
            }

            var lexicalValue = mustUnderstand.Value.Trim();
            var isMandatory = lexicalValue switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => throw InvalidRequest(),
            };

            if (targetsThisNode && isMandatory)
            {
                throw new SoapRequestValidationException(
                    StatusCodes.Status500InternalServerError,
                    "SOAP_MUST_UNDERSTAND",
                    "MustUnderstand");
            }
        }
    }

    private static void EnsureDepthAtMost(XElement element, int depth)
    {
        if (depth > 32)
        {
            throw InvalidRequest();
        }

        foreach (var child in element.Elements())
        {
            EnsureDepthAtMost(child, depth + 1);
        }
    }

    private static void EnsureNoUnexpectedAttributes(XElement element)
    {
        if (element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
        {
            throw InvalidRequest();
        }
    }

    private static void EnsureContainerText(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
            {
                throw InvalidRequest();
            }

            if (node is not (XElement or XText or XComment))
            {
                throw InvalidRequest();
            }
        }
    }

    private static void EnsureScalarNodes(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is not (XText or XComment))
            {
                throw InvalidRequest();
            }
        }
    }

    private static bool HasNonUtf8Bom(byte[] bytes) =>
        (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        || (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        || (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        || (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00);

    private static SoapRequestValidationException InvalidRequest() =>
        new(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "Sender");
}
