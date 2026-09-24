using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VSOpenCode.Models;
using VSOpenCode.Resources;

namespace VSOpenCode.Services
{
    public class OpenCodeServerService : IOpenCodeServerService
    {
        private const int ConnectTimeoutMs = 30000;
        private const int HealthCheckIntervalMs = 500;

        private System.Diagnostics.Process _process;
        private HttpClient _httpClient;
        private ServerInfo _serverInfo;
        private ConnectionState _state = ConnectionState.Disconnected;
        private bool _launchedViaCmd;

        public ServerInfo ServerInfo => _serverInfo;
        public ConnectionState State => _state;
        public string LastError { get; private set; }
        public event Action<ConnectionState> StateChanged;

        public HttpClient GetClient()
        {
            return _httpClient;
        }

        public async Task<bool> StartAsync(string projectRoot)
        {
            Stop();
            SetState(ConnectionState.Connecting);

            try
            {
                var executable = OpenCodeLocator.Locate();
                if (executable == null)
                {
                    LastError = StringsHelper.ErrorOpenCodeNotFound
                        + "\n\n" + OpenCodeLocator.BuildNotFoundDiagnostic();
                    System.Diagnostics.Debug.WriteLine($"[OpenCode] {LastError}");
                    SetState(ConnectionState.Error);
                    return false;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable.FileName,
                    Arguments = executable.BuildArguments("serve"),
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // fnm/nvm/volta never reach Visual Studio's PATH, so the child
                // process would inherit an environment without node on it.
                ApplyNodeDirectory(psi, executable.NodeDirectory);

                _launchedViaCmd = executable.IsBatchShim;

                _process = System.Diagnostics.Process.Start(psi);
                if (_process == null)
                {
                    LastError = StringsHelper.ErrorServerStartFailed
                        + $" (could not launch '{executable.FileName}')";
                    SetState(ConnectionState.Error);
                    return false;
                }

                LastError = null;

                ProcessBinding.BindToCurrentProcess(_process);

                var resolvedInfo = await ResolveServerUrlAsync(_process, ConnectTimeoutMs);
                if (resolvedInfo == null)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to detect server URL from process output");
                    SetState(ConnectionState.Error);
                    return false;
                }

                _serverInfo = resolvedInfo;

                var healthy = await WaitForHealthAsync(ConnectTimeoutMs);
                if (healthy)
                {
                    await InitializeHttpClientAsync();
                    SetState(ConnectionState.Connected);
                    return true;
                }

                SetState(ConnectionState.Error);
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start server: {ex}");
                LastError = StringsHelper.ErrorServerStartFailed + "\n\n" + ex.Message;
                SetState(ConnectionState.Error);
                return false;
            }
        }

        public async Task<bool> CheckHealthAsync()
        {
            if (_httpClient == null) return false;
            try
            {
                var response = await _httpClient.GetAsync("/global/health");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var health = JsonConvert.DeserializeObject<HealthInfo>(json);
                    return health?.Healthy == true;
                }
                return false;
            }
            catch { return false; }
        }

        public void Stop()
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _serverInfo = null;

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        // opencode started through cmd.exe is a child of cmd.exe —
                        // killing cmd.exe alone would orphan the real server.
                        if (_launchedViaCmd)
                            KillProcessTree(_process.Id);

                        _process.Kill();
                        _process.WaitForExit(5000);
                    }
                }
                catch { }

                _process.Dispose();
            }
            _process = null;
            _launchedViaCmd = false;
            SetState(ConnectionState.Disconnected);
        }

        /// <summary>
        /// Put the directory holding <c>node.exe</c> at the front of the child
        /// process PATH. Version managers only expose node from inside an
        /// interactive shell, which Visual Studio never is.
        /// </summary>
        private static void ApplyNodeDirectory(System.Diagnostics.ProcessStartInfo psi, string nodeDirectory)
        {
            if (string.IsNullOrEmpty(nodeDirectory)) return;

            try
            {
                var environment = psi.EnvironmentVariables;

                string existingKey = null;
                foreach (string key in environment.Keys)
                {
                    if (string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase))
                    {
                        existingKey = key;
                        break;
                    }
                }

                var current = existingKey != null ? environment[existingKey] : string.Empty;

                // Normalise the key so the env block does not end up with both
                // "Path" and "PATH", where the winner is undefined.
                if (existingKey != null && !string.Equals(existingKey, "PATH", StringComparison.Ordinal))
                    environment.Remove(existingKey);

                var alreadyPresent = (current ?? string.Empty)
                    .Split(Path.PathSeparator)
                    .Any(entry => string.Equals(
                        entry.Trim().Trim('"'), nodeDirectory, StringComparison.OrdinalIgnoreCase));

                environment["PATH"] = alreadyPresent
                    ? current
                    : nodeDirectory + Path.PathSeparator + current;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenCode] Could not extend PATH: {ex.Message}");
            }
        }

        private static void KillProcessTree(int processId)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/pid {processId} /f /t",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var killer = System.Diagnostics.Process.Start(psi))
                    killer?.WaitForExit(3000);
            }
            catch { }
        }

        public void UpdateConnectionState(bool connected)
        {
            if (connected)
                SetState(ConnectionState.Connected);
            else
                SetState(ConnectionState.Disconnected);
        }

        private static async Task<ServerInfo> ResolveServerUrlAsync(
            System.Diagnostics.Process process, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<ServerInfo>();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            async Task ReadStreamAsync(System.IO.StreamReader reader, string label)
            {
                try
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        var lineTask = reader.ReadLineAsync();
                        var delayTask = Task.Delay(1000);
                        var completed = await Task.WhenAny(lineTask, delayTask);
                        if (completed == delayTask) continue;
                        var line = await lineTask;
                        if (line == null) break;
                        System.Diagnostics.Debug.WriteLine($"OpenCode {label}: {line}");
                        if (TryParseListenLine(line, out string host, out int port))
                        {
                            tcs.TrySetResult(new ServerInfo(host, port));
                            return;
                        }
                    }
                }
                catch { }
            }

            var stdoutTask = ReadStreamAsync(process.StandardOutput, "stdout");
            var stderrTask = ReadStreamAsync(process.StandardError, "stderr");
            var timeoutTask = Task.Delay(timeoutMs);

            await Task.WhenAny(tcs.Task, timeoutTask);
            return tcs.Task.IsCompleted ? await tcs.Task : null;
        }

        private static bool TryParseListenLine(string line, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrEmpty(line)) return false;
            const string prefix = "opencode server listening on http://";
            var idx = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var url = line.Substring(idx + prefix.Length).Trim();
            var colonIdx = url.LastIndexOf(':');
            if (colonIdx < 0) return false;
            host = url.Substring(0, colonIdx);
            return int.TryParse(url.Substring(colonIdx + 1), out port);
        }

        private async Task<bool> WaitForHealthAsync(int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (await TryConnectAsync(_serverInfo))
                    return true;
                await Task.Delay(HealthCheckIntervalMs);
            }
            return false;
        }

        private async Task<bool> TryConnectAsync(ServerInfo info)
        {
            try
            {
                using (var client = CreateHttpClient(info))
                {
                    client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync("/global/health");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var health = JsonConvert.DeserializeObject<HealthInfo>(json);
                        return health?.Healthy == true;
                    }
                }
            }
            catch { }
            return false;
        }

        private async Task InitializeHttpClientAsync()
        {
            _httpClient?.Dispose();
            _httpClient = CreateHttpClient(_serverInfo);
            await Task.CompletedTask;
        }

        private static HttpClient CreateHttpClient(ServerInfo info)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            return new HttpClient(handler)
            {
                BaseAddress = new Uri(info.BaseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        private void SetState(ConnectionState newState)
        {
            if (_state != newState)
            {
                _state = newState;
                StateChanged?.Invoke(newState);
            }
        }

    }
}
