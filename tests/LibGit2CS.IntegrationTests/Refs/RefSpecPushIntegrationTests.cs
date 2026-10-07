using LibGit2CS.Core;
using LibGit2CS.Refs;

namespace LibGit2CS.IntegrationTests.Refs;

/// <summary>
/// Integration tests for <see cref="GitRefSpec.Parse"/> on the push side
/// (<c>isFetch: false</c>): the push-no-dst copy branch, the push-empty-dst
/// throw, the push-invalid-name throw, and a push-side transform.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> The unit <c>RefSpecTests</c> cover the fetch
/// side and the <c>:</c>/<c>+:</c> matching forms; these push-side cases
/// exercise the no-colon copy, empty-RHS throw, and invalid-name throw
/// through the same public <see cref="GitRefSpec.Parse"/> entry point. They
/// live with the integration suite so an end-to-end run exercises them.
/// </para>
/// <para>
/// <b>Libgit2 counterparts.</b> Mirrors scenarios from
/// <c>tests/libgit2/network/remote/refspecs.c</c>
/// (<c>test_remote_refspecs__parsing</c>).
/// </para>
/// <para>
/// <b>Not Docker-gated.</b> Runs on every build.
/// </para>
/// </remarks>
public sealed class RefSpecPushIntegrationTests
{
    // ── Push no-dst → copy LHS to RHS ──────────────────────────────────

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/>(<c>"refs/heads/main"</c>,
    /// <c>isFetch: false</c>) — a push refspec without a colon copies the LHS
    /// into the RHS. Exercises the <c>dst is null</c> branch. The resulting
    /// refspec is non-wildcard, non-matching, has <c>Source == Destination</c>,
    /// and transforms identity.
    /// </summary>
    [Fact]
    public void Parse_Push_NoColon_CopiesLhsToRhs()
    {
        var spec = GitRefSpec.Parse("refs/heads/main", isFetch: false);

        Assert.Equal("refs/heads/main", spec.Source);
        Assert.Equal("refs/heads/main", spec.Destination);
        Assert.False(spec.IsWildcard);
        Assert.False(spec.IsMatching);
        Assert.False(spec.IsFetch);
        Assert.False(spec.Force);
        Assert.False(spec.IsNegative);

        // Transform identity: src → dst where both are the same literal.
        Assert.Equal("refs/heads/main", spec.Transform("refs/heads/main"));
    }

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/>(<c>"+refs/heads/main"</c>,
    /// <c>isFetch: false</c>) — force push without a colon. The force flag is
    /// captured and the LHS still copies to RHS.
    /// </summary>
    [Fact]
    public void Parse_Push_ForceNoColon_CopiesLhsToRhs_SetsForce()
    {
        var spec = GitRefSpec.Parse("+refs/heads/main", isFetch: false);

        Assert.True(spec.Force);
        Assert.Equal("refs/heads/main", spec.Source);
        Assert.Equal("refs/heads/main", spec.Destination);
    }

    // ── Push empty dst → throws ────────────────────────────────────────

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/>(<c>"refs/heads/main:"</c>,
    /// <c>isFetch: false</c>) — push refspec with an explicit empty RHS is
    /// invalid. Exercises the <c>dst.Length == 0</c> throw.
    /// </summary>
    [Fact]
    public void Parse_Push_EmptyDst_ThrowsInvalidSpec()
    {
        GitException ex = Assert.Throws<GitException>(
            () => GitRefSpec.Parse("refs/heads/main:", isFetch: false));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    // ── Push invalid RHS name → throws ─────────────────────────────────

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/> with an invalid RHS ref name
    /// throws <see cref="GitErrorCode.InvalidSpec"/>. Exercises the push-side
    /// name-validation branch.
    /// <c>refs/heads/main:..</c> — the destination <c>..</c> is not a valid
    /// refname (no double-dot sequences allowed).
    /// </summary>
    [Fact]
    public void Parse_Push_InvalidDstName_ThrowsInvalidSpec()
    {
        GitException ex = Assert.Throws<GitException>(
            () => GitRefSpec.Parse("refs/heads/main:..", isFetch: false));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }

    // ── Push with wildcard + transform ─────────────────────────────────

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/>(<c>"refs/heads/*:refs/heads/*"</c>,
    /// <c>isFetch: false</c>) — push wildcard refspec.
    /// <see cref="GitRefSpec.Transform"/> substitutes the wildcard capture
    /// group from source into destination. Exercises
    /// <see cref="GitRefSpec.WildcardSubstitute"/> via the push path.
    /// </summary>
    [Fact]
    public void Parse_Push_Wildcard_Transforms()
    {
        var spec = GitRefSpec.Parse("refs/heads/*:refs/heads/*", isFetch: false);

        Assert.True(spec.IsWildcard);
        Assert.Equal("refs/heads/*", spec.Source);
        Assert.Equal("refs/heads/*", spec.Destination);

        Assert.Equal("refs/heads/feature", spec.Transform("refs/heads/feature"));

        // Reverse transform on the push side.
        Assert.Equal("refs/heads/feature", spec.ReverseTransform("refs/heads/feature"));
    }

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/>(<c>"refs/heads/*:refs/remotes/origin/*"</c>,
    /// <c>isFetch: false</c>) — push wildcard refspec with a different RHS
    /// prefix. The transform substitutes the captured wildcard into the
    /// destination prefix.
    /// </summary>
    [Fact]
    public void Parse_Push_WildcardDifferentPrefix_Transforms()
    {
        var spec = GitRefSpec.Parse("refs/heads/*:refs/remotes/origin/*", isFetch: false);

        Assert.Equal("refs/remotes/origin/feature", spec.Transform("refs/heads/feature"));
    }

    // ── Push LHS-only glob with non-glob RHS → throws ──────────────────

    /// <summary>
    /// <see cref="GitRefSpec.Parse"/>(<c>"refs/heads/*:refs/heads/main"</c>,
    /// <c>isFetch: false</c>) — LHS has a glob but RHS does not. This is
    /// invalid: a wildcard on one side requires a wildcard on the other.
    /// Exercises the wildcarded-LHS/non-wildcard-RHS rejection from the
    /// push parse path.
    /// </summary>
    [Fact]
    public void Parse_Push_LhsGlobNoRhsGlob_ThrowsInvalidSpec()
    {
        GitException ex = Assert.Throws<GitException>(
            () => GitRefSpec.Parse("refs/heads/*:refs/heads/main", isFetch: false));
        Assert.Equal(GitErrorCode.InvalidSpec, ex.Code);
    }
}
