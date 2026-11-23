using Camzivo.Core.Devices;
using Xunit;

namespace Camzivo.Core.Tests.Devices;

public sealed class NvrSafetyTests
{
    private const string Envelope = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetProfiles xmlns:tds="http://www.onvif.org/ver10/media/wsdl" />
        </s:Body></s:Envelope>
        """;

    [Theory]
    [InlineData("GetDeviceInformation")]
    [InlineData("GetProfiles")]
    [InlineData("GetStreamUri")]
    [InlineData("GetRecordings")]
    [InlineData("FindRecordings")]
    [InlineData("GetRecordingSearchResults")]
    [InlineData("EndSearch")]
    public void Read_only_operations_are_allowlisted(string operation)
    {
        var body = Envelope.Replace("<tds:GetProfiles", $"<tds:{operation}", StringComparison.Ordinal);

        Assert.Null(NvrReadOnlyOperations.Validate(body, out var found));
        Assert.Equal(operation, found);
    }

    [Theory]
    [InlineData("SetHostname")]
    [InlineData("SetNetworkInterfaces")]
    [InlineData("CreateUsers")]
    [InlineData("DeleteUsers")]
    [InlineData("ContinuousMove")]
    [InlineData("GotoPreset")]
    [InlineData("SetPreset")]
    [InlineData("Stop")]
    [InlineData("StartRecording")]
    [InlineData("SetVideoEncoderConfiguration")]
    public void State_changing_and_unknown_operations_are_denied(string operation)
    {
        var body = Envelope.Replace("<tds:GetProfiles", $"<tds:{operation}", StringComparison.Ordinal);

        Assert.NotNull(NvrReadOnlyOperations.Validate(body, out var found));
        Assert.Equal(operation, found);
    }

    [Fact]
    public void Malformed_or_missing_soap_is_denied()
    {
        Assert.NotNull(NvrReadOnlyOperations.Validate("<not-soap", out var broken));
        Assert.Equal("unreadable request", broken);
        Assert.NotNull(NvrReadOnlyOperations.Validate(string.Empty, out _));
    }

    [Fact]
    public void Policy_allows_only_the_exact_recorder_host_and_ports()
    {
        var policy = NvrAccessPolicy.ForEndpoint(new DeviceEndpoint("192.168.1.108", 80, 554, false));

        Assert.True(policy.AllowsOnvifUri(new Uri("http://192.168.1.108:80/onvif/media")));
        Assert.True(policy.AllowsStreamUri(new Uri("rtsp://192.168.1.108:554/cam/realmonitor?channel=1")));

        Assert.False(policy.AllowsOnvifUri(new Uri("http://192.168.1.109:80/onvif/media")));
        Assert.False(policy.AllowsOnvifUri(new Uri("https://192.168.1.108:80/onvif/media")));
        Assert.False(policy.AllowsOnvifUri(new Uri("http://192.168.1.108:8080/onvif/media")));
        Assert.False(policy.AllowsStreamUri(new Uri("rtsp://10.0.0.7:554/live")));
        Assert.False(policy.AllowsStreamUri(new Uri("rtsp://192.168.1.108:8554/live")));
        Assert.False(policy.AllowsStreamUri(new Uri("rtsp://admin:password@192.168.1.108:554/live")));
        Assert.False(policy.AllowsOnvifUri(new Uri("http://admin:password@192.168.1.108:80/onvif/media")));
    }

    [Fact]
    public void Policy_rejects_ip_hostname_substitution()
    {
        var policy = NvrAccessPolicy.ForEndpoint(new DeviceEndpoint("nvr.example", 80, 554, false));

        Assert.False(policy.AllowsOnvifUri(new Uri("http://10.0.0.8:80/onvif/device_service")));
    }

    [Fact]
    public void Hardware_gate_is_blocked_by_default()
    {
        var decision = NvrHardwareAccessGate.Evaluate("192.168.1.108", 80, 554, authorized: false, confirmedHost: null);

        Assert.False(decision.Allowed);
        Assert.Contains("not explicitly authorized", decision.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hardware_gate_requires_a_separate_confirmation_of_the_exact_host()
    {
        var wrong = NvrHardwareAccessGate.Evaluate("192.168.1.108", 80, 554, authorized: true, confirmedHost: "192.168.1.109");
        var missing = NvrHardwareAccessGate.Evaluate("192.168.1.108", 80, 554, authorized: true, confirmedHost: null);
        var accepted = NvrHardwareAccessGate.Evaluate("192.168.1.108", 80, 554, authorized: true, confirmedHost: "192.168.1.108");

        Assert.False(wrong.Allowed);
        Assert.False(missing.Allowed);
        Assert.True(accepted.Allowed);
    }

    [Fact]
    public void Hardware_gate_rejects_invalid_ports_and_missing_host()
    {
        Assert.False(NvrHardwareAccessGate.Evaluate(null, 80, 554, true, null).Allowed);
        Assert.False(NvrHardwareAccessGate.Evaluate("nvr", 0, 554, true, "nvr").Allowed);
        Assert.False(NvrHardwareAccessGate.Evaluate("nvr", 80, 70000, true, "nvr").Allowed);
    }

    [Theory]
    [InlineData("GetDeviceInformation")]
    [InlineData("GetCapabilities")]
    [InlineData("GetServices")]
    [InlineData("GetServiceCapabilities")]
    [InlineData("GetSystemDateAndTime")]
    [InlineData("GetProfiles")]
    [InlineData("GetVideoSources")]
    [InlineData("GetVideoSourceConfigurations")]
    [InlineData("GetVideoEncoderConfigurations")]
    public void Metadata_allowlist_permits_only_device_and_media_metadata(string operation)
    {
        var body = Envelope.Replace("<tds:GetProfiles", $"<tds:{operation}", StringComparison.Ordinal);

        Assert.Null(NvrMetadataOperations.Validate(body, out var found));
        Assert.Equal(operation, found);
    }

    [Theory]
    [InlineData("GetStreamUri")]
    [InlineData("GetSnapshotUri")]
    [InlineData("GetReplayUri")]
    [InlineData("FindRecordings")]
    [InlineData("GetRecordingSearchResults")]
    [InlineData("GetRecordings")]
    [InlineData("ContinuousMove")]
    [InlineData("Stop")]
    [InlineData("SetVideoEncoderConfiguration")]
    [InlineData("CreateUsers")]
    public void Metadata_allowlist_denies_stream_recording_and_control(string operation)
    {
        var body = Envelope.Replace("<tds:GetProfiles", $"<tds:{operation}", StringComparison.Ordinal);

        Assert.NotNull(NvrMetadataOperations.Validate(body, out var found));
        Assert.Equal(operation, found);
        Assert.False(NvrMetadataOperations.IsAllowed(operation));
    }

    [Fact]
    public void Metadata_allowlist_is_narrower_than_the_read_only_allowlist()
    {
        Assert.True(NvrReadOnlyOperations.IsAllowed("GetStreamUri"));
        Assert.False(NvrMetadataOperations.IsAllowed("GetStreamUri"));
        Assert.DoesNotContain("GetStreamUri", NvrMetadataOperations.Approved);
        Assert.Contains("GetStreamUri", NvrMetadataOperations.Excluded);
        Assert.Contains("RTSP DESCRIBE", NvrMetadataOperations.Excluded);
    }

    [Fact]
    public void Metadata_gate_is_blocked_without_its_own_authorization()
    {
        var decision = NvrMetadataAccessGate.Evaluate("192.168.1.108", 80, false, metadataAuthorized: false, "192.168.1.108");

        Assert.False(decision.Allowed);
        Assert.Contains("--nvr-metadata-authorized", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_gate_requires_a_separate_exact_host_confirmation()
    {
        var wrong = NvrMetadataAccessGate.Evaluate("192.168.1.108", 80, false, metadataAuthorized: true, "192.168.1.109");
        var missing = NvrMetadataAccessGate.Evaluate("192.168.1.108", 80, false, metadataAuthorized: true, confirmedHost: null);
        var accepted = NvrMetadataAccessGate.Evaluate("192.168.1.108", 80, false, metadataAuthorized: true, "192.168.1.108");

        Assert.False(wrong.Allowed);
        Assert.False(missing.Allowed);
        Assert.True(accepted.Allowed);
        Assert.Contains("no RTSP", accepted.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Metadata_gate_rejects_invalid_ports_and_missing_host()
    {
        Assert.False(NvrMetadataAccessGate.Evaluate(null, 80, false, true, null).Allowed);
        Assert.False(NvrMetadataAccessGate.Evaluate("nvr", 0, false, true, "nvr").Allowed);
        Assert.False(NvrMetadataAccessGate.Evaluate("nvr", 70000, false, true, "nvr").Allowed);
    }

    [Theory]
    [InlineData("192.168.1.108", "192.168.1.***")]
    [InlineData("nvr.example.local", "nvr.***")]
    [InlineData("fe80::1", "fe80:****")]
    public void Masked_hosts_hide_network_details(string host, string expected)
    {
        Assert.Equal(expected, NvrSafety.MaskHost(host));
    }

    [Fact]
    public void Masking_a_missing_host_is_explicit()
    {
        Assert.Equal("not supplied", NvrSafety.MaskHost(null));
        Assert.Equal("not supplied", NvrSafety.MaskHost("  "));
    }

    [Fact]
    public void Redaction_never_preserves_uri_credentials()
    {
        var redacted = NvrSafety.Redact(new Uri("rtsp://admin:secret@192.168.1.108:554/live"));

        Assert.DoesNotContain("secret", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("192.168.1.108", redacted, StringComparison.Ordinal);
    }
}
