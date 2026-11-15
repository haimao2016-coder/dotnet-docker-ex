// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.DotNet.Docker.Tests.DockerSsh;

/// <summary>
/// Runs only when <c>DOCKER_SSH_HOST</c> is set and exercises the real tunnel through <see cref="DockerHelper"/>.
/// </summary>
public sealed class DockerSshFactAttribute : FactAttribute
{
    public DockerSshFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DockerSshOptions.HostVariable)))
        {
            Skip = $"{DockerSshOptions.HostVariable} is not set.";
        }
    }
}

[Trait("Category", "docker-ssh")]
public class DockerSshEndToEndTests(ITestOutputHelper outputHelper)
{
    private const string ImageVariable = "DOCKER_SSH_TEST_IMAGE";
    private const string DefaultImage = "alpine:3.20";

    private readonly DockerHelper _dockerHelper = new(outputHelper);

    [DockerSshFact]
    public void CanRunListAndStopContainerOverSsh()
    {
        DockerHostConnection.IsRemote.ShouldBeTrue();
        outputHelper.WriteLine($"Docker daemon: {DockerHostConnection.SshOptions!.DisplayTarget}");

        string image = Environment.GetEnvironmentVariable(ImageVariable) is { Length: > 0 } custom ? custom : DefaultImage;
        string name = "docker-ssh-e2e-" + Guid.NewGuid().ToString("N")[..12];

        _dockerHelper.Pull(image);
        DockerHelper.ImageExists(image).ShouldBeTrue();

        try
        {
            _dockerHelper.Run(image, name, command: "sleep 300", detach: true, skipAutoCleanup: true);

            DockerHelper.ContainerExists(name).ShouldBeTrue();
            DockerHelper.ContainerIsRunning(name).ShouldBeTrue();

            string output = _dockerHelper.Run(image, name + "-echo", command: "echo hello-over-ssh");
            output.ShouldContain("hello-over-ssh");
        }
        finally
        {
            _dockerHelper.DeleteContainer(name);
        }

        DockerHelper.ContainerExists(name).ShouldBeFalse();
    }
}
