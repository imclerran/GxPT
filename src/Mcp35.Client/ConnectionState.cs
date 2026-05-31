using System;

namespace Mcp35.Client
{
    /// <summary>Lifecycle states of an <see cref="McpServerConnection"/>.</summary>
    public enum ConnectionState
    {
        Created,
        Starting,
        Initializing,
        Ready,
        Faulted,
        Closed
    }

    public sealed class ConnectionStateEventArgs : EventArgs
    {
        public readonly ConnectionState OldState;
        public readonly ConnectionState NewState;

        /// <summary>Populated when transitioning to <see cref="ConnectionState.Faulted"/>.</summary>
        public readonly string FaultMessage;

        public ConnectionStateEventArgs(ConnectionState oldState, ConnectionState newState, string faultMessage)
        {
            OldState = oldState;
            NewState = newState;
            FaultMessage = faultMessage;
        }
    }
}
