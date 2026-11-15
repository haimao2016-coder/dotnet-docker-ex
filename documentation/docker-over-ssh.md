# Controlling a Remote Docker Daemon over SSH

The image tests in [`tests/Microsoft.DotNet.Docker.Tests`](../tests/Microsoft.DotNet.Docker.Tests) drive Docker by running the `docker` CLI.
By default they use whatever Docker daemon the local CLI is configured for. This behavior is unchanged.

When `DOCKER_SSH_HOST` is set, the tests instead control a Docker daemon on another machine through SSH:

1. The test process starts one OpenSSH client for the whole run:
   `ssh -N -L <private local socket>:/var/run/docker.sock <host>`.
2. Every `docker` command the tests run gets `DOCKER_HOST=unix://<private local socket>`, so all existing test code (pull, build, run, inspect, cp, stop, rm, ...) works against the remote daemon without changes.
3. The tunnel is closed and the local socket directory is deleted when the test process exits.

Only `sshd` and the Docker daemon are needed on the remote host. The remote host does not need the Docker CLI, and no extra NuGet packages are used.

## Requirements

- Client: Linux or macOS with the OpenSSH client (`ssh`) and the Docker CLI. Windows clients are not supported because the tunnel forwards a Unix socket.
- Remote host: `sshd` with stream local forwarding allowed (`AllowStreamLocalForwarding yes`, the OpenSSH default), and an SSH user that can access the Docker socket (for example, a member of the `docker` group).

## Configuration

All settings are environment variables. No credentials are stored in the repository.

| Variable | Default | Description |
| --- | --- | --- |
| `DOCKER_SSH_HOST` | (unset: use local Docker) | Remote host name or IP address. Setting it enables SSH mode. Do not include `user@`; use `DOCKER_SSH_USER`. |
| `DOCKER_SSH_USER` | SSH default (`~/.ssh/config` or current user) | Remote user name. |
| `DOCKER_SSH_PORT` | `22` | SSH port (1-65535). |
| `DOCKER_SSH_IDENTITY_FILE` | SSH default keys / agent | Private key file. When set, only this key is offered (`IdentitiesOnly=yes`). |
| `DOCKER_SSH_KNOWN_HOSTS_FILE` | `~/.ssh/known_hosts` | known_hosts file used to verify the remote host key. |
| `DOCKER_SSH_STRICT_HOST_KEY_CHECKING` | `yes` | `yes`: the host key must already be in known_hosts. `accept-new`: unknown hosts are added on first use, changed keys are still rejected. `no` is refused. |
| `DOCKER_SSH_REMOTE_SOCKET` | `/var/run/docker.sock` | Docker socket path on the remote host (for example `/run/user/1000/docker.sock` for rootless Docker). |
| `DOCKER_SSH_CONNECT_TIMEOUT` | `10` | SSH connect timeout in seconds (1-600). |
| `DOCKER_SSH_EXECUTABLE` | `ssh` | Path to the OpenSSH client. |
| `DOCKER_SSH_PUBLISHED_PORT_HOST` | `DOCKER_SSH_HOST` | Host used by web tests to reach ports that containers publish on the remote host. |

`DOCKER_SSH_HOST` cannot be combined with `DOCKER_HOST` or `DOCKER_CONTEXT`; the tests fail fast if both are set so it is always clear which daemon is being controlled.

The SSH client always runs with `BatchMode=yes` (it never prompts for passwords or passphrases) and `ExitOnForwardFailure=yes`. Settings that are not listed above, such as `ProxyJump`, can be set in `~/.ssh/config`.

## Usage

Record the remote host key once, after verifying its fingerprint out of band:

``` console
ssh-keyscan -p 22 docker-host.example.com > ~/.ssh/docker-host.known_hosts
ssh-keygen -lf ~/.ssh/docker-host.known_hosts   # compare with the fingerprint shown on the remote host
```

Run the tests against the remote daemon:

``` console
export DOCKER_SSH_HOST=docker-host.example.com
export DOCKER_SSH_USER=ops
export DOCKER_SSH_IDENTITY_FILE=~/.ssh/id_ed25519
export DOCKER_SSH_KNOWN_HOSTS_FILE=~/.ssh/docker-host.known_hosts

./tests/run-tests.ps1 -PullImages -Paths '*10.0*noble*'
```

Check the SSH setup with the connectivity tests (unit tests plus one test that pulls, runs, lists, stops and removes a container on the remote daemon):

``` console
./tests/run-tests.ps1 -Version 10.0 -TestCategories docker-ssh
```

or directly:

``` console
cd tests/Microsoft.DotNet.Docker.Tests
dotnet test --filter "Category=docker-ssh"
```

The end-to-end test is skipped when `DOCKER_SSH_HOST` is not set. It uses `alpine:3.20` by default; set `DOCKER_SSH_TEST_IMAGE` to use another image.

## Security Notes

- Anyone who can use the Docker socket effectively has root on the remote host. Use a dedicated SSH user and key, and restrict that key on the server where possible (for example, `restrict,port-forwarding` in `authorized_keys`).
- Host key checking cannot be disabled. Prefer `yes` with a known_hosts file whose fingerprints you verified; use `accept-new` only on trusted networks.
- Keep private keys out of the repository and out of images. Provide them through a file path (`DOCKER_SSH_IDENTITY_FILE`), a mounted secret in CI, or `ssh-agent`.
- The forwarded local socket is created in a new temporary directory that only the current user can access (mode `0700`) and is removed when the tests finish.
- Arguments are passed to `ssh` as an argument list (no shell). Host and user values starting with `-` or containing whitespace are rejected.

## Limitations

- `tests/run-tests.ps1` skips pulling the ImageBuilder image and the local `docker version` check when `DOCKER_SSH_HOST` is set. Tests that call PowerShell scripts which run Docker themselves (the `pre-build` generated artifact tests) still use the local Docker configuration.
- `build-and-test.ps1 -Mode Build` builds images with the local Docker daemon. To test locally built images on a remote host, push them to a registry and use `-Registry`/`-RepoPrefix` with `-PullImages`.
- Bind mounts of local paths (for example, the YARP config file in `ReverseProxyBasicScenario`) refer to paths on the remote host and fail unless the same path exists there. Named volumes, `docker cp` and build contexts work, because the Docker CLI streams them over the tunnel.
- Web tests connect to published ports on `DOCKER_SSH_PUBLISHED_PORT_HOST`, so those ports must be reachable from the client (or set it to a host that forwards them). When tests use container IP addresses (Windows containers or `RUNNING_TESTS_IN_CONTAINER`), those addresses are usually not reachable from the client.
