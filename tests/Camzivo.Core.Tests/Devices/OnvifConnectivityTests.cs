using System.Security.Cryptography;
using System.Text;
using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;
using Camzivo.Core.Security;
using Xunit;

namespace Camzivo.Core.Tests.Devices;

/// <summary>
/// The M3 connection slice, exercised against scripted ONVIF responses. These fixtures mirror what a Dahua/Hikvision
/// camera returns; no hardware is involved, so passing tests prove protocol handling, not device compatibility.
/// </summary>
public sealed class OnvifConnectivityTests
{
    private const string DeviceInformationXml = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
              <tds:Manufacturer>Dahua</tds:Manufacturer>
              <tds:Model>IPC-HDW2431T</tds:Model>
              <tds:FirmwareVersion>2.820.15AB000.0.R</tds:FirmwareVersion>
              <tds:SerialNumber>7A03D1CPAZ1B7E1</tds:SerialNumber>
            </tds:GetDeviceInformationResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string ProfilesXml = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
              <trt:Profiles token="MainToken" fixed="true">
                <tt:Name>mainStream</tt:Name>
                <tt:VideoEncoderConfiguration>
                  <tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
                  <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit></tt:RateControl>
                </tt:VideoEncoderConfiguration>
              </trt:Profiles>
              <trt:Profiles token="SubToken">
                <tt:Name>subStream</tt:Name>
                <tt:VideoEncoderConfiguration>
                  <tt:Encoding>H265</tt:Encoding>
                  <tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution>
                  <tt:RateControl><tt:FrameRateLimit>15</tt:FrameRateLimit></tt:RateControl>
                </tt:VideoEncoderConfiguration>
              </trt:Profiles>
              <trt:Profiles token="AudioToken">
                <tt:Name>audioOnly</tt:Name>
              </trt:Profiles>
            </trt:GetProfilesResponse>
          </s:Body>
        </s:Envelope>
        """;

    private const string StreamUriXml = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <trt:GetStreamUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
              <trt:MediaUri>
                <tt:Uri>rtsp://192.168.1.108:554/cam/realmonitor?channel=1&amp;subtype=0</tt:Uri>
                <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
                <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
                <tt:Timeout>PT0S</tt:Timeout>
              </trt:MediaUri>
            </trt:GetStreamUriResponse>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void Digest_MatchesRfc2617ReferenceVector()
    {
        var challenge = new DigestChallenge(
            "testrealm@host.com",
            "dcd98b7102dd2f0e8b11d0f600bfb0c093",
            "5ccc069c403ebaf9f0171e9517f40e41",
            "auth",
            "MD5",
            Stale: false);

        var header = HttpDigest.BuildAuthorization(
            challenge,
            "GET",
            "/dir/index.html",
            "Mufasa",
            "Circle Of Life",
            "0a4f113b");

        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header, StringComparison.Ordinal);
        Assert.Contains("nc=00000001", header, StringComparison.Ordinal);
        Assert.Contains("qop=auth", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_ParsesRealmContainingComma()
    {
        Assert.True(HttpDigest.TryParse("Digest realm=\"Front, Desk\", nonce=\"abc\", qop=\"auth\"", out var challenge));
        Assert.Equal("Front, Desk", challenge.Realm);
        Assert.Equal("abc", challenge.Nonce);
    }

    [Fact]
    public void Digest_RejectsBasicChallenge()
    {
        Assert.False(HttpDigest.TryParse("Basic realm=\"cam\"", out _));
    }

    [Fact]
    public void UsernameToken_DigestIsSha1OfNonceThenCreatedThenPassword()
    {
        var nonce = Encoding.ASCII.GetBytes("0123456789abcdef");
        var created = DateTimeOffset.Parse("2026-11-22T10:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

        var expected = Convert.ToBase64String(SHA1.HashData(
            [.. nonce, .. Encoding.UTF8.GetBytes("2026-11-22T10:00:00.000Z"), .. Encoding.UTF8.GetBytes("Sup3rSecret!")]));

        Assert.Equal(expected, OnvifUsernameToken.PasswordDigest("Sup3rSecret!", nonce, created));
    }

    [Fact]
    public void UsernameToken_HeaderNeverCarriesThePlaintextPassword()
    {
        var header = OnvifUsernameToken.SecurityHeader(
            "admin",
            "Sup3rSecret!",
            Encoding.ASCII.GetBytes("0123456789abcdef"),
            DateTimeOffset.Parse("2026-11-22T10:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Contains("PasswordDigest", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3rSecret!", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_ProbeCarriesProbeActionAndTransmitterType()
    {
        var message = WsDiscoveryProbe.BuildMessage(Guid.Parse("11111111-2222-3333-4444-555555555555"), DateTimeOffset.UnixEpoch);

        Assert.Contains("uuid:11111111-2222-3333-4444-555555555555", message, StringComparison.Ordinal);
        Assert.Contains("/Probe</w:Action>", message, StringComparison.Ordinal);
        Assert.Contains("dn:NetworkVideoTransmitter", message, StringComparison.Ordinal);
        Assert.Contains("soap.udp://239.255.255.250:3702", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_ParsesMatchesAndCollapsesDuplicates()
    {
        const string datagram = """
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing">
              <e:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <a:EndpointReference><a:Address>urn:uuid:aaaa</a:Address></a:EndpointReference>
                    <d:Types>dn:NetworkVideoTransmitter</d:Types>
                    <d:Scopes>onvif://www.onvif.org/name/Front%20Door onvif://www.onvif.org/hardware/IPC-HDW2431T</d:Scopes>
                    <d:XAddrs>http://192.168.1.108/onvif/device_service</d:XAddrs>
                  </d:ProbeMatch>
                  <d:ProbeMatch>
                    <a:EndpointReference><a:Address>urn:uuid:aaaa</a:Address></a:EndpointReference>
                    <d:XAddrs>http://10.0.0.5/onvif/device_service</d:XAddrs>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </e:Body>
            </e:Envelope>
            """;

        var devices = WsDiscoveryProbe.ParseDatagram(datagram);

        var device = Assert.Single(devices);
        Assert.Equal("Front Door", device.DisplayName);
        Assert.Equal("IPC-HDW2431T", device.Hardware);
        Assert.Equal("192.168.1.108", device.PrimaryAddress!.Host);
    }

    [Fact]
    public void Discovery_MalformedDatagramIsIgnored()
    {
        Assert.Empty(WsDiscoveryProbe.ParseDatagram("<not-xml"));
    }

    [Fact]
    public void Soap_ParsesDeviceInformation()
    {
        var information = OnvifSoap.ParseDeviceInformation(DeviceInformationXml);

        Assert.Equal("Dahua", information.Manufacturer);
        Assert.Equal("IPC-HDW2431T", information.Model);
        Assert.Equal("2.820.15AB000.0.R", information.FirmwareVersion);
    }

    [Fact]
    public void Soap_ParsesProfilesAndSeparatesMainFromSub()
    {
        var profiles = OnvifSoap.ParseProfiles(ProfilesXml);
        var (main, sub) = OnvifSoap.SelectStreams(profiles);

        Assert.Equal(3, profiles.Count);
        Assert.Equal("MainToken", main!.Token);
        Assert.Equal(1920, main.Width);
        Assert.Equal("H264", main.Encoding);
        Assert.Equal("SubToken", sub!.Token);
        Assert.Equal("H265", sub.Encoding);
    }

    [Fact]
    public void Soap_AcceptsARecordersVideoProfileWhenItOmitsTheResolution()
    {
        // Dahua NVRs answer GetProfiles without <Resolution>; the resolution is only in GetVideoEncoderConfigurations.
        // This is what a real recorder of that kind returns, and the token — not the resolution — is what
        // GetStreamUri needs.
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:Profiles token="000" fixed="true">
                    <tt:Name>Channel 1</tt:Name>
                    <tt:VideoSourceConfiguration token="src000"><tt:SourceToken>src000</tt:SourceToken></tt:VideoSourceConfiguration>
                    <tt:VideoEncoderConfiguration token="enc000"><tt:Encoding>H264</tt:Encoding></tt:VideoEncoderConfiguration>
                  </trt:Profiles>
                  <trt:Profiles token="001">
                    <tt:Name>Channel 2</tt:Name>
                    <tt:VideoEncoderConfiguration token="enc001"><tt:Encoding>H264</tt:Encoding></tt:VideoEncoderConfiguration>
                  </trt:Profiles>
                  <trt:Profiles token="audio">
                    <tt:Name>Audio only</tt:Name>
                  </trt:Profiles>
                </trt:GetProfilesResponse>
              </s:Body>
            </s:Envelope>
            """;

        var profiles = OnvifSoap.ParseProfiles(xml);
        var (main, sub) = OnvifSoap.SelectStreams(profiles);

        Assert.Equal("000", main!.Token);
        Assert.Equal("unknown", main.ResolutionText);
        Assert.Equal("001", sub!.Token);
    }

    [Fact]
    public async Task Tester_SaysHowManyProfilesCameBackWhenNoneDescribesVideo()
    {
        const string profilesWithoutVideo = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:Profiles token="M1"><tt:Name>Main</tt:Name></trt:Profiles>
                  <trt:Profiles token="M2"><tt:Name>Sub</tt:Name></trt:Profiles>
                </trt:GetProfilesResponse>
              </s:Body>
            </s:Envelope>
            """;

        var transport = new ScriptedTransport((uri, body, authorization) =>
            body.Contains("GetProfiles", StringComparison.Ordinal)
                ? new OnvifHttpResponse(200, profilesWithoutVideo)
                : HealthyDevice(uri, body, authorization));

        var result = await new ConnectionTester(transport, new ScriptedProbe(null))
            .TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        Assert.False(result.Success);
        var stage = Assert.Single(result.Stages, candidate => candidate.Stage == ConnectionStage.Profiles);
        Assert.Contains("2 returned profile(s)", stage.Detail, StringComparison.Ordinal);
        Assert.Contains("M1", stage.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Soap_ParsesStreamUriAndDecodesEntities()
    {
        var media = OnvifSoap.ParseStreamUri(StreamUriXml);

        Assert.NotNull(media.StreamUri);
        Assert.Equal("rtsp", media.StreamUri!.Scheme);
        Assert.Contains("subtype=0", media.StreamUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Soap_RefusesDtdsInsteadOfResolvingEntities()
    {
        const string hostile = """
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><tt:Manufacturer>&xxe;</tt:Manufacturer></s:Body></s:Envelope>
            """;

        Assert.Throws<InvalidDataException>(() => OnvifSoap.ParseDeviceInformation(hostile));
    }

    [Fact]
    public async Task Tester_CompletesFourStagesForHealthyDevice()
    {
        var transport = new ScriptedTransport(HealthyDevice);
        var tester = new ConnectionTester(transport, new ScriptedProbe(null));

        var result = await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        Assert.True(result.Success);
        Assert.Equal(4, result.Stages.Count);
        Assert.All(result.Stages, stage => Assert.True(stage.Success, stage.Title));
        Assert.Equal("Dahua", result.Information!.Manufacturer);
        Assert.Equal("MainToken", OnvifSoap.SelectStreams(result.Profiles).Main!.Token);
        Assert.Equal("rtsp://192.168.1.108:554/cam/realmonitor?channel=1&subtype=0", result.MainStream!.AbsoluteUri);
        Assert.NotNull(result.SubStream);
    }

    [Fact]
    public async Task Tester_SendsWsSecurityHeaderWithoutPlaintextPassword()
    {
        var transport = new ScriptedTransport(HealthyDevice);
        var tester = new ConnectionTester(transport, new ScriptedProbe(null));

        await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        var first = transport.Calls[0];
        Assert.Contains("UsernameToken", first.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Sup3rSecret!", first.Body, StringComparison.Ordinal);
        Assert.Contains("/onvif/device_service", first.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Null(first.Authorization);
    }

    [Fact]
    public async Task Tester_RetriesWithDigestWhenDeviceChallenges()
    {
        var transport = new ScriptedTransport((uri, body, authorization) =>
            authorization is null
                ? new OnvifHttpResponse(401, string.Empty, "Digest realm=\"ONVIF\", nonce=\"abc123\", qop=\"auth\"")
                : HealthyDevice(uri, body, authorization));

        var tester = new ConnectionTester(transport, new ScriptedProbe(null));
        var result = await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        Assert.True(result.Success);
        Assert.Equal(2, transport.Calls.Count(call => call.Body.Contains("GetDeviceInformation", StringComparison.Ordinal)));
        Assert.StartsWith("Digest ", transport.Calls[1].Authorization, StringComparison.Ordinal);
        Assert.Contains("response=\"", transport.Calls[1].Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tester_ReportsUnauthorizedWhenNoCredentialsWereGiven()
    {
        var transport = new ScriptedTransport((_, _, _) => new OnvifHttpResponse(401, string.Empty, "Digest realm=\"ONVIF\", nonce=\"abc\""));
        var tester = new ConnectionTester(transport, new ScriptedProbe(null));

        var result = await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), null, null);

        Assert.False(result.Success);
        Assert.Equal(StreamFailure.Unauthorized, result.Stages[0].Failure);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task Tester_ReportsTimeoutRatherThanHanging()
    {
        var transport = new ThrowingTransport(new TaskCanceledException("timed out"));
        var tester = new ConnectionTester(transport, new ScriptedProbe(null));

        var result = await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "x");

        Assert.False(result.Success);
        Assert.Equal(StreamFailure.Timeout, result.Stages[0].Failure);
    }

    [Fact]
    public async Task Tester_FailsWhenTheAdvertisedRtspPortIsClosed()
    {
        var transport = new ScriptedTransport(HealthyDevice);
        var tester = new ConnectionTester(transport, new ScriptedProbe(StreamFailure.ConnectionRefused));

        var result = await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        Assert.False(result.Success);
        Assert.Equal(StreamFailure.ConnectionRefused, result.Stages[^1].Failure);
        Assert.Equal(ConnectionStage.Stream, result.Stages[^1].Stage);
    }

    [Fact]
    public async Task Tester_RejectsNonOnvifProfileResponse()
    {
        var transport = new ScriptedTransport((uri, body, authorization) =>
            body.Contains("GetProfiles", StringComparison.Ordinal)
                ? new OnvifHttpResponse(200, "<html><body>login</body></html>")
                : HealthyDevice(uri, body, authorization));

        var tester = new ConnectionTester(transport, new ScriptedProbe(null));
        var result = await tester.TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        Assert.False(result.Success);
        Assert.Equal(StreamFailure.ProtocolViolation, result.Stages[^1].Failure);
    }

    [Fact]
    public async Task Tester_UsesTheAdvertisedMediaServiceForProfilesAndStreams()
    {
        var transport = new ScriptedTransport((uri, body, authorization) =>
        {
            if (body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return new OnvifHttpResponse(200, """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                        <tds:Capabilities>
                          <tt:Media><tt:XAddr>http://192.168.1.108/onvif/media_service</tt:XAddr></tt:Media>
                        </tds:Capabilities>
                      </tds:GetCapabilitiesResponse>
                    </s:Body></s:Envelope>
                    """);
            }

            if (body.Contains("GetProfiles", StringComparison.Ordinal) || body.Contains("GetStreamUri", StringComparison.Ordinal))
            {
                Assert.Equal("/onvif/media_service", uri.AbsolutePath);
            }

            return HealthyDevice(uri, body, authorization);
        });

        var result = await new ConnectionTester(transport, new ScriptedProbe(null))
            .TestAsync(new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "Sup3rSecret!");

        Assert.True(result.Success, result.Failure?.Explanation);
        Assert.Equal(new Uri("http://192.168.1.108/onvif/media_service"), result.Services!.Media);
    }

    [Fact]
    public void Soap_ParsesDeviceTimeAndAudioCapabilityWithoutGuessing()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
              <tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                <tds:SystemDateAndTime>
                  <tt:UTCDateTime xmlns:tt="http://www.onvif.org/ver10/schema">
                    <tt:Date><tt:Year>2026</tt:Year><tt:Month>11</tt:Month><tt:Day>22</tt:Day></tt:Date>
                    <tt:Time><tt:Hour>14</tt:Hour><tt:Minute>5</tt:Minute><tt:Second>9</tt:Second></tt:Time>
                  </tt:UTCDateTime>
                  <tt:LocalDateTime xmlns:tt="http://www.onvif.org/ver10/schema">
                    <tt:Date><tt:Year>2026</tt:Year><tt:Month>11</tt:Month><tt:Day>22</tt:Day></tt:Date>
                    <tt:Time><tt:Hour>22</tt:Hour><tt:Minute>5</tt:Minute><tt:Second>9</tt:Second></tt:Time>
                  </tt:LocalDateTime>
                  <tt:TimeZone xmlns:tt="http://www.onvif.org/ver10/schema"><tt:TZ>UTC+08:00</tt:TZ></tt:TimeZone>
                </tds:SystemDateAndTime>
              </tds:GetSystemDateAndTimeResponse>
            </s:Body></s:Envelope>
            """;

        var time = OnvifSoap.ParseSystemDateAndTime(xml);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T14:05:09Z"), time.Utc);
        Assert.Equal("2026-11-22T22:05:09", time.Local);
        Assert.Equal("UTC+08:00", time.TimeZone);

        var profile = OnvifSoap.ParseProfiles("""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
              <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                <trt:Profiles token="p1"><tt:Name>Channel 1</tt:Name><tt:VideoEncoderConfiguration>
                  <tt:Encoding>H264</tt:Encoding><tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
                </tt:VideoEncoderConfiguration><tt:AudioEncoderConfiguration><tt:Encoding>AAC</tt:Encoding></tt:AudioEncoderConfiguration>
                </trt:Profiles>
              </trt:GetProfilesResponse>
            </s:Body></s:Envelope>
            """);

        Assert.True(Assert.Single(profile).HasAudio);
    }

    private static OnvifHttpResponse HealthyDevice(Uri uri, string body, string? authorization)
    {
        _ = uri;
        _ = authorization;

        if (body.Contains("GetDeviceInformation", StringComparison.Ordinal))
        {
            return new OnvifHttpResponse(200, DeviceInformationXml);
        }

        if (body.Contains("GetProfiles", StringComparison.Ordinal))
        {
            return new OnvifHttpResponse(200, ProfilesXml);
        }

        if (body.Contains("GetStreamUri", StringComparison.Ordinal))
        {
            return new OnvifHttpResponse(200, StreamUriXml);
        }

        return new OnvifHttpResponse(500, string.Empty);
    }

    private sealed class ScriptedTransport(Func<Uri, string, string?, OnvifHttpResponse> handler) : IOnvifTransport
    {
        public List<(Uri Uri, string Body, string? Authorization)> Calls { get; } = [];

        public Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken)
        {
            Calls.Add((uri, body, authorization));
            return Task.FromResult(handler(uri, body, authorization));
        }
    }

    private sealed class ThrowingTransport(Exception exception) : IOnvifTransport
    {
        public Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken) =>
            Task.FromException<OnvifHttpResponse>(exception);
    }

    private sealed class ScriptedProbe(StreamFailure? failure) : IRtspEndpointProbe
    {
        public Task<StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken)
        {
            Assert.Equal("rtsp", uri.Scheme);
            return Task.FromResult(failure);
        }
    }
}
