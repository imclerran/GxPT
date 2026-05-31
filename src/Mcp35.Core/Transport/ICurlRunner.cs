using System;

namespace Mcp35.Core.Transport
{
    /// <summary>
    /// The curl-exec seam. <see cref="CurlRunner"/> is the real implementation; consumers that
    /// want to be testable without a network (e.g. the client's HttpTransport) depend on this
    /// interface and accept an injected fake. Mirrors <see cref="CurlRunner"/>'s public surface.
    /// </summary>
    public interface ICurlRunner
    {
        /// <summary>Run a buffered request; the full response body, status, and headers are returned.</summary>
        CurlResult Run(CurlRequest req);

        /// <summary>Run a streaming request, delivering each stdout line to <paramref name="onLine"/>.</summary>
        void RunStreaming(CurlRequest req, Action<string> onLine, Action onDone, Action<string> onError);
    }
}
