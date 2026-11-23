using System.Net;
using Camzivo.Core.Devices;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;
using Camzivo.Data.Net;
using Xunit;

namespace Camzivo.Data.Tests.Devices;

public sealed class ReadOnlyNvrTransportTests
{
    private const string GetProfiles = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetProfiles xmlns:trt="http://www.onvif.org/ver10/media/wsdl" />
        </s:Body></s:Envelope>
        """;

    private static NvrAccessPolicy Policy() =>
        NvrAccessPolicy.ForEndpoint(new DeviceEndpoint("192.168.1.108", 80, 554, false));

    [Fact]
    public async Task Approved_read_operation_at_the_exact_endpoint_is_forwarded()
    {
        var inner = new RecordingTransport();
        var guarded = new ReadOnlyNvrTransport(inner, Policy());

        await guarded.PostAsync(new Uri("http://192.168.1.108:80/onvif/media"), GetProfiles, null, CancellationToken.None);

        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Different_host_is_rejected_before_any_network_call()
    {
        var inner = new RecordingTransport();
        var guarded = new ReadOnlyNvrTransport(inner, Policy());

        await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.PostAsync(new Uri("http://10.0.0.9:80/onvif/media"), GetProfiles, null, CancellationToken.None));

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Different_port_and_scheme_are_rejected()
    {
        var inner = new RecordingTransport();
        var guarded = new ReadOnlyNvrTransport(inner, Policy());

        await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.PostAsync(new Uri("http://192.168.1.108:8080/onvif/media"), GetProfiles, null, CancellationToken.None));
        await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.PostAsync(new Uri("https://192.168.1.108:80/onvif/media"), GetProfiles, null, CancellationToken.None));

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Configuration_write_is_rejected_at_the_protocol_layer()
    {
        var inner = new RecordingTransport();
        var guarded = new ReadOnlyNvrTransport(inner, Policy());
        var body = GetProfiles.Replace("GetProfiles", "SetHostname", StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.PostAsync(new Uri("http://192.168.1.108:80/onvif/device_service"), body, null, CancellationToken.None));

        Assert.Contains("SetHostname", error.Message, StringComparison.Ordinal);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Unknown_operation_is_denied_by_default()
    {
        var inner = new RecordingTransport();
        var guarded = new ReadOnlyNvrTransport(inner, Policy());
        var body = GetProfiles.Replace("GetProfiles", "FutureWriteFeature", StringComparison.Ordinal);

        await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.PostAsync(new Uri("http://192.168.1.108:80/onvif/device_service"), body, null, CancellationToken.None));

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Http_redirects_are_returned_and_never_followed()
    {
        var handler = new RedirectHandler();
        using var client = new HttpClient(handler);
        using var transport = new HttpOnvifTransport(client);

        var response = await transport.PostAsync(
            new Uri("http://192.168.1.108:80/onvif/device_service"), GetProfiles, null, CancellationToken.None);

        Assert.Equal(302, response.StatusCode);
        Assert.Single(handler.Targets);
        Assert.Equal("http://192.168.1.108/onvif/device_service", handler.Targets[0].ToString());
    }

    [Fact]
    public async Task Rtsp_probe_never_opens_a_camera_address()
    {
        var inner = new RecordingProbe();
        var guarded = new ReadOnlyNvrRtspProbe(inner, Policy());

        await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.ProbeAsync(new Uri("rtsp://10.0.0.7:554/live"), CancellationToken.None));
        await Assert.ThrowsAsync<NvrAccessDeniedException>(() =>
            guarded.ProbeAsync(new Uri("rtsp://192.168.1.108:8554/live"), CancellationToken.None));

        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Rtsp_probe_allows_the_recorder_endpoint()
    {
        var inner = new RecordingProbe();
        var guarded = new ReadOnlyNvrRtspProbe(inner, Policy());

        await guarded.ProbeAsync(new Uri("rtsp://192.168.1.108:554/live"), CancellationToken.None);

        Assert.Single(inner.Calls);
    }

    private sealed class RecordingTransport : IOnvifTransport
    {
        public List<Uri> Calls { get; } = [];

        public Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken)
        {
            Calls.Add(uri);
            return Task.FromResult(new OnvifHttpResponse(200, "<ok/>"));
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public List<Uri> Targets { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Targets.Add(request.RequestUri!);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found)
            {
                RequestMessage = request,
            };
            redirect.Headers.Location = new Uri("http://10.0.0.9/onvif/device_service");
            return Task.FromResult(redirect);
        }
    }

    private sealed class RecordingProbe : IRtspEndpointProbe
    {
        public List<Uri> Calls { get; } = [];

        public Task<StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken)
        {
            Calls.Add(uri);
            return Task.FromResult<StreamFailure?>(null);
        }
    }
}
