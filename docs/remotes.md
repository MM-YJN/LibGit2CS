# Remotes and authentication

See [getting started](getting-started.md) for cloning. Fetch downloads objects and
updates configured tracking refs; it does **not** merge changes into your working
branch. Push updates remote refs and can fail or reject individual updates.

## Fetch and push

Use a scratch clone with an `origin` remote. Pass the clone path and the full local
branch ref to publish, for example `refs/heads/main`. This example fetches first,
then pushes that local branch to the same remote branch name without force.

```csharp
using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitRepository.OpenAsync(args[0], context, ct);
await using var remote = await repo.RemoteLookupAsync("origin", ct);
await remote.FetchAsync(cancellationToken: ct);
GitPushResult result = await remote.PushAsync([$"{args[1]}:{args[1]}"], cancellationToken: ct);
if (!result.UnpackOk)
    throw new InvalidOperationException("Remote could not unpack the push.");
foreach (var status in result.Status)
{
    Console.WriteLine($"{status.Ref}: {(status.Ok ? "OK" : status.Message)}");
    if (!status.Ok)
        throw new InvalidOperationException($"Push rejected: {status.Ref}: {status.Message}");
}
```

For local testing, push to a separate **bare** repository. Network access,
authentication, and server policy can all cause failure. Inspect `UnpackOk` and
per-ref status rather than assuming an awaited push means every ref succeeded.

## Credential callbacks

For HTTP, `GitRemoteCallbacks.Credentials` receives allowed credential types,
suggested username, URL, and cancellation token, in that order. SSH currently
supplies URL before suggested username. Return a supported credential or null;
the transport may invoke it more than once. Applications should bound retries when
credentials are rejected. This helper class creates callbacks from application
configuration; it does not read or embed secrets itself.

```csharp
using LibGit2CS.Remote;

public static class ExampleCredentials
{
    public static GitRemoteCallbacks Https(Uri trustedOrigin, string username, string token) => new()
    {
        Credentials = (allowed, suggestedUsername, url, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            bool trusted = Uri.TryCreate(url, UriKind.Absolute, out Uri? destination)
                && string.Equals(destination.Scheme, trustedOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(destination.IdnHost, trustedOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
                && destination.Port == trustedOrigin.Port;
            GitCredential? credential = trusted && (allowed & GitCredentialType.UserPassPlaintext) != 0
                ? new GitUserPassCredential(username, token) : null;
            return Task.FromResult(credential);
        },
    };

    public static GitRemoteCallbacks Ssh(string username, string? privateKeyPath,
        string? passphrase = null) => new()
    {
        Credentials = (allowed, url, suggestedUsername, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            GitCredential? credential = null;
            if ((allowed & GitCredentialType.SshKey) != 0)
                credential = privateKeyPath is null
                    ? new GitSshAgentCredential(username)
                    : new GitSshKeyCredential(username, null, privateKeyPath, passphrase);
            else if ((allowed & GitCredentialType.Username) != 0)
                credential = new GitUsernameCredential(username);
            return Task.FromResult(credential);
        },
    };
}
```

Supply these callbacks through `GitFetchOptions.RemoteCallbacks` or
`GitPushOptions.RemoteCallbacks`; for cloning, set `GitCloneOptions.FetchOptions`
with the configured fetch options. For example, this separate program uses the
helper above and application-provided environment variables to fetch over HTTPS:

```csharp
using LibGit2CS.Core;
using LibGit2CS.Remote;
using LibGit2CS.Repository;

string username = Environment.GetEnvironmentVariable("GIT_EXAMPLE_USERNAME")
    ?? throw new InvalidOperationException("Set GIT_EXAMPLE_USERNAME.");
string token = Environment.GetEnvironmentVariable("GIT_EXAMPLE_TOKEN")
    ?? throw new InvalidOperationException("Set GIT_EXAMPLE_TOKEN.");
// Configure the origin authorized to receive this token independently of redirects.
var trustedOrigin = new Uri(Environment.GetEnvironmentVariable("GIT_EXAMPLE_TRUSTED_ORIGIN")
    ?? throw new InvalidOperationException("Set GIT_EXAMPLE_TRUSTED_ORIGIN, e.g. https://git.example.com."));
using var context = new GitContext();
CancellationToken ct = CancellationToken.None;
await using var repo = await GitRepository.OpenAsync(args[0], context, ct);
await using var remote = await repo.RemoteLookupAsync("origin", ct);
await remote.FetchAsync(options: new GitFetchOptions
{
    RemoteCallbacks = ExampleCredentials.Https(trustedOrigin, username, token),
}, cancellationToken: ct);
```

The HTTPS helper returns credentials only for the configured origin; the SSH
helper assumes it is used for the intended trusted remote. Neither implements
a multi-host credential store. For asynchronous secret stores, use an
async callback and propagate its token to the lookup. Do not put tokens into URLs
or progress logs. `SidebandProgress` and `TransferProgress` accept `IProgress<T>`
implementations for reporting progress.

## HTTP redirects and credentials

An origin is the scheme, host, and effective port. Same-origin redirects retain
HTTP authentication. A redirect changing any of these clears server credentials
before the first request to the destination. If that destination challenges,
the credential callback receives its URL and must decide whether to provide
credentials. After an origin change, URL-embedded credentials are not reused
for the rest of the connection, even if a redirect returns to the original origin.
Proxy authentication remains separate.

`CustomHeaders` on connect, fetch, and push options are sent only to the
connection's original origin. This includes all custom headers, such as
Authorization, Cookie, and API keys, on retries and subsequent service requests.
They are sent again if a redirect returns to the original origin. This is
intentional hardening beyond libgit2 1.9.4, which forwards custom headers.

Leading-slash redirect locations follow libgit2 semantics: even
`//other.example/path` is a path on the existing host, not a change of host.
HTTPS-to-HTTP redirects remain rejected.

## TLS and SSH host keys

Keep certificate validation enabled. Leaving `CertificateCheck` unset uses
transport validation; do not use an unconditional accepting callback to make an
example connect. SSH reads known-hosts configuration and rejects unknown or
mismatched keys unless the application deliberately provides a trust decision.
Provision known hosts through your application's deployment process. An SSH agent
credential requires a reachable agent with an appropriate key loaded; a key-file
credential requires a readable private key and its passphrase when encrypted.
