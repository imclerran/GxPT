namespace Mcp35.Core.Diagnostics
{
    /// <summary>
    /// The only way Core logs. The host adapts its own logger to this seam (seam rule #1);
    /// Core never references GxPT.*. <see cref="NullLogSink.Instance"/> is the default no-op.
    /// </summary>
    public interface ILogSink
    {
        void Log(string category, string message);
    }

    /// <summary>A no-op sink used when the host injects nothing.</summary>
    public sealed class NullLogSink : ILogSink
    {
        public static readonly NullLogSink Instance = new NullLogSink();

        private NullLogSink() { }

        public void Log(string category, string message) { }
    }
}
