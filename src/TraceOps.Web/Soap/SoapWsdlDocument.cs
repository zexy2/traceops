using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using TraceOps.Web.Middleware;

namespace TraceOps.Web.Soap;

internal static class SoapWsdlDocument
{
    private const string Template = """
        <?xml version="1.0" encoding="utf-8"?>
        <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/"
                          xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
                          xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                          xmlns:tns="urn:traceops:soap:v1"
                          name="TraceOpsShipmentService"
                          targetNamespace="urn:traceops:soap:v1">
          <wsdl:types>
            <xsd:schema targetNamespace="urn:traceops:soap:v1" elementFormDefault="qualified">
              <xsd:complexType name="SoapRegisterShipmentRequest">
                <xsd:sequence><xsd:element name="trackingNumber" type="xsd:string" /></xsd:sequence>
              </xsd:complexType>
              <xsd:complexType name="SoapGetShipmentRequest">
                <xsd:sequence><xsd:element name="trackingNumber" type="xsd:string" /></xsd:sequence>
              </xsd:complexType>
              <xsd:complexType name="SoapAddShipmentEventRequest">
                <xsd:sequence>
                  <xsd:element name="trackingNumber" type="xsd:string" />
                  <xsd:element name="eventId" type="xsd:string" />
                  <xsd:element name="status" type="xsd:string" />
                  <xsd:element name="occurredAt" type="xsd:string" />
                  <xsd:element name="sourceSystem" type="xsd:string" />
                </xsd:sequence>
              </xsd:complexType>
              <xsd:complexType name="SoapShipmentSummary">
                <xsd:sequence>
                  <xsd:element name="trackingNumber" type="xsd:string" />
                  <xsd:element name="status" type="xsd:string" />
                  <xsd:element name="lastOccurredAt" type="xsd:string" />
                  <xsd:element name="version" type="xsd:long" />
                </xsd:sequence>
              </xsd:complexType>
              <xsd:complexType name="SoapFaultDetail">
                <xsd:sequence>
                  <xsd:element name="code" type="xsd:string" />
                  <xsd:element name="correlationId" type="xsd:string" />
                </xsd:sequence>
              </xsd:complexType>
              <xsd:element name="TraceOpsFault" type="tns:SoapFaultDetail" />

              <xsd:element name="RegisterShipment">
                <xsd:complexType><xsd:sequence>
                  <xsd:element name="request" type="tns:SoapRegisterShipmentRequest" />
                </xsd:sequence></xsd:complexType>
              </xsd:element>
              <xsd:element name="GetShipment">
                <xsd:complexType><xsd:sequence>
                  <xsd:element name="request" type="tns:SoapGetShipmentRequest" />
                </xsd:sequence></xsd:complexType>
              </xsd:element>
              <xsd:element name="AddShipmentEvent">
                <xsd:complexType><xsd:sequence>
                  <xsd:element name="request" type="tns:SoapAddShipmentEventRequest" />
                </xsd:sequence></xsd:complexType>
              </xsd:element>

              <xsd:element name="RegisterShipmentResponse">
                <xsd:complexType><xsd:sequence><xsd:element name="RegisterShipmentResult">
                  <xsd:complexType><xsd:sequence>
                    <xsd:element name="code" type="xsd:string" />
                    <xsd:element name="shipment" type="tns:SoapShipmentSummary" />
                  </xsd:sequence></xsd:complexType>
                </xsd:element></xsd:sequence></xsd:complexType>
              </xsd:element>
              <xsd:element name="GetShipmentResponse">
                <xsd:complexType><xsd:sequence><xsd:element name="GetShipmentResult">
                  <xsd:complexType><xsd:sequence>
                    <xsd:element name="code" type="xsd:string" />
                    <xsd:element name="shipment" type="tns:SoapShipmentSummary" />
                  </xsd:sequence></xsd:complexType>
                </xsd:element></xsd:sequence></xsd:complexType>
              </xsd:element>
              <xsd:element name="AddShipmentEventResponse">
                <xsd:complexType><xsd:sequence><xsd:element name="AddShipmentEventResult">
                  <xsd:complexType><xsd:sequence>
                    <xsd:element name="code" type="xsd:string" />
                    <xsd:element name="eventId" type="xsd:string" />
                    <xsd:element name="duplicate" type="xsd:boolean" />
                    <xsd:element name="shipment" type="tns:SoapShipmentSummary" />
                  </xsd:sequence></xsd:complexType>
                </xsd:element></xsd:sequence></xsd:complexType>
              </xsd:element>
            </xsd:schema>
          </wsdl:types>

          <wsdl:message name="RegisterShipmentInput"><wsdl:part name="parameters" element="tns:RegisterShipment" /></wsdl:message>
          <wsdl:message name="RegisterShipmentOutput"><wsdl:part name="parameters" element="tns:RegisterShipmentResponse" /></wsdl:message>
          <wsdl:message name="GetShipmentInput"><wsdl:part name="parameters" element="tns:GetShipment" /></wsdl:message>
          <wsdl:message name="GetShipmentOutput"><wsdl:part name="parameters" element="tns:GetShipmentResponse" /></wsdl:message>
          <wsdl:message name="AddShipmentEventInput"><wsdl:part name="parameters" element="tns:AddShipmentEvent" /></wsdl:message>
          <wsdl:message name="AddShipmentEventOutput"><wsdl:part name="parameters" element="tns:AddShipmentEventResponse" /></wsdl:message>
          <wsdl:message name="TraceOpsFaultMessage"><wsdl:part name="detail" element="tns:TraceOpsFault" /></wsdl:message>

          <wsdl:portType name="ITraceOpsSoapService">
            <wsdl:operation name="RegisterShipment">
              <wsdl:input message="tns:RegisterShipmentInput" /><wsdl:output message="tns:RegisterShipmentOutput" />
              <wsdl:fault name="TraceOpsFault" message="tns:TraceOpsFaultMessage" />
            </wsdl:operation>
            <wsdl:operation name="GetShipment">
              <wsdl:input message="tns:GetShipmentInput" /><wsdl:output message="tns:GetShipmentOutput" />
              <wsdl:fault name="TraceOpsFault" message="tns:TraceOpsFaultMessage" />
            </wsdl:operation>
            <wsdl:operation name="AddShipmentEvent">
              <wsdl:input message="tns:AddShipmentEventInput" /><wsdl:output message="tns:AddShipmentEventOutput" />
              <wsdl:fault name="TraceOpsFault" message="tns:TraceOpsFaultMessage" />
            </wsdl:operation>
          </wsdl:portType>

          <wsdl:binding name="TraceOpsShipmentSoap12Binding" type="tns:ITraceOpsSoapService">
            <soap12:binding style="document" transport="http://schemas.xmlsoap.org/soap/http" />
            <wsdl:operation name="RegisterShipment">
              <soap12:operation soapAction="urn:traceops:soap:v1/ITraceOpsSoapService/RegisterShipment" />
              <wsdl:input><soap12:body use="literal" /></wsdl:input><wsdl:output><soap12:body use="literal" /></wsdl:output>
              <wsdl:fault name="TraceOpsFault"><soap12:fault name="TraceOpsFault" use="literal" /></wsdl:fault>
            </wsdl:operation>
            <wsdl:operation name="GetShipment">
              <soap12:operation soapAction="urn:traceops:soap:v1/ITraceOpsSoapService/GetShipment" />
              <wsdl:input><soap12:body use="literal" /></wsdl:input><wsdl:output><soap12:body use="literal" /></wsdl:output>
              <wsdl:fault name="TraceOpsFault"><soap12:fault name="TraceOpsFault" use="literal" /></wsdl:fault>
            </wsdl:operation>
            <wsdl:operation name="AddShipmentEvent">
              <soap12:operation soapAction="urn:traceops:soap:v1/ITraceOpsSoapService/AddShipmentEvent" />
              <wsdl:input><soap12:body use="literal" /></wsdl:input><wsdl:output><soap12:body use="literal" /></wsdl:output>
              <wsdl:fault name="TraceOpsFault"><soap12:fault name="TraceOpsFault" use="literal" /></wsdl:fault>
            </wsdl:operation>
          </wsdl:binding>

          <wsdl:service name="TraceOpsShipmentService">
            <wsdl:port name="TraceOpsShipmentSoap12Port" binding="tns:TraceOpsShipmentSoap12Binding">
              <soap12:address location="http://127.0.0.1:__PORT__/soap/shipments" />
            </wsdl:port>
          </wsdl:service>
        </wsdl:definitions>
        """;

    public static async Task WriteAsync(HttpContext context, int port)
    {
        var xml = Template.Replace(
            "__PORT__",
            port.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/xml; charset=utf-8";
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] =
            CorrelationIdMiddleware.GetCorrelationId(context);
        await context.Response.WriteAsync(xml, Encoding.UTF8, context.RequestAborted);
    }
}
