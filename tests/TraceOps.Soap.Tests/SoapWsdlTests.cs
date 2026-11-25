using System.Net;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;
using Xunit;

namespace TraceOps.Soap.Tests;

public sealed class SoapWsdlTests
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    [Fact]
    [Trait("Acceptance", "M5-T02")]
    public async Task Served_WSDL_inline_XSD_matches_and_validates_actual_requests_responses_and_fault()
    {
        using var factory = SoapAuthorAFactory.Create(enabled: true, port: 5081, seed: false);
        using var client = factory.NewClient();
        using var wsdlRequest = new HttpRequestMessage(HttpMethod.Get, "/soap/shipments?wsdl");
        wsdlRequest.Headers.Host = "attacker.example";
        using var wsdlResponse = await client.SendAsync(wsdlRequest);
        Assert.Equal(HttpStatusCode.OK, wsdlResponse.StatusCode);
        Assert.Equal("text/xml", wsdlResponse.Content.Headers.ContentType?.MediaType);

        var operationNames = new[] { "RegisterShipment", "GetShipment", "AddShipmentEvent" };
        var wsdl = XDocument.Parse(await wsdlResponse.Content.ReadAsStringAsync());
        Assert.Equal(Wsdl + "definitions", wsdl.Root!.Name);
        Assert.Equal(SoapAuthorAHarness.ServiceNamespace, (string?)wsdl.Root.Attribute("targetNamespace"));

        var service = Assert.Single(wsdl.Descendants(Wsdl + "service"));
        Assert.Equal("TraceOpsShipmentService", (string?)service.Attribute("name"));
        var port = Assert.Single(service.Elements(Wsdl + "port"));
        Assert.Equal("TraceOpsShipmentSoap12Port", (string?)port.Attribute("name"));
        Assert.Equal(SoapAuthorAHarness.ServiceNs + "TraceOpsShipmentSoap12Binding", ResolveQName(port, "binding"));
        var address = Assert.Single(port.Elements(Soap12 + "address"));
        Assert.Equal("http://127.0.0.1:5081/soap/shipments", (string?)address.Attribute("location"));

        var binding = Assert.Single(wsdl.Descendants(Wsdl + "binding"));
        Assert.Equal("TraceOpsShipmentSoap12Binding", (string?)binding.Attribute("name"));
        Assert.Equal(SoapAuthorAHarness.ServiceNs + "ITraceOpsSoapService", ResolveQName(binding, "type"));
        var soapBinding = Assert.Single(binding.Elements(Soap12 + "binding"));
        Assert.Equal("document", (string?)soapBinding.Attribute("style"));
        var soapBodies = binding.Descendants(Soap12 + "body").ToArray();
        Assert.Equal(operationNames.Length * 2, soapBodies.Length);
        Assert.All(soapBodies, body => Assert.Equal("literal", (string?)body.Attribute("use")));

        var portType = Assert.Single(wsdl.Descendants(Wsdl + "portType"));
        Assert.Equal("ITraceOpsSoapService", (string?)portType.Attribute("name"));
        var abstractOperations = portType.Elements(Wsdl + "operation").ToArray();
        var bindingOperations = binding.Elements(Wsdl + "operation").ToArray();
        Assert.Equal(3, abstractOperations.Length);
        Assert.Equal(3, bindingOperations.Length);
        var expectedOperations = operationNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedOperations, abstractOperations.Select(operation => (string?)operation.Attribute("name"))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedOperations, bindingOperations.Select(operation => (string?)operation.Attribute("name"))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        foreach (var operation in operationNames)
        {
            var action = $"{SoapAuthorAHarness.ServiceNamespace}/ITraceOpsSoapService/{operation}";
            var operationBinding = Assert.Single(bindingOperations, item => (string?)item.Attribute("name") == operation);
            Assert.Equal(action, (string?)operationBinding.Element(Soap12 + "operation")?.Attribute("soapAction"));
            var abstractOperation = Assert.Single(portType.Elements(Wsdl + "operation"),
                item => (string?)item.Attribute("name") == operation);
            var abstractFault = Assert.Single(abstractOperation.Elements(Wsdl + "fault"));
            var faultName = (string?)abstractFault.Attribute("name");
            Assert.False(string.IsNullOrWhiteSpace(faultName));
            var messageName = ResolveQName(abstractFault, "message");
            var targetNamespace = (string?)wsdl.Root.Attribute("targetNamespace");
            Assert.False(string.IsNullOrWhiteSpace(targetNamespace));
            var message = Assert.Single(wsdl.Root.Elements(Wsdl + "message"), item =>
                XName.Get((string)item.Attribute("name")!, targetNamespace!) == messageName);
            var part = Assert.Single(message.Elements(Wsdl + "part"));
            Assert.Equal(SoapAuthorAHarness.ServiceNs + "TraceOpsFault", ResolveQName(part, "element"));

            var bindingFault = Assert.Single(operationBinding.Elements(Wsdl + "fault"));
            Assert.Equal(faultName, (string?)bindingFault.Attribute("name"));
            var soapFault = Assert.Single(bindingFault.Elements(Soap12 + "fault"));
            Assert.Equal(faultName, (string?)soapFault.Attribute("name"));
            Assert.Equal("literal", (string?)soapFault.Attribute("use"));
        }

        Assert.Empty(wsdl.Descendants(Wsdl + "import"));
        Assert.Empty(wsdl.Descendants(Xsd + "import"));
        Assert.Empty(wsdl.Descendants(Xsd + "include"));
        Assert.Empty(wsdl.Descendants(Xsd + "redefine"));

        var schemaElements = wsdl.Descendants(Xsd + "schema").ToArray();
        Assert.NotEmpty(schemaElements);
        Assert.Contains(schemaElements, schema =>
            (string?)schema.Attribute("targetNamespace") == SoapAuthorAHarness.ServiceNamespace);
        var schemaSet = CompileSchemas(schemaElements);
        Assert.True(schemaSet.GlobalElements.Contains(new XmlQualifiedName("RegisterShipment", SoapAuthorAHarness.ServiceNamespace)));
        Assert.True(schemaSet.GlobalElements.Contains(new XmlQualifiedName("GetShipment", SoapAuthorAHarness.ServiceNamespace)));
        Assert.True(schemaSet.GlobalElements.Contains(new XmlQualifiedName("AddShipmentEvent", SoapAuthorAHarness.ServiceNamespace)));
        Assert.True(schemaSet.GlobalElements.Contains(new XmlQualifiedName("TraceOpsFault", SoapAuthorAHarness.ServiceNamespace)));

        foreach (var schema in schemaElements)
        {
            Assert.Equal(SoapAuthorAHarness.ServiceNamespace, (string?)schema.Attribute("targetNamespace"));
            Assert.Equal("qualified", (string?)schema.Attribute("elementFormDefault"));
            foreach (var element in schema.Descendants(Xsd + "element"))
            {
                Assert.Equal("1", (string?)element.Attribute("minOccurs") ?? "1");
                Assert.Equal("1", (string?)element.Attribute("maxOccurs") ?? "1");
                Assert.Equal("false", (string?)element.Attribute("nillable") ?? "false");
            }
        }

        var summaryFields = new[] { "shipment/trackingNumber", "shipment/status", "shipment/lastOccurredAt", "shipment/version" };
        AssertWireTypes(schemaSet, "RegisterShipment", ["request/trackingNumber"]);
        AssertWireTypes(schemaSet, "GetShipment", ["request/trackingNumber"]);
        AssertWireTypes(schemaSet, "AddShipmentEvent",
            ["request/trackingNumber", "request/eventId", "request/status", "request/occurredAt", "request/sourceSystem"]);
        foreach (var operation in new[] { "RegisterShipment", "GetShipment" })
            AssertWireTypes(schemaSet, operation + "Response",
                new[] { "code" }.Concat(summaryFields).Select(path => operation + "Result/" + path).ToArray());
        AssertWireTypes(schemaSet, "AddShipmentEventResponse",
            new[] { "code", "eventId", "duplicate" }.Concat(summaryFields)
                .Select(path => "AddShipmentEventResult/" + path).ToArray());
        AssertWireTypes(schemaSet, "TraceOpsFault", ["code", "correlationId"]);

        var registerXml = SoapAuthorAHarness.RegisterRequest("SOAP-WSDL-001");
        ValidateElement(Payload(XDocument.Parse(registerXml)), schemaSet);
        using (var register = await SoapAuthorAHarness.PostAsync(client, "RegisterShipment", registerXml))
        {
            var response = await SoapAuthorAHarness.ReadSuccessAsync(register, "RegisterShipment", "REGISTERED");
            ValidateElement(SoapAuthorAHarness.Payload(response), schemaSet);
        }

        var getXml = SoapAuthorAHarness.GetRequest("SOAP-WSDL-001");
        ValidateElement(Payload(XDocument.Parse(getXml)), schemaSet);
        using (var get = await SoapAuthorAHarness.PostAsync(client, "GetShipment", getXml))
        {
            var response = await SoapAuthorAHarness.ReadSuccessAsync(get, "GetShipment", "FOUND");
            ValidateElement(SoapAuthorAHarness.Payload(response), schemaSet);
        }

        var addXml = SoapAuthorAHarness.AddEventRequest(
            "SOAP-WSDL-001", "cccccccc-0000-0000-0000-000000000001", "Accepted", "2026-11-17T10:00:01Z", "wsdl.test");
        ValidateElement(Payload(XDocument.Parse(addXml)), schemaSet);
        using (var add = await SoapAuthorAHarness.PostAsync(client, "AddShipmentEvent", addXml))
        {
            var response = await SoapAuthorAHarness.ReadSuccessAsync(add, "AddShipmentEvent", "APPLIED");
            ValidateElement(SoapAuthorAHarness.Payload(response), schemaSet);
        }

        using (var invalid = await SoapAuthorAHarness.PostAsync(client, "RegisterShipment", SoapAuthorAHarness.RegisterRequest("bad tracking")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var fault = XDocument.Parse(await invalid.Content.ReadAsStringAsync());
            var detail = fault.Descendants(SoapAuthorAHarness.Soap12Ns + "Detail").Single()
                .Elements(SoapAuthorAHarness.ServiceNs + "TraceOpsFault").Single();
            ValidateElement(detail, schemaSet);
        }
    }

    private static XName ResolveQName(XElement context, string attributeName)
    {
        var value = (string?)context.Attribute(attributeName);
        Assert.False(string.IsNullOrWhiteSpace(value));
        var parts = value!.Trim().Split(':');
        Assert.InRange(parts.Length, 1, 2);
        foreach (var part in parts)
            XmlConvert.VerifyNCName(part);
        var ns = parts.Length == 1 ? context.GetDefaultNamespace() : context.GetNamespaceOfPrefix(parts[0]);
        Assert.NotNull(ns);
        return ns! + parts[^1];
    }

    private static void AssertWireTypes(XmlSchemaSet schemas, string wrapper, string[] expectedPaths)
    {
        var root = Assert.IsType<XmlSchemaElement>(schemas.GlobalElements[
            new XmlQualifiedName(wrapper, SoapAuthorAHarness.ServiceNamespace)]);
        var leaves = new List<(string Path, XmlQualifiedName Type)>();
        Visit(root, "");
        Assert.Equal(expectedPaths, leaves.Select(leaf => leaf.Path));
        foreach (var (path, type) in leaves)
        {
            var expectedType = path.Split('/')[^1] switch
            {
                "version" => "long",
                "duplicate" => "boolean",
                _ => "string",
            };
            Assert.Equal(new XmlQualifiedName(expectedType, Xsd.NamespaceName), type);
        }

        void Visit(XmlSchemaElement element, string path)
        {
            Assert.Equal(SoapAuthorAHarness.ServiceNamespace, element.QualifiedName.Namespace);
            if (element.ElementSchemaType is XmlSchemaComplexType complex)
            {
                var sequence = Assert.IsType<XmlSchemaSequence>(complex.ContentTypeParticle);
                foreach (var item in sequence.Items)
                {
                    var child = Assert.IsType<XmlSchemaElement>(item);
                    Visit(child, path.Length == 0 ? child.QualifiedName.Name : path + "/" + child.QualifiedName.Name);
                }
            }
            else
            {
                Assert.NotNull(element.ElementSchemaType);
                leaves.Add((path, element.ElementSchemaType!.QualifiedName));
            }
        }
    }

    private static XmlSchemaSet CompileSchemas(IEnumerable<XElement> schemaElements)
    {
        var errors = new List<string>();
        var schemas = new XmlSchemaSet { XmlResolver = null };
        foreach (var element in schemaElements)
        {
            using var reader = element.CreateReader();
            var schema = XmlSchema.Read(reader, (_, args) => errors.Add(args.Message));
            Assert.NotNull(schema);
            schemas.Add(schema!);
        }
        schemas.Compile();
        Assert.Empty(errors);
        return schemas;
    }

    private static XElement Payload(XDocument document) =>
        document.Root!.Element(SoapAuthorAHarness.Soap12Ns + "Body")!.Elements().Single();

    private static void ValidateElement(XElement element, XmlSchemaSet schemas)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(element.ToString(SaveOptions.DisableFormatting));
        document.Schemas = schemas;
        var errors = new List<string>();
        document.Validate((_, args) => errors.Add(args.Message));
        Assert.Empty(errors);
    }
}
