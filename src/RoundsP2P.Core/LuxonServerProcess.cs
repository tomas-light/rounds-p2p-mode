using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RoundsP2P.Core
{
    /// <summary>Owns one pinned Luxon v1.5 process and its newly written configuration.</summary>
    public sealed class LuxonServerProcess : IDisposable
    {
        public const string ExpectedSha256 = "d5b272f49b21757d36094f30f986e4f4d2f1502eec79be33ce998ea19fff3286";

        private readonly Process process;
        private readonly int masterPort;
        private readonly int gamePort;
        private readonly Stopwatch startupClock = new Stopwatch();
        private long nextProbeAtMilliseconds = 250;
        private bool listening;
        private bool disposed;

        public LuxonServerProcess(string executablePath, string workingDirectory, PrototypeOptions options, Action<string> log)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.Role != PrototypeRole.Host)
                throw new ArgumentException("Only the host may start the local server.", nameof(options));
            if (log == null)
                throw new ArgumentNullException(nameof(log));
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("An executable path is required.", nameof(executablePath));
            if (string.IsNullOrWhiteSpace(workingDirectory))
                throw new ArgumentException("A dedicated working directory is required.", nameof(workingDirectory));

            string executable = Path.GetFullPath(executablePath);
            string directory = Path.GetFullPath(workingDirectory);
            if (File.Exists(directory))
                throw new ArgumentException("The working directory path points to a file.", nameof(workingDirectory));
            masterPort = options.MasterPort;
            gamePort = options.GamePort;

            // Keep the verified file open without write/delete sharing through process
            // creation, so the executable cannot be swapped after validation on Windows.
            using (var binary = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string digest;
                using (SHA256 sha256 = SHA256.Create())
                    digest = BitConverter.ToString(sha256.ComputeHash(binary)).Replace("-", string.Empty).ToLowerInvariant();
                if (!string.Equals(digest, ExpectedSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Luxon executable SHA-256 does not match the pinned v1.5 release.");

                EnsurePortsAvailable(masterPort, gamePort);
                Directory.CreateDirectory(directory);
                using (var config = new FileStream(Path.Combine(directory, "config.yml"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(config, new UTF8Encoding(false)))
                    writer.Write(LuxonConfig.Render(options));

                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        WorkingDirectory = directory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.OutputDataReceived += (_, args) => LogSafely(log, args.Data);
                process.ErrorDataReceived += (_, args) => LogSafely(log, args.Data);

                bool started = false;
                try
                {
                    started = process.Start();
                    if (!started)
                        throw new InvalidOperationException("Luxon process did not start.");
                    startupClock.Start();
                    ProcessId = process.Id;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch
                {
                    try
                    {
                        if (started)
                            StopOwnedProcess(process);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                    throw;
                }
            }
        }

        public bool HasExited => disposed || process.HasExited;
        public int ProcessId { get; }

        /// <summary>
        /// Checks that both IPv4 UDP ports are occupied after successful exclusive
        /// preflight, without privileged listener enumeration. The caller must bound
        /// readiness polling by a timeout.
        /// </summary>
        /// <remarks>
        /// Occupancy does not identify the owning PID. Another process can race the
        /// preflight-to-launch interval; an actual Photon handshake is still required.
        /// </remarks>
        public bool IsListening
        {
            get
            {
                if (HasExited)
                    return false;

                long elapsed = startupClock.ElapsedMilliseconds;
                if (!listening && elapsed >= nextProbeAtMilliseconds)
                {
                    // A bind probe briefly owns an idle port. Give Luxon time to bind
                    // first and avoid competing for the port on every Unity frame.
                    nextProbeAtMilliseconds = elapsed + 100;
                    listening = IsPortOccupied(masterPort) && IsPortOccupied(gamePort);
                }
                return listening;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            try
            {
                StopOwnedProcess(process);
            }
            finally
            {
                disposed = true;
                process.Dispose();
            }
        }

        private static void StopOwnedProcess(Process ownedProcess)
        {
            if (!ownedProcess.HasExited)
            {
                try
                {
                    ownedProcess.Kill();
                }
                catch (InvalidOperationException) when (ownedProcess.HasExited)
                {
                    // It exited between the check and Kill; still reap this process.
                }
            }
            if (!ownedProcess.WaitForExit(5000))
                throw new TimeoutException("The owned Luxon process did not exit within five seconds.");
        }

        private static void EnsurePortsAvailable(int masterPort, int gamePort)
        {
            // Hold both exclusive bindings simultaneously to detect either occupied
            // port before starting the server. Release them immediately before launch;
            // readiness is checked separately because another process can still race us.
            using (var master = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            using (var game = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                master.ExclusiveAddressUse = true;
                game.ExclusiveAddressUse = true;
                master.Bind(new IPEndPoint(IPAddress.Any, masterPort));
                game.Bind(new IPEndPoint(IPAddress.Any, gamePort));
            }
        }

        private static bool IsPortOccupied(int port)
        {
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.ExclusiveAddressUse = true;
                try
                {
                    probe.Bind(new IPEndPoint(IPAddress.Any, port));
                    return false;
                }
                catch (SocketException exception) when (
                    exception.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                    exception.SocketErrorCode == SocketError.AccessDenied)
                {
                    // Windows can report either error for a conflicting exclusive
                    // UDP binding. Other socket errors must not imply readiness.
                    return true;
                }
            }
        }

        private static void LogSafely(Action<string> log, string? message)
        {
            if (message == null)
                return;
            try
            {
                log(message);
            }
            catch
            {
                // A logging observer must not crash a process I/O callback thread.
            }
        }
    }
}
