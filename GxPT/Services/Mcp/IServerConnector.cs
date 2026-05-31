using Mcp35.Client;

namespace GxPT
{
    // Builds an McpServerConnection for a spec (without opening it). Abstracting the actual transport
    // construction (process spawn / HTTP) behind this seam lets McpHost's assembly + lifecycle logic
    // be unit-tested with a fake connector — no real servers. DefaultServerConnector is the live impl.
    internal interface IServerConnector
    {
        // workdir is injected as GXPT_WORKDIR for workdir-scoped stdio servers when non-null.
        // Returns a Created (not yet opened) connection, or null if the spec can't be realized.
        McpServerConnection Create(McpServerSpec spec, string workdir);
    }
}
