// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Microsoft.DotNet.Docker.Tests.DockerSsh;

/// <summary>
/// Host key checking modes that are allowed when tunneling to a remote Docker daemon over SSH.
/// </summary>
/// <remarks>
/// Disabling host key checking (OpenSSH's <c>StrictHostKeyChecking=no</c>) is intentionally not supported,
/// because it would allow a man-in-the-middle to take control of the Docker daemon connection.
/// </remarks>
public enum DockerSshHostKeyChecking
{
    /// <summary>The host key must already be present in the known_hosts file (OpenSSH <c>yes</c>).</summary>
    Strict,

    /// <summary>
    /// Unknown hosts are added to known_hosts on first use, but changed keys are still rejected
    /// (OpenSSH <c>accept-new</c>).
    /// </summary>
    AcceptNew,
}

/// <summary>
/// Settings for controlling a remote Docker daemon through an SSH tunnel.
/// </summary>
/// <remarks>
/// All values come from environment variables so that no credentials need to be stored in the repository.
/// SSH tunneling is disabled unless <see cref="HostVariable"/> is set.
/// </remarks>
public sealed record DockerSshOptions
{
    public const string HostVariable = "DOCKER_SSH_HOST";
    public const string UserVariable = "DOCKER_SSH_USER";
    public const string PortVariable = "DOCKER_SSH_PORT";
    public const string IdentityFileVariable = "DOCKER_SSH_IDENTITY_FILE";
    public const string KnownHostsFileVariable = "DOCKER_SSH_KNOWN_HOSTS_FILE";
    public const string HostKeyCheckingVariable = "DOCKER_SSH_STRICT_HOST_KEY_CHECKING";
    public const string RemoteSocketVariable = "DOCKER_SSH_REMOTE_SOCKET";
    public const string ConnectTimeoutVariable = "DOCKER_SSH_CONNECT_TIMEOUT";
    public const string SshExecutableVariable = "DOCKER_SSH_EXECUTABLE";
    public const string PublishedPortHostVariable = "DOCKER_SSH_PUBLISHED_PORT_HOST";

    public const int DefaultPort = 22;
    public const string DefaultRemoteSocket = "/var/run/docker.sock";
    public const int DefaultConnectTimeoutSeconds = 10;
    public const string DefaultSshExecutable = "ssh";

    public required string Host { get; init; }
    public string? User { get; init; }
    public int Port { get; init; } = DefaultPort;
    public string? IdentityFile { get; init; }
    public string? KnownHostsFile { get; init; }
    public DockerSshHostKeyChecking HostKeyChecking { get; init; } = DockerSshHostKeyChecking.Strict;
    public string RemoteSocket { get; init; } = DefaultRemoteSocket;
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;
    public string SshExecutable { get; init; } = DefaultSshExecutable;

    /// <summary>
    /// Host name used to reach ports that containers publish on the remote Docker host.
    /// Defaults to <see cref="Host"/>.
    /// </summary>
    public string? PublishedPortHost { get; init; }

    public string EffectivePublishedPortHost => PublishedPortHost ?? Host;

    /// <summary>
    /// A description of the target that is safe to log (it never contains key material).
    /// </summary>
    public string DisplayTarget =>
        $"ssh://{(User is null ? string.Empty : User + "@")}{Host}:{Port.ToString(CultureInfo.InvariantCulture)}{RemoteSocket}";

    /// <summary>
    /// Reads options from the process environment. Returns <c>null</c> when SSH tunneling is not configured.
    /// </summary>
    public static DockerSshOptions? FromEnvironment() => FromVariables(Environment.GetEnvironmentVariable, File.Exists);

    /// <summary>
    /// Reads options using the given variable lookup. Returns <c>null</c> when SSH tunneling is not configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">A variable has an invalid or unsafe value.</exception>
    public static DockerSshOptions? FromVariables(Func<string, string?> getVariable, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(fileExists);

        string? Get(string name)
        {
            string? value = getVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? host = Get(HostVariable);
        if (host is null)
        {
            return null;
        }

        ValidateToken(HostVariable, host);

        string? user = Get(UserVariable);
        if (user is not null)
        {
            ValidateToken(UserVariable, user);
        }

        int port = ParseInt(PortVariable, Get(PortVariable), DefaultPort, 1, 65535);
        int connectTimeout = ParseInt(ConnectTimeoutVariable, Get(ConnectTimeoutVariable), DefaultConnectTimeoutSeconds, 1, 600);

        string? identityFile = Get(IdentityFileVariable);
        if (identityFile is not null && !fileExists(identityFile))
        {
            throw new InvalidOperationException(
                $"{IdentityFileVariable} points to a file that does not exist: '{identityFile}'.");
        }

        string? knownHostsFile = Get(KnownHostsFileVariable);
        if (knownHostsFile is not null && !fileExists(knownHostsFile))
        {
            throw new InvalidOperationException(
                $"{KnownHostsFileVariable} points to a file that does not exist: '{knownHostsFile}'.");
        }

        DockerSshHostKeyChecking hostKeyChecking = ParseHostKeyChecking(Get(HostKeyCheckingVariable));

        string remoteSocket = Get(RemoteSocketVariable) ?? DefaultRemoteSocket;
        if (!remoteSocket.StartsWith('/') || remoteSocket.Any(char.IsWhiteSpace) || remoteSocket.Contains(':'))
        {
            throw new InvalidOperationException(
                $"{RemoteSocketVariable} must be an absolute Unix socket path without spaces or ':', got '{remoteSocket}'.");
        }

        string? publishedPortHost = Get(PublishedPortHostVariable);
        if (publishedPortHost is not null)
        {
            ValidateToken(PublishedPortHostVariable, publishedPortHost);
        }

        return new DockerSshOptions
        {
            Host = host,
            User = user,
            Port = port,
            IdentityFile = identityFile,
            KnownHostsFile = knownHostsFile,
            HostKeyChecking = hostKeyChecking,
            RemoteSocket = remoteSocket,
            ConnectTimeoutSeconds = connectTimeout,
            SshExecutable = Get(SshExecutableVariable) ?? DefaultSshExecutable,
            PublishedPortHost = publishedPortHost,
        };
    }

    /// <summary>
    /// Builds the OpenSSH client arguments that forward <paramref name="localSocketPath"/> to the remote
    /// Docker socket. Each element is a separate argument; no shell quoting is involved.
    /// </summary>
    public IReadOnlyList<string> BuildTunnelArguments(string localSocketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localSocketPath);

        List<string> args =
        [
            // No remote command, no TTY: only port forwarding.
            "-N",
            "-T",
            // Never prompt for passwords or passphrases; fail instead.
            "-o", "BatchMode=yes",
            // Fail fast if the socket forward cannot be established.
            "-o", "ExitOnForwardFailure=yes",
            "-o", "StrictHostKeyChecking=" + (HostKeyChecking == DockerSshHostKeyChecking.AcceptNew ? "accept-new" : "yes"),
            "-o", "ConnectTimeout=" + ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "StreamLocalBindUnlink=yes",
            "-p", Port.ToString(CultureInfo.InvariantCulture),
        ];

        if (IdentityFile is not null)
        {
            args.AddRange(["-o", "IdentitiesOnly=yes", "-i", IdentityFile]);
        }

        if (KnownHostsFile is not null)
        {
            args.AddRange(["-o", "UserKnownHostsFile=" + KnownHostsFile]);
        }

        if (User is not null)
        {
            args.AddRange(["-l", User]);
        }

        args.AddRange(["-L", $"{localSocketPath}:{RemoteSocket}"]);

        // Terminate option parsing so the host can never be interpreted as an option.
        args.AddRange(["--", Host]);
        return args;
    }

    private static void ValidateToken(string variable, string value)
    {
        if (value.StartsWith('-') || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || value.Contains('@'))
        {
            throw new InvalidOperationException(
                $"{variable} has an invalid value '{value}'. It must not start with '-' or contain whitespace or '@' " +
                $"(use {UserVariable} for the user name).");
        }
    }

    private static int ParseInt(string variable, string? value, int defaultValue, int min, int max)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) || result < min || result > max)
        {
            throw new InvalidOperationException($"{variable} must be an integer between {min} and {max}, got '{value}'.");
        }

        return result;
    }

    private static DockerSshHostKeyChecking ParseHostKeyChecking(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "yes" or "strict" => DockerSshHostKeyChecking.Strict,
            "accept-new" => DockerSshHostKeyChecking.AcceptNew,
            "no" or "off" or "false" or "0" => throw new InvalidOperationException(
                $"{HostKeyCheckingVariable}={value} is not supported because it disables host key verification. " +
                $"Add the host key to a known_hosts file (see {KnownHostsFileVariable}) or use 'accept-new'."),
            _ => throw new InvalidOperationException(
                $"{HostKeyCheckingVariable} must be 'yes' or 'accept-new', got '{value}'."),
        };
}
