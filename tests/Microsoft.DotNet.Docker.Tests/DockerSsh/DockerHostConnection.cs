// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;

namespace Microsoft.DotNet.Docker.Tests.DockerSsh;

/// <summary>
/// Decides which Docker daemon the tests talk to. By default this is whatever the Docker CLI uses locally
/// (unchanged behavior). When <see cref="DockerSshOptions.HostVariable"/> is set, a single SSH tunnel is
/// opened for the test run and every <c>docker</c> process is pointed at it.
/// </summary>
public static class DockerHostConnection
{
    private static readonly Lazy<DockerSshOptions?> LazyOptions = new(DockerSshOptions.FromEnvironment);
    private static readonly Lazy<DockerSshTunnel?> LazyTunnel = new(StartTunnel);

    /// <summary>SSH options in effect, or <c>null</c> when the local Docker daemon is used.</summary>
    public static DockerSshOptions? SshOptions => LazyOptions.Value;

    public static bool IsRemote => SshOptions is not null;

    /// <summary>
    /// Environment variables to set on every <c>docker</c> process, or <c>null</c> to inherit the
    /// current environment unchanged.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? DockerEnvironment
    {
        get
        {
            DockerSshTunnel? tunnel = LazyTunnel.Value;
            if (tunnel is null)
            {
                return null;
            }

            if (!tunnel.IsRunning)
            {
                throw new InvalidOperationException(
                    $"The SSH tunnel to {SshOptions!.DisplayTarget} is no longer running.{Environment.NewLine}" +
                    $"ssh stderr: {tunnel.GetStandardError()}");
            }

            return new Dictionary<string, string> { ["DOCKER_HOST"] = tunnel.DockerHost };
        }
    }

    /// <summary>
    /// Host name to use when connecting to a port that a container published on the Docker host.
    /// </summary>
    public static string PublishedPortHost => SshOptions?.EffectivePublishedPortHost ?? "localhost";

    /// <summary>
    /// Fails if SSH settings conflict with other ways of selecting a Docker daemon.
    /// </summary>
    public static void ValidateNoConflicts(DockerSshOptions? options, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        if (options is null)
        {
            return;
        }

        foreach (string variable in new[] { "DOCKER_HOST", "DOCKER_CONTEXT" })
        {
            if (!string.IsNullOrWhiteSpace(getVariable(variable)))
            {
                throw new InvalidOperationException(
                    $"{DockerSshOptions.HostVariable} and {variable} are both set. Unset one of them so it is clear " +
                    "which Docker daemon the tests should control.");
            }
        }
    }

    private static DockerSshTunnel? StartTunnel()
    {
        DockerSshOptions? options = SshOptions;
        ValidateNoConflicts(options, Environment.GetEnvironmentVariable);
        if (options is null)
        {
            return null;
        }

        DockerSshTunnel tunnel = DockerSshTunnel.Start(options, Console.Error.WriteLine);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tunnel.Dispose();
        return tunnel;
    }
}
