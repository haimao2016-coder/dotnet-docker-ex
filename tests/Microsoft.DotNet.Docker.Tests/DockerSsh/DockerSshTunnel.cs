// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Microsoft.DotNet.Docker.Tests.DockerSsh;

/// <summary>
/// Forwards a private local Unix socket to a remote Docker daemon socket using the OpenSSH client
/// (<c>ssh -N -L local.sock:/var/run/docker.sock</c>).
/// </summary>
/// <remarks>
/// The Docker CLI is then pointed at the local socket with <c>DOCKER_HOST=unix://...</c>, so every
/// existing <c>docker</c> invocation works unchanged against the remote daemon. Only the SSH server and
/// the Docker daemon are needed on the remote host; no Docker CLI is required there.
/// </remarks>
public sealed class DockerSshTunnel : IDisposable
{
    // sun_path is 104 bytes on macOS and 108 on Linux, including the terminating NUL.
    private const int MaxUnixSocketPathLength = 103;

    private readonly Process _process;
    private readonly DirectoryInfo _socketDirectory;
    private readonly StringBuilder _stdErr;
    private bool _disposed;

    private DockerSshTunnel(Process process, DirectoryInfo socketDirectory, string socketPath, StringBuilder stdErr)
    {
        _process = process;
        _socketDirectory = socketDirectory;
        _stdErr = stdErr;
        SocketPath = socketPath;
    }

    /// <summary>Local Unix socket that is forwarded to the remote Docker daemon.</summary>
    public string SocketPath { get; }

    /// <summary>Value to use for the <c>DOCKER_HOST</c> environment variable.</summary>
    public string DockerHost => "unix://" + SocketPath;

    public bool IsRunning => !_disposed && !_process.HasExited;

    /// <summary>
    /// Starts the tunnel and waits until the local socket is available.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tunnel could not be established.</exception>
    public static DockerSshTunnel Start(DockerSshOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Controlling Docker over SSH requires forwarding a Unix socket, which is only supported on Linux and macOS clients.");
        }

        // CreateTempSubdirectory creates the directory with mode 0700 on Unix, so only the current user can
        // reach the forwarded socket (which grants full control of the remote Docker daemon).
        DirectoryInfo socketDirectory = Directory.CreateTempSubdirectory("dkssh-");
        string socketPath = Path.Combine(socketDirectory.FullName, "docker.sock");
        if (socketPath.Length > MaxUnixSocketPathLength)
        {
            TryDeleteDirectory(socketDirectory);
            throw new InvalidOperationException(
                $"The local socket path '{socketPath}' is too long for a Unix socket. Set TMPDIR to a shorter directory.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = options.SshExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in options.BuildTunnelArguments(socketPath))
        {
            startInfo.ArgumentList.Add(arg);
        }

        StringBuilder stdErr = new();
        Process process = new() { StartInfo = startInfo };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdErr)
                {
                    stdErr.AppendLine(e.Data);
                }
            }
        };
        process.OutputDataReceived += (_, _) => { };

        log?.Invoke($"Starting SSH tunnel to Docker daemon at {options.DisplayTarget}");

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            TryDeleteDirectory(socketDirectory);
            throw new InvalidOperationException(
                $"Failed to start the SSH client '{options.SshExecutable}'. Install OpenSSH or set {DockerSshOptions.SshExecutableVariable}.",
                ex);
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        DockerSshTunnel tunnel = new(process, socketDirectory, socketPath, stdErr);
        TimeSpan timeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds + 10);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!File.Exists(socketPath))
        {
            if (process.HasExited)
            {
                process.WaitForExit();
                string error = tunnel.GetStandardError();
                int exitCode = process.ExitCode;
                tunnel.Dispose();
                throw new InvalidOperationException(
                    $"SSH tunnel to {options.DisplayTarget} exited with code {exitCode} before it was ready." +
                    $"{Environment.NewLine}ssh stderr: {error}");
            }

            if (stopwatch.Elapsed > timeout)
            {
                string error = tunnel.GetStandardError();
                tunnel.Dispose();
                throw new InvalidOperationException(
                    $"Timed out after {timeout.TotalSeconds:0}s waiting for the SSH tunnel to {options.DisplayTarget}." +
                    $"{Environment.NewLine}ssh stderr: {error}");
            }

            Thread.Sleep(100);
        }

        // The local socket exists as soon as SSH authentication succeeded, but the server can still refuse to
        // open the forwarded channel (for example when the user cannot access the Docker socket or sshd forbids
        // stream local forwarding). Ping the daemon so such problems fail fast with a clear message.
        string? pingError = PingDockerDaemon(socketPath, TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
        if (pingError is not null)
        {
            string error = tunnel.GetStandardError();
            tunnel.Dispose();
            throw new InvalidOperationException(
                $"Connected to {options.DisplayTarget} over SSH, but the Docker daemon did not answer: {pingError}. " +
                $"Check that the SSH user can access '{options.RemoteSocket}' on the remote host and that sshd allows " +
                $"stream local forwarding (AllowStreamLocalForwarding, and no 'restrict' without 'port-forwarding' in authorized_keys)." +
                $"{Environment.NewLine}ssh stderr: {error}");
        }

        log?.Invoke($"SSH tunnel ready; using DOCKER_HOST={tunnel.DockerHost}");
        return tunnel;
    }

    /// <summary>
    /// Sends <c>GET /_ping</c> to the Docker API over the forwarded socket.
    /// </summary>
    /// <returns><c>null</c> on success, otherwise a description of the failure.</returns>
    internal static string? PingDockerDaemon(string socketPath, TimeSpan timeout)
    {
        try
        {
            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            int timeoutMs = (int)timeout.TotalMilliseconds;
            socket.SendTimeout = timeoutMs;
            socket.ReceiveTimeout = timeoutMs;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            socket.Send(Encoding.ASCII.GetBytes("GET /_ping HTTP/1.0\r\nHost: docker\r\n\r\n"));

            byte[] buffer = new byte[256];
            int read = socket.Receive(buffer);
            if (read == 0)
            {
                return "the forwarded connection was closed by the remote side";
            }

            string statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim();
            return statusLine.StartsWith("HTTP/1.", StringComparison.Ordinal) && statusLine.Contains(" 200", StringComparison.Ordinal)
                ? null
                : $"unexpected response '{statusLine}'";
        }
        catch (SocketException ex)
        {
            return ex.Message;
        }
    }

    public string GetStandardError()
    {
        lock (_stdErr)
        {
            return _stdErr.ToString().Trim();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        finally
        {
            _process.Dispose();
            TryDeleteDirectory(_socketDirectory);
        }
    }

    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
