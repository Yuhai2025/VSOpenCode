namespace VSOpenCode.Services
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Error
    }

    public interface IOpenCodeServerService
    {
        Models.ServerInfo ServerInfo { get; }
        ConnectionState State { get; }

        /// <summary>
        /// Diagnostics for the most recent failed start, or <c>null</c> when the
        /// last start succeeded.
        /// </summary>
        string LastError { get; }

        System.Net.Http.HttpClient GetClient();
        System.Threading.Tasks.Task<bool> StartAsync(string projectRoot);
        System.Threading.Tasks.Task<bool> CheckHealthAsync();
        void Stop();
        event System.Action<ConnectionState> StateChanged;

        void UpdateConnectionState(bool connected);
    }
}
