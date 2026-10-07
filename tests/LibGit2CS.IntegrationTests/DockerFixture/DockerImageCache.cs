using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

using Microsoft.Extensions.Logging;

namespace LibGit2CS.IntegrationTests.DockerFixture;

/// <summary>Prepares one persistent Docker image lazily and serializes concurrent first use.</summary>
internal class DockerImageCache(string kind, ILogger logger) : IAsyncDisposable
{
    private static readonly Action<ILogger, string, string, string, double, string, Exception?> s_logPreparation =
        LoggerMessage.Define<string, string, string, double, string>(LogLevel.Information, new EventId(1),
            "Docker image {Variant} {Image} preparation {Result} in {DurationMs} ms ({Mode})");
    private readonly SemaphoreSlim _preparationLock = new(1, 1);
    private IFutureDockerImage? _image;

    internal static string GetImageName(string kind, string dockerfile, string builderVersion)
    {
        const string cacheFormatVersion = "1";
        byte[] identity = Encoding.UTF8.GetBytes(
            cacheFormatVersion + "\n" + builderVersion + "\n" + dockerfile.ReplaceLineEndings("\n"));
        return "localhost/libgit2cs-tests/" + kind + ":" + Convert.ToHexStringLower(SHA256.HashData(identity));
    }

    internal static ImageFromDockerfileBuilder ConfigureImage(ImageFromDockerfileBuilder builder, bool refresh)
    {
        builder = builder
            .WithImageBuildPolicy(refresh ? PullPolicy.Always : PullPolicy.Missing)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .WithLabel("org.testcontainers.session-id", Guid.Empty.ToString("D"))
            .WithLabel("org.libgit2cs.integration-image", "true");
        return refresh
            ? builder.WithCreateParameterModifier(parameters =>
            {
                parameters.NoCache = true;
                parameters.Pull = "true";
            })
            : builder;
    }

    internal async Task<IFutureDockerImage> EnsureImageAsync(string dockerfile, CancellationToken ct)
    {
        await _preparationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_image is not null)
            {
                return _image;
            }

            string version = typeof(ImageFromDockerfileBuilder).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            string name = GetImageName(kind, dockerfile, version);
            bool refresh = Environment.GetEnvironmentVariable("LIBGIT2CS_TEST_IMAGE_REFRESH") == "1";
            long started = Stopwatch.GetTimestamp();
            string tempDir = Path.Combine(Path.GetTempPath(), "libgit2cs-image-" + Guid.NewGuid().ToString("N"));
            IFutureDockerImage? image = null;
            try
            {
                Directory.CreateDirectory(tempDir);
                await File.WriteAllTextAsync(Path.Combine(tempDir, "Dockerfile"), dockerfile, new UTF8Encoding(false), ct).ConfigureAwait(false);
                image = BuildImage(ConfigureImage(new ImageFromDockerfileBuilder(), refresh)
                    .WithName(name)
                    .WithDockerfile("Dockerfile")
                    .WithDockerfileDirectory(tempDir)
                    .WithLogger(logger));
                await CreateImageAsync(image, ct).ConfigureAwait(false);
                _image = image;
                return image;
            }
            finally
            {
                try
                {
                    if (image is not null && _image is null)
                    {
                        await image.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                    s_logPreparation(logger,
                        kind, name, _image is null ? "failed" : "succeeded",
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, refresh ? "refresh" : "normal", null);
                }
            }
        }
        finally
        {
            _preparationLock.Release();
        }
    }

    internal virtual IFutureDockerImage BuildImage(ImageFromDockerfileBuilder builder) => builder.Build();

    internal virtual Task CreateImageAsync(IFutureDockerImage image, CancellationToken ct) => image.CreateAsync(ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_image is not null)
            {
                await _image.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _preparationLock.Dispose();
        }
    }
}
