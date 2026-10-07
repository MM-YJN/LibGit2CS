using System.Reflection;

using LibGit2CS.Core;
using LibGit2CS.Diff;
using LibGit2CS.Repository;

namespace LibGit2CS.UnitTests.Diff;

/// <summary> Parity tests for diff/patch/blame: the hashsig heap sift-down reads the stale right slot, and ComputeDiffable ports the
/// size/OID identity check and the unconditional UNMODIFIED short-circuit. Expectations are C-verified against libgit2 1.9.4 (hashsig.c:89-105;
/// patch_generate.c:178-205). </summary>
public sealed class DiffLowParityTests : IDisposable
{
    private readonly string _tempDir;

    public DiffLowParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LibGit2CS_DiffLow2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    // ── hashsig_heap_down reads the stale right slot ───────────

    [Fact]
    public void HashHeap_SiftDown_ReadsStaleRightSlot()
    {
        // C (hashsig.c:89-105): hashsig_heap_down reads the right child UNCONDITIONALLY — when the sift reaches el = Size/2 - 1 the right child index equals
        // Size and the STALE slot participates in the comparison. Guarding with `right < Size` would skip the stale slot and diverge the heap contents.
        Type heapType = typeof(SimilarityHash).GetNestedType("HashHeap", BindingFlags.NonPublic)!;
        ConstructorInfo ctor = heapType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            .Single(c => c.GetParameters().Length == 1);
        object heap = ctor.Invoke([true])!;

        // Crafted state: Size = 126 with the sift walk forced down the right
        // spine (0 → 2 → 6 → 14 → 30 → 62). At el = 62 the children are
        // 125/126 where 126 == Size — the stale slot (value 5) is SMALLER
        // than the left child (60), so C swaps the stale value up; the
        // guarded port picked the left child.
        uint[] values = new uint[127];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (uint)(i + 1);
        }

        values[0] = 200;   // the value being sifted (larger than every child)
        values[1] = 100;
        values[2] = 1;   // right child smaller at el = 0
        values[5] = 90;
        values[6] = 2;    // el = 2
        values[13] = 80;
        values[14] = 3;  // el = 6
        values[29] = 70;
        values[30] = 4;  // el = 14
        values[61] = 60;
        values[62] = 5;  // el = 30
        values[125] = 60;
        values[126] = 5; // el = 62: stale (126) < left (125)

        FieldInfo valuesField = heapType.GetField("_values", BindingFlags.NonPublic | BindingFlags.Instance)!;
        valuesField.SetValue(heap, values);
        PropertyInfo sizeProp = heapType.GetProperty("Size")!;
        sizeProp.SetValue(heap, 126);
        MethodInfo siftDown = heapType.GetMethod("SiftDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        siftDown.Invoke(heap, [0]);

        // C: the stale slot's value (5) was swapped into slot 62 and the
        // sifted value (200) landed in the stale slot 126.
        uint[] result = (uint[])valuesField.GetValue(heap)!;
        Assert.Equal(5u, result[62]);
        Assert.Equal(200u, result[126]);
    }

    // ── ComputeDiffable ports the size/OID identity check ──────

    [Fact]
    public async Task Diffable_UnmodifiedDelta_ShortCircuitsEvenWithIncludeUnmodified()
    {
        // C (patch_generate.c:181-183): an UNMODIFIED delta is never diffable — UNCONDITIONAL, even with INCLUDE_UNMODIFIED set, so the standalone
        // patch does not run a no-op XDiff.
        string repoDir = Path.Combine(_tempDir, "repo");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using var patch = GitPatch.FromBuffers(
            repo,
            "same content\n"u8.ToArray(),
            "same content\n"u8.ToArray(),
            new GitDiffOptions { Flags = GitDiffOptionsFlags.IncludeUnmodified });

        int hunks = await patch.GetHunkCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, hunks);

        PatchGenerator generator = Assert.IsType<PatchGenerator>(patch.Source);
        FieldInfo diffableField = typeof(PatchGenerator).GetField("_diffable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)diffableField.GetValue(generator)!);
    }

    [Fact]
    public async Task Diffable_IdenticalContentDifferentMode_NotDiffable()
    {
        // C (patch_generate.c:200-204): identical size AND identical OID →
        // not diffable, even when the delta is MODIFIED (mode-only change), so
        // no XDiff runs on identical content.
        string repoDir = Path.Combine(_tempDir, "repo2");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using var patch = GitPatch.FromBuffers(
            repo,
            "same content\n"u8.ToArray(),
            "same content\n"u8.ToArray());

        PatchGenerator generator = Assert.IsType<PatchGenerator>(patch.Source);

        // Force the same MODIFIED-delta state C's check sees: flip the status
        // while keeping size + OID identical — the size/OID test must still
        // report not-diffable.
        FieldInfo deltaField = typeof(PatchGenerator).GetField("_delta", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var delta = (GitDiffDelta)deltaField.GetValue(generator)!;
        delta.Status = GitDeltaStatus.Modified;

        int hunks = await patch.GetHunkCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, hunks);

        FieldInfo diffableField = typeof(PatchGenerator).GetField("_diffable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)diffableField.GetValue(generator)!);
    }

    [Fact]
    public async Task Diffable_DifferentContent_StillDiffable()
    {
        // Control: differing content stays diffable and produces hunks.
        string repoDir = Path.Combine(_tempDir, "repo3");
        await using GitRepository repo = await GitRepository.InitAsync(repoDir, isBare: true, new GitContext(), TestContext.Current.CancellationToken);

        using var patch = GitPatch.FromBuffers(
            repo,
            "old line\n"u8.ToArray(),
            "new line\n"u8.ToArray());

        int hunks = await patch.GetHunkCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, hunks);
    }
}
