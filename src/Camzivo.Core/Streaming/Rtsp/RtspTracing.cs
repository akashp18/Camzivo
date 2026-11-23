namespace Camzivo.Core.Streaming.Rtsp;

/// <summary>
/// Optional tracing seam for RTSP exchanges.
///
/// Core deliberately has no logger, but when a device refuses a stream the only useful evidence is what it actually
/// answered: a 401, a 503, or nothing at all before the socket closed. The application installs a sink (the app log);
/// tests and the engine run with none.
///
/// Only method, path, and status/reason are written. Authorization values, nonces and credentials never reach this
/// seam, so a captured trace can be shared without leaking anything.
/// </summary>
public static class RtspTracing
{
    public static Action<string>? Sink { get; set; }

    public static bool IsEnabled => Sink is not null;

    public static void Write(string message)
    {
        if (Sink is { } sink)
        {
            sink(message);
        }
    }
}
