// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Microsoft.DotNet.Docker.Tests.DockerSsh;

[Trait("Category", "docker-ssh")]
public class DockerSshOptionsTests
{
    private static DockerSshOptions? Parse(Dictionary<string, string> variables, params string[] existingFiles) =>
        DockerSshOptions.FromVariables(
            name => variables.TryGetValue(name, out string? value) ? value : null,
            path => existingFiles.Contains(path));

    [Fact]
    public void ReturnsNullWhenHostIsNotSet()
    {
        Parse([]).ShouldBeNull();
        Parse(new() { [DockerSshOptions.HostVariable] = "   " }).ShouldBeNull();
    }

    [Fact]
    public void UsesSecureDefaults()
    {
        DockerSshOptions options = Parse(new() { [DockerSshOptions.HostVariable] = "docker.example.com" })!;

        options.Host.ShouldBe("docker.example.com");
        options.User.ShouldBeNull();
        options.Port.ShouldBe(22);
        options.HostKeyChecking.ShouldBe(DockerSshHostKeyChecking.Strict);
        options.RemoteSocket.ShouldBe("/var/run/docker.sock");
        options.ConnectTimeoutSeconds.ShouldBe(10);
        options.SshExecutable.ShouldBe("ssh");
        options.EffectivePublishedPortHost.ShouldBe("docker.example.com");
    }

    [Fact]
    public void ReadsAllSettings()
    {
        DockerSshOptions options = Parse(
            new()
            {
                [DockerSshOptions.HostVariable] = "10.0.0.5",
                [DockerSshOptions.UserVariable] = "ops",
                [DockerSshOptions.PortVariable] = "2222",
                [DockerSshOptions.IdentityFileVariable] = "/keys/id_ed25519",
                [DockerSshOptions.KnownHostsFileVariable] = "/keys/known_hosts",
                [DockerSshOptions.HostKeyCheckingVariable] = "accept-new",
                [DockerSshOptions.RemoteSocketVariable] = "/run/user/1000/docker.sock",
                [DockerSshOptions.ConnectTimeoutVariable] = "30",
                [DockerSshOptions.SshExecutableVariable] = "/usr/bin/ssh",
                [DockerSshOptions.PublishedPortHostVariable] = "docker-public.example.com",
            },
            "/keys/id_ed25519",
            "/keys/known_hosts")!;

        options.User.ShouldBe("ops");
        options.Port.ShouldBe(2222);
        options.IdentityFile.ShouldBe("/keys/id_ed25519");
        options.KnownHostsFile.ShouldBe("/keys/known_hosts");
        options.HostKeyChecking.ShouldBe(DockerSshHostKeyChecking.AcceptNew);
        options.RemoteSocket.ShouldBe("/run/user/1000/docker.sock");
        options.ConnectTimeoutSeconds.ShouldBe(30);
        options.SshExecutable.ShouldBe("/usr/bin/ssh");
        options.EffectivePublishedPortHost.ShouldBe("docker-public.example.com");
        options.DisplayTarget.ShouldBe("ssh://ops@10.0.0.5:2222/run/user/1000/docker.sock");
    }

    [Theory]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("NO")]
    public void RejectsDisabledHostKeyChecking(string value)
    {
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => Parse(new()
        {
            [DockerSshOptions.HostVariable] = "host",
            [DockerSshOptions.HostKeyCheckingVariable] = value,
        }));
        ex.Message.ShouldContain("disables host key verification");
    }

    [Theory]
    [InlineData(DockerSshOptions.HostVariable, "-oProxyCommand=evil")]
    [InlineData(DockerSshOptions.HostVariable, "user@host")]
    [InlineData(DockerSshOptions.HostVariable, "host name")]
    [InlineData(DockerSshOptions.UserVariable, "-lroot")]
    [InlineData(DockerSshOptions.PortVariable, "0")]
    [InlineData(DockerSshOptions.PortVariable, "65536")]
    [InlineData(DockerSshOptions.PortVariable, "22a")]
    [InlineData(DockerSshOptions.PortVariable, "-1")]
    [InlineData(DockerSshOptions.ConnectTimeoutVariable, "0")]
    [InlineData(DockerSshOptions.RemoteSocketVariable, "relative.sock")]
    [InlineData(DockerSshOptions.RemoteSocketVariable, "/var/run/a b.sock")]
    [InlineData(DockerSshOptions.RemoteSocketVariable, "/var/run/x:y")]
    [InlineData(DockerSshOptions.HostKeyCheckingVariable, "ask")]
    [InlineData(DockerSshOptions.IdentityFileVariable, "/missing/key")]
    [InlineData(DockerSshOptions.KnownHostsFileVariable, "/missing/known_hosts")]
    public void RejectsInvalidValues(string variable, string value)
    {
        Dictionary<string, string> variables = new() { [DockerSshOptions.HostVariable] = "host" };
        variables[variable] = value;

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => Parse(variables));
        ex.Message.ShouldContain(variable);
    }

    [Fact]
    public void BuildsMinimalTunnelArguments()
    {
        DockerSshOptions options = new() { Host = "docker.example.com" };

        IReadOnlyList<string> args = options.BuildTunnelArguments("/tmp/dkssh-1/docker.sock");

        args.ShouldBe(
        [
            "-N", "-T",
            "-o", "BatchMode=yes",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "StreamLocalBindUnlink=yes",
            "-p", "22",
            "-L", "/tmp/dkssh-1/docker.sock:/var/run/docker.sock",
            "--", "docker.example.com",
        ]);
    }

    [Fact]
    public void BuildsFullTunnelArguments()
    {
        DockerSshOptions options = new()
        {
            Host = "h",
            User = "ops",
            Port = 2222,
            IdentityFile = "/keys/id",
            KnownHostsFile = "/keys/known_hosts",
            HostKeyChecking = DockerSshHostKeyChecking.AcceptNew,
            RemoteSocket = "/run/docker.sock",
        };

        List<string> args = [.. options.BuildTunnelArguments("/tmp/s.sock")];

        args.ShouldContain("StrictHostKeyChecking=accept-new");
        args.ShouldNotContain("StrictHostKeyChecking=no");
        args[args.IndexOf("-i") + 1].ShouldBe("/keys/id");
        args.ShouldContain("IdentitiesOnly=yes");
        args.ShouldContain("UserKnownHostsFile=/keys/known_hosts");
        args[args.IndexOf("-l") + 1].ShouldBe("ops");
        args[args.IndexOf("-p") + 1].ShouldBe("2222");
        args[args.IndexOf("-L") + 1].ShouldBe("/tmp/s.sock:/run/docker.sock");
        args[^2].ShouldBe("--");
        args[^1].ShouldBe("h");
    }

    [Theory]
    [InlineData("DOCKER_HOST")]
    [InlineData("DOCKER_CONTEXT")]
    public void RejectsConflictingDockerHostSelection(string variable)
    {
        DockerSshOptions options = new() { Host = "h" };

        Should.Throw<InvalidOperationException>(() =>
            DockerHostConnection.ValidateNoConflicts(options, name => name == variable ? "something" : null));
    }

    [Fact]
    public void AllowsDockerHostWhenSshIsNotConfigured() =>
        DockerHostConnection.ValidateNoConflicts(null, _ => "tcp://localhost:2375");

    [Fact]
    public void TunnelReportsSshFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 'false' exits immediately with code 1, like ssh does when authentication or host key checks fail.
        DockerSshOptions options = new() { Host = "h", SshExecutable = "false" };

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => DockerSshTunnel.Start(options));
        ex.Message.ShouldContain("exited with code 1");
    }

    [Fact]
    public void TunnelReportsMissingSshClient()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        DockerSshOptions options = new() { Host = "h", SshExecutable = "/nonexistent/ssh-" + Guid.NewGuid().ToString("N") };

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => DockerSshTunnel.Start(options));
        ex.Message.ShouldContain(DockerSshOptions.SshExecutableVariable);
    }
}
