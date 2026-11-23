using Camzivo.Core.Devices;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;

namespace Camzivo.Data.Net;

/// <summary>Selects the allowlist enforced below a recorder ONVIF transport.</summary>
public enum NvrOperationScope
{
    FullReadOnly = 0,
    MetadataOnly = 1,
}

/// <summary>
/// Enforces the recorder boundary below every ONVIF caller. No UI path can bypass this by constructing a request:
/// the exact host, port and scheme are checked first, then the SOAP operation must be on the selected allowlist.
/// Redirects are disabled by <see cref="HttpOnvifTransport"/>.
/// </summary>
public sealed class ReadOnlyNvrTransport(
    IOnvifTransport inner,
    NvrAccessPolicy policy,
    NvrOperationScope scope = NvrOperationScope.FullReadOnly) : IOnvifTransport
{
    public async Task<OnvifHttpResponse> PostAsync(
        Uri uri,
        string body,
        string? authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!policy.AllowsOnvifUri(uri))
        {
            throw new NvrAccessDeniedException(
                "ONVIF request",
                uri,
                "the address is outside the approved recorder host, port or scheme");
        }

        string operation;
        var violation = scope == NvrOperationScope.MetadataOnly
            ? NvrMetadataOperations.Validate(body, out operation)
            : NvrReadOnlyOperations.Validate(body, out operation);
        if (violation is not null)
        {
            throw new NvrAccessDeniedException(operation, uri, violation);
        }

        return await inner.PostAsync(uri, body, authorization, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Prevents a recorder's connection test from opening a returned stream on a camera IP or an unapproved port.
/// </summary>
public sealed class ReadOnlyNvrRtspProbe(IRtspEndpointProbe inner, NvrAccessPolicy policy) : IRtspEndpointProbe
{
    public Task<StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!policy.AllowsStreamUri(uri))
        {
            throw new NvrAccessDeniedException(
                "RTSP connection test",
                uri,
                "the stream address is outside the approved recorder host and RTSP port");
        }

        return inner.ProbeAsync(uri, cancellationToken);
    }
}
