using System.Reflection;
using Camzivo.Core.Devices;
using Camzivo.Desktop.Services;
using Camzivo.Desktop.ViewModels;
using Xunit;

namespace Camzivo.Desktop.Tests;

public sealed class NvrConnectionWindowTests
{
    private const string DeviceInfo = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
            <tds:Manufacturer>Dahua</tds:Manufacturer><tds:Model>NVR5216</tds:Model>
            <tds:FirmwareVersion>4.0</tds:FirmwareVersion><tds:SerialNumber>SN-1</tds:SerialNumber>
          </tds:GetDeviceInformationResponse>
        </s:Body></s:Envelope>
        """;

    private static string Capabilities(string media) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <tds:Capabilities><tt:Media><tt:XAddr>{media}</tt:XAddr></tt:Media></tds:Capabilities>
          </tds:GetCapabilitiesResponse>
        </s:Body></s:Envelope>
        """;

    private const string Profiles = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:Profiles token="main"><tt:Name>Channel 1 main</tt:Name>
              <tt:VideoSourceConfiguration><tt:SourceToken>src1</tt:SourceToken></tt:VideoSourceConfiguration>
              <tt:VideoEncoderConfiguration token="enc-main"><tt:Encoding>H264</tt:Encoding>
                <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
              </tt:VideoEncoderConfiguration>
            </trt:Profiles>
            <trt:Profiles token="sub"><tt:Name>Channel 1 sub</tt:Name>
              <tt:VideoSourceConfiguration><tt:SourceToken>src1</tt:SourceToken></tt:VideoSourceConfiguration>
              <tt:VideoEncoderConfiguration token="enc-sub"><tt:Encoding>H264</tt:Encoding>
                <tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution>
              </tt:VideoEncoderConfiguration>
            </trt:Profiles>
          </trt:GetProfilesResponse>
        </s:Body></s:Envelope>
        """;

    private const string Empty = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body /></s:Envelope>
        """;

    private static string Respond(string operation, string media = "http://192.168.1.108:80/onvif/media") => operation switch
    {
        "GetDeviceInformation" => DeviceInfo,
        "GetCapabilities" => Capabilities(media),
        "GetProfiles" => Profiles,
        _ => Empty,
    };

    private static NvrConnectionViewModel Ready(IOnvifTransport transport, TimeSpan? timeout = null)
    {
        var viewModel = new NvrConnectionViewModel(() => transport, timeout);
        viewModel.Host = "192.168.1.108";
        return viewModel;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Opening_the_window_model_does_nothing_on_the_network()
    {
        var created = 0;
        var viewModel = new NvrConnectionViewModel(() =>
        {
            created++;
            return new ScriptedTransport(_ => Empty);
        });

        Assert.Equal(NvrConnectionState.Idle, viewModel.State);
        Assert.Equal(0, created);
        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.HasResult);
        Assert.True(viewModel.IsIdle);
        Assert.Equal(string.Empty, viewModel.Host);
        Assert.Equal("80", viewModel.PortText);
        Assert.False(viewModel.IsPasswordRevealed);
    }

    [Theory]
    [InlineData("", "80", "Enter the NVR IP address")]
    [InlineData("http://192.168.1.108", "80", "Enter only the IP address")]
    [InlineData("192.168.1.108/onvif", "80", "Enter only the IP address")]
    [InlineData("admin@192.168.1.108", "80", "Enter only the IP address")]
    [InlineData("192.168.1.108", "0", "between 1 and 65535")]
    [InlineData("192.168.1.108", "70000", "between 1 and 65535")]
    [InlineData("192.168.1.108", "eighty", "whole number")]
    public void Invalid_fields_are_rejected_before_any_confirmation(string host, string port, string expected)
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);
        viewModel.Host = host;
        viewModel.PortText = port;

        viewModel.RequestTest("password");

        Assert.NotEqual(NvrConnectionState.Confirming, viewModel.State);
        Assert.True(viewModel.HasError);
        Assert.Contains(expected, viewModel.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public void A_missing_password_is_rejected_before_any_confirmation()
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);

        viewModel.RequestTest(null);

        Assert.True(viewModel.HasError);
        Assert.Contains("password", viewModel.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(NvrConnectionState.Confirming, viewModel.State);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public void Protocol_change_moves_the_port_between_its_defaults_and_keeps_a_custom_one()
    {
        var viewModel = new NvrConnectionViewModel(() => new ScriptedTransport(_ => Empty));

        Assert.Equal("80", viewModel.PortText);

        viewModel.UseTls = true;
        Assert.Equal("443", viewModel.PortText);
        Assert.Equal("https", viewModel.Scheme);

        viewModel.PortText = "8443";
        viewModel.UseTls = false;
        Assert.Equal("8443", viewModel.PortText);
        Assert.Equal("http", viewModel.Scheme);
    }

    [Fact]
    public void The_password_is_masked_by_default_and_never_held_by_the_view_model()
    {
        var viewModel = new NvrConnectionViewModel(() => new ScriptedTransport(_ => Empty));

        Assert.False(viewModel.IsPasswordRevealed);

        viewModel.IsPasswordRevealed = true;
        Assert.True(viewModel.IsPasswordRevealed);

        // The reveal flag is a UI switch only. No member of the view model may hold a secret string.
        var members = typeof(NvrConnectionViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        static bool HoldsString(MemberInfo member) => member switch
        {
            PropertyInfo property => property.PropertyType == typeof(string),
            FieldInfo field => field.FieldType == typeof(string),
            _ => false,
        };

        Assert.DoesNotContain(members, member =>
            member.Name.Contains("password", StringComparison.OrdinalIgnoreCase) && HoldsString(member));
    }

    [Fact]
    public void The_view_model_has_no_storage_dependency()
    {
        var constructors = typeof(NvrConnectionViewModel).GetConstructors();

        var parameters = Assert.Single(constructors).GetParameters();

        Assert.All(parameters, parameter =>
            Assert.True(
                parameter.ParameterType == typeof(Func<IOnvifTransport>) ||
                parameter.ParameterType == typeof(TimeSpan?) ||
                parameter.ParameterType == typeof(TimeSpan),
                $"{parameter.Name} would give the window a way to persist or reload a credential."));
    }

    [Fact]
    public void Test_connection_only_asks_for_confirmation()
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);

        viewModel.RequestTest("password");

        Assert.Equal(NvrConnectionState.Confirming, viewModel.State);
        Assert.True(viewModel.IsConfirming);
        Assert.False(viewModel.IsTesting);
        Assert.Contains("http://192.168.1.108:80", viewModel.ConfirmationDetail, StringComparison.Ordinal);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public void Declining_the_confirmation_sends_nothing()
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);
        viewModel.RequestTest("password");

        viewModel.Decline();

        Assert.Equal(NvrConnectionState.Idle, viewModel.State);
        Assert.Empty(viewModel.Rows);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Confirming_is_required_before_the_probe_can_run()
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);

        // No RequestTest: there is no pending confirmation, so the call must be a no-op.
        await viewModel.ConfirmAsync("password");

        Assert.Equal(NvrConnectionState.Idle, viewModel.State);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task A_confirmed_test_sends_only_metadata_operations()
    {
        var transport = new ScriptedTransport(body => Respond(NvrReadOnlyOperations.FindOperation(body)!));
        var viewModel = Ready(transport);
        viewModel.RequestTest("password");

        await viewModel.ConfirmAsync("password");

        Assert.Equal(NvrConnectionState.Passed, viewModel.State);
        Assert.NotEmpty(transport.Calls);
        Assert.All(transport.Calls, uri => Assert.Equal("192.168.1.108", uri.Host));
        Assert.All(transport.Operations, operation => Assert.True(NvrMetadataOperations.IsAllowed(operation)));
        Assert.DoesNotContain(transport.Operations, operation => operation is
            "GetStreamUri" or "GetReplayUri" or "FindRecordings" or "GetRecordingSearchResults" or
            "GetRecordings" or "ContinuousMove" or "Stop");

        static string Value(NvrConnectionViewModel model, string label) =>
            model.Rows.Single(row => row.Label == label).Value;

        Assert.Equal("Dahua", Value(viewModel, "Manufacturer"));
        Assert.Equal("NVR5216", Value(viewModel, "Model"));
        Assert.Equal("4.0", Value(viewModel, "Firmware"));
        Assert.Equal("2", Value(viewModel, "Media profiles"));
        Assert.Contains("Main stream", viewModel.Rows.Select(row => row.Label));
        Assert.Contains("Sub stream", viewModel.Rows.Select(row => row.Label));
        Assert.Equal("None", Value(viewModel, "Blocked endpoints"));
    }

    [Fact]
    public async Task A_cancelled_test_reports_cancelled_and_stops_asking()
    {
        var transport = new BlockingTransport();
        var viewModel = Ready(transport);
        viewModel.RequestTest("password");

        var run = viewModel.ConfirmAsync("password");
        await WaitUntil(() => transport.Calls > 0, TimeSpan.FromSeconds(5));
        viewModel.CancelTest();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NvrConnectionState.Cancelled, viewModel.State);
        Assert.True(viewModel.HasResult);
        Assert.NotNull(viewModel.Error);
        Assert.False(viewModel.IsTesting);

        var callsAfterCancel = transport.Calls;
        await Task.Delay(200);
        Assert.Equal(callsAfterCancel, transport.Calls);
    }

    [Fact]
    public async Task The_overall_time_limit_is_reported_as_a_failure()
    {
        var transport = new BlockingTransport();
        var viewModel = Ready(transport, TimeSpan.FromMilliseconds(150));
        viewModel.RequestTest("password");

        await viewModel.ConfirmAsync("password").WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(NvrConnectionState.Failed, viewModel.State);
        Assert.Contains("time limit", viewModel.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsTesting);
    }

    [Fact]
    public async Task Rejected_credentials_are_reported_without_leaking_the_password()
    {
        const string secret = "Sup3rSecret!";
        var transport = new ScriptedTransport(
            _ => Empty, statusCode: 401, challenge: "Digest realm=\"nvr\", nonce=\"abc123\", qop=\"auth\"");
        var viewModel = Ready(transport);
        viewModel.RequestTest(secret);

        await viewModel.ConfirmAsync(secret);

        Assert.Equal(NvrConnectionState.Failed, viewModel.State);
        Assert.NotNull(viewModel.Error);
        Assert.DoesNotContain(secret, viewModel.Error, StringComparison.Ordinal);
        Assert.All(viewModel.Rows, row => Assert.DoesNotContain(secret, row.Value, StringComparison.Ordinal));
        Assert.Equal(2, transport.Calls.Count); // one request, one digest/basic retry
    }

    [Fact]
    public void Resetting_clears_results_and_the_reveal_state_without_contacting_anything()
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);

        viewModel.IsPasswordRevealed = true;
        viewModel.RequestTest("password");
        viewModel.Decline();

        viewModel.Reset();

        Assert.Equal(NvrConnectionState.Idle, viewModel.State);
        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.HasResult);
        Assert.False(viewModel.IsPasswordRevealed);
        Assert.Null(viewModel.Error);
        Assert.Equal(string.Empty, viewModel.ConfirmationDetail);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public void The_devices_page_action_only_asks_the_shell_to_open_the_window()
    {
        var devices = new DevicesViewModel(demo: false);
        var requested = 0;
        devices.TestNvrConnectionRequested += (_, _) => requested++;

        devices.TestNvrConnectionCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Cancelling_outside_a_test_is_harmless()
    {
        var transport = new ScriptedTransport(_ => Empty);
        var viewModel = Ready(transport);

        viewModel.CancelTest();

        Assert.Equal(NvrConnectionState.Idle, viewModel.State);
        Assert.Empty(transport.Calls);
    }

    private sealed class ScriptedTransport(
        Func<string, string> respond,
        int statusCode = 200,
        string? challenge = null) : IOnvifTransport
    {
        private readonly List<string> _operations = [];

        public List<Uri> Calls { get; } = [];

        public IReadOnlyList<string> Operations
        {
            get
            {
                lock (_operations)
                {
                    return _operations.ToList();
                }
            }
        }

        public Task<OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            Calls.Add(uri);

            var operation = NvrReadOnlyOperations.FindOperation(body) ?? "unreadable";
            lock (_operations)
            {
                _operations.Add(operation);
            }

            return Task.FromResult(statusCode is 200 or 202
                ? new OnvifHttpResponse(statusCode, respond(body))
                : new OnvifHttpResponse(statusCode, string.Empty, challenge));
        }
    }

    private sealed class BlockingTransport : IOnvifTransport
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new OnvifHttpResponse(200, "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>");
        }
    }
}
