using LibGit2CS.Transports;

using LibSsh2CS;

namespace LibGit2CS.IntegrationTests.Transports.TestInfrastructure;

/// <summary>
/// Test-only <see cref="ISshSessionFactory"/> that captures the underlying
/// <see cref="SshSession"/> created for each connection. Used by
/// <c>SshTransportDockerTests.Rekey_UnderLoad_Succeeds</c> to assert
/// <see cref="SshSession.RekeyCount"/> after a fetch with a tiny
/// <see cref="RekeyPolicy"/> threshold.
/// </summary>
internal sealed class CapturingSshSessionFactory : ISshSessionFactory
{
    private readonly RekeyPolicy? _rekeyPolicy;

    /// <summary>The last <see cref="SshSession"/> created by this factory, or <c>null</c>.</summary>
    public SshSession? LastSession { get; private set; }

    public CapturingSshSessionFactory()
    {
    }

    public CapturingSshSessionFactory(RekeyPolicy? rekeyPolicy)
    {
        _rekeyPolicy = rekeyPolicy;
    }

    /// <inheritdoc/>
    public ISshSession Create()
    {
        var session = new SshSession();
        if (_rekeyPolicy is not null)
        {
            session.RekeyPolicy = _rekeyPolicy;
        }

        LastSession = session;
        return new SshSessionAdapter(session);
    }
}
