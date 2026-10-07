// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

using System.Security.Cryptography;

using LibGit2CS.Core;
using LibGit2CS.Core.Hashing;

namespace LibGit2CS.Objects;

/// <summary>
/// Incremental hash helper for streaming object writes. Wraps the BCL
/// <see cref="SHA1"/>/<see cref="SHA256"/>
/// incremental hash providers.
/// </summary>
internal sealed class GitIncrementalHash : IDisposable
{
    private readonly IncrementalHash _inner;
    private readonly GitHashAlgorithmKind _algorithm;

    private GitIncrementalHash(IncrementalHash inner, GitHashAlgorithmKind algorithm)
    {
        _inner = inner;
        _algorithm = algorithm;
    }

    public static GitIncrementalHash Create(GitHashAlgorithmKind algorithm)
    {
        HashAlgorithmName hashAlgorithm = algorithm == GitHashAlgorithmKind.Sha256
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA1;
        return new GitIncrementalHash(
            IncrementalHash.CreateHash(hashAlgorithm),
            algorithm);
    }

    public void AppendData(ReadOnlySpan<byte> data) => _inner.AppendData(data);

    public GitOid Finalize()
    {
        int hashSize = GitOid.SizeFor(_algorithm);
        Span<byte> result = stackalloc byte[hashSize];
        if (!_inner.TryGetHashAndReset(result, out int written) || written != hashSize)
        {
            throw new GitException(
                GitErrorCode.Error,
                $"incremental hash finalization failed (expected {hashSize} bytes, got {written})",
                GitErrorCategory.Odb);
        }

        return GitOid.FromRaw(result, _algorithm);
    }

    public void Dispose() => _inner.Dispose();
}
