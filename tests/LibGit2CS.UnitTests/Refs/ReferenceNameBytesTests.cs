using System.Text;

using LibGit2CS.Core;
using LibGit2CS.Objects;
using LibGit2CS.Refs;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Refs;

public sealed class ReferenceNameBytesTests : IAsyncLifetime
{
    private readonly GitContext _context = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LibGit2CS_ref_bytes_" + Guid.NewGuid().ToString("N"));
    private GitRepository _repo = null!;
    private GitOid _first;
    private GitOid _second;
    private static byte[] FirstName => [.. "refs/heads/raw_"u8, 0xfe];
    private static byte[] SecondName => [.. "refs/heads/raw_"u8, 0xff];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string PackedPath => Path.Combine(_repo.Path, "packed-refs");

    public async ValueTask InitializeAsync()
    {
        _repo = await GitRepository.InitAsync(_directory, isBare: true, _context, Token);
        _first = await _repo.ObjectWriteAsync(GitObjectType.Blob, "first"u8.ToArray(), Token);
        _second = await _repo.ObjectWriteAsync(GitObjectType.Blob, "second"u8.ToArray(), Token);
        await File.WriteAllBytesAsync(PackedPath,
            [.. "# pack-refs with: sorted\n"u8,
             .. Encoding.UTF8.GetBytes(_first.ToString()), (byte)' ', .. FirstName, (byte)'\n',
             .. Encoding.UTF8.GetBytes(_second.ToString()), (byte)' ', .. SecondName, (byte)'\n'], Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        _context.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task LookupAndGlob_DistinguishNamesWithIdenticalDisplay()
    {
        GitReference first = (await _repo.ReferenceLookupAsync(FirstName, Token))!;
        GitReference second = (await _repo.ReferenceLookupAsync(SecondName, Token))!;
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(FirstName, first.NameBytes.ToArray());
        Assert.Equal(SecondName, second.NameBytes.ToArray());
        Assert.Equal(_first, Assert.IsType<GitDirectReference>(first).Target);
        Assert.Equal(_second, Assert.IsType<GitDirectReference>(second).Target);
        Assert.Null(await _repo.ReferenceLookupAsync(first.Name, Token));

        var exact = new List<GitReference>();
        await foreach (GitReference reference in _repo.ReferenceListBytesAsync(FirstName, Token))
        {
            exact.Add(reference);
        }
        Assert.Equal(FirstName, Assert.Single(exact).NameBytes.ToArray());
        var names = new List<byte[]>();
        await foreach (ReadOnlyMemory<byte> name in _repo.ReferenceListNamesBytesAsync("refs/heads/raw_?"u8.ToArray(), Token))
        {
            names.Add(name.ToArray());
        }
        Assert.Equal(2, names.Count);
        Assert.Equal(FirstName, names[0]);
        Assert.Equal(SecondName, names[1]);
    }

    [Fact]
    public async Task SymbolicTargets_PreserveBytesThroughCreateResolveAndCompareAndSwap()
    {
        GitSymbolicReference symbolic = Assert.IsType<GitSymbolicReference>(await _repo.ReferenceCreateSymbolicAsync(
            "HEAD"u8.ToArray(), FirstName, force: true, cancellationToken: Token));
        Assert.Equal(FirstName, symbolic.TargetNameBytes.ToArray());
        byte[] headBytes = await File.ReadAllBytesAsync(Path.Combine(_repo.Path, "HEAD"), Token);
        Assert.Equal((byte[])[.. "ref: "u8, .. FirstName, (byte)'\n'], headBytes);
        Assert.Equal(_first, Assert.IsType<GitDirectReference>(await symbolic.TargetAsync(Token)).Target);
        Assert.Equal(_first, Assert.IsType<GitDirectReference>(await _repo.HeadAsync(Token)).Target);
        GitReference first = (await _repo.ReferenceLookupAsync(FirstName, Token))!;
        GitReference second = (await _repo.ReferenceLookupAsync(SecondName, Token))!;
        Assert.True(await first.IsHeadAsync(Token));
        Assert.False(await second.IsHeadAsync(Token));

        await _repo.ReferenceSetSymbolicTargetAsync(symbolic, SecondName, cancellationToken: Token);
        GitException error = await Assert.ThrowsAsync<GitException>(() =>
            _repo.ReferenceSetSymbolicTargetAsync(symbolic, FirstName, cancellationToken: Token));
        Assert.Equal(GitErrorCode.Modified, error.Code);
        Assert.Equal(_second, Assert.IsType<GitDirectReference>(await _repo.HeadAsync(Token)).Target);
    }

    [Fact]
    public async Task UnsupportedFilesystemNames_CannotMutateReplacementNamedReference()
    {
        string display = Encoding.UTF8.GetString(FirstName);
        GitReference replacement = await _repo.ReferenceCreateAsync(display, _second, cancellationToken: Token);
        byte[] packedBefore = await File.ReadAllBytesAsync(PackedPath, Token);
        GitReference raw = (await _repo.ReferenceLookupAsync(FirstName, Token))!;
        byte[] missingRaw = [.. "refs/heads/missing_"u8, 0xff];
        GitException lookupError = await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceLookupAsync(missingRaw, Token));
        Assert.Equal(GitErrorCode.InvalidSpec, lookupError.Code);
        await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceDeleteAsync(raw, Token));
        await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceDeleteAsync(FirstName, Token));
        await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceCreateAsync(FirstName, _first, force: true, cancellationToken: Token));
        await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceRenameAsync(raw, "refs/heads/other", cancellationToken: Token));
        await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceRenameAsync(replacement, FirstName, cancellationToken: Token));
        await Assert.ThrowsAsync<GitException>(() => _repo.ReferenceReadLogAsync(FirstName, Token));
        await using GitTransaction transaction = _repo.NewReferenceTransaction();
        Assert.Throws<GitException>(() => transaction.LockRef(FirstName));
        Assert.Equal(_second, Assert.IsType<GitDirectReference>(await _repo.ReferenceLookupAsync(display, Token)).Target);
        Assert.Equal(packedBefore, await File.ReadAllBytesAsync(PackedPath, Token));
        Assert.Empty(Directory.EnumerateFiles(_repo.Path, "*.lock", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Transaction_CopiesNamesAndSymbolicTargets()
    {
        byte[] name = "refs/heads/symbolic"u8.ToArray();
        byte[] target = FirstName;
        await using (GitTransaction transaction = _repo.NewReferenceTransaction())
        {
            transaction.LockRef(name);
            transaction.SetSymbolicTarget(name, target);
            name[^1] = (byte)'X';
            target[^1] = 0xff;
            await transaction.CommitAsync(Token);
        }
        GitSymbolicReference symbolic = Assert.IsType<GitSymbolicReference>(await _repo.ReferenceLookupAsync("refs/heads/symbolic", Token));
        Assert.Equal(FirstName, symbolic.TargetNameBytes.ToArray());
        Assert.Equal(_first, Assert.IsType<GitDirectReference>(await _repo.ReferenceResolveAsync("refs/heads/symbolic", Token)).Target);
    }

    [Fact]
    public async Task PackingAndValidRenameDelete_PreserveUnrelatedRawNames()
    {
        GitReference valid = await _repo.ReferenceCreateAsync("refs/heads/valid"u8.ToArray(), _first, cancellationToken: Token);
        GitReference renamed = await _repo.ReferenceRenameAsync(valid, "refs/heads/renamed"u8.ToArray(), cancellationToken: Token);
        await _repo.PackRefsAsync(Token);
        await _repo.ReferenceDeleteAsync(renamed, Token);
        Assert.Equal(_first, Assert.IsType<GitDirectReference>(await _repo.ReferenceLookupAsync(FirstName, Token)).Target);
        Assert.Equal(_second, Assert.IsType<GitDirectReference>(await _repo.ReferenceLookupAsync(SecondName, Token)).Target);
        byte[] packed = await File.ReadAllBytesAsync(PackedPath, Token);
        Assert.True(packed.AsSpan().IndexOf(FirstName) >= 0);
        Assert.True(packed.AsSpan().IndexOf(SecondName) >= 0);
        Assert.Null(await _repo.ReferenceLookupAsync("refs/heads/renamed", Token));
    }

    [Fact]
    public async Task RevwalkGlob_ResolvesRawNamesWithoutDisplayRoundTrip()
    {
        GitOid tree = await _repo.ObjectWriteAsync(GitObjectType.Tree, Array.Empty<byte>(), Token);
        var signature = new GitSignature("Test", "test@example.com", new GitTime(1700000000, 0));
        GitOid commit = await _repo.CommitCreateAsync(new CommitCreateOptions
        {
            Tree = tree,
            Author = signature,
            Committer = signature,
            Message = "raw ref"
        }, Token);
        await File.WriteAllBytesAsync(PackedPath,
            [.. Encoding.UTF8.GetBytes(commit.ToString()), (byte)' ', .. FirstName, (byte)'\n'], Token);
        using LibGit2CS.Revwalk.GitRevWalker walker = _repo.NewRevWalker();
        await walker.PushGlobAsync("refs/heads/raw_*", Token);
        var found = new List<GitOid>();
        await foreach (GitOid oid in walker.WalkAsync(Token))
        {
            found.Add(oid);
        }
        Assert.Equal(commit, Assert.Single(found));
    }

    [Fact]
    public async Task CachedEnumeration_HonorsEnumeratorCancellation()
    {
        Assert.NotNull(await _repo.ReferenceLookupAsync(FirstName, Token));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator = _repo.ReferenceListNamesBytesAsync(cancellationToken: Token)
            .GetAsyncEnumerator(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await enumerator.MoveNextAsync(); });
    }

    [Fact]
    public void Snapshots_CopyBuffersAndCompareByteContents()
    {
        byte[] name = FirstName;
        byte[] target = SecondName;
        var reference = new GitSymbolicReference { NameBytes = name, TargetNameBytes = target };
        var reflog = new GitRefLog(name, _repo.ObjectFormat);
        Assert.Equal(0, reflog.EntryCount);
        var equal = new GitSymbolicReference { NameBytes = FirstName, TargetNameBytes = SecondName };
        Assert.Equal(reference, equal);
        Assert.Equal(reference.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(reference, reference with { NameBytes = SecondName });
        Assert.NotEqual(reference, reference with { TargetNameBytes = FirstName });
        name[^1] = 0;
        target[^1] = 0;
        Assert.Equal(FirstName, reference.NameBytes.ToArray());
        Assert.Equal(SecondName, reference.TargetNameBytes.ToArray());
        Assert.Equal(FirstName, reflog.RefNameBytes.ToArray());
        Assert.Equal(FirstName, GitReferences.NormalizeName((ReadOnlyMemory<byte>)FirstName)!.Value.ToArray());
        Assert.True(GitReferences.IsNameValid(FirstName.AsSpan()));
        Assert.False(GitReferences.IsNameValid("refs/heads/../bad"u8));
    }
}
