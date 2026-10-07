// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Attributes;

/// <summary>
/// A git content filter. Managed port of <c>git_filter</c>
/// (<c>include/git2/sys/filter.h:299-351</c>). Replaces C's function-pointer
/// vtable with an interface — AOT-clean (no <c>delegate*</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each filter declares which attributes it cares about via
/// <see cref="Attributes"/> (e.g. <c>"crlf eol text"</c>, <c>"+ident"</c>).
/// The filter list loader resolves these attributes for the file being
/// filtered and passes the resolved values to <see cref="CheckAsync"/>.
/// </para>
/// <para>
/// If <see cref="CheckAsync"/> returns <see cref="GitFilterResult.Apply"/>, the
/// filter's <see cref="ApplyAsync"/> method is called with the full input buffer.
/// If it returns <see cref="GitFilterResult.Passthrough"/>, the filter is
/// skipped for this file.
/// </para>
/// <para>
/// The <c>stream</c> vtable slot is handled by the streaming pipeline:
/// <see cref="GitFilterList"/> wraps each <see cref="ApplyAsync"/> in a
/// <see cref="BufferedFilterStream"/> that buffers all writes and calls
/// <see cref="ApplyAsync"/> on <c>Close</c>.
/// </para>
/// </remarks>
public interface IFilter
{
    /// <summary>The filter name (e.g. <c>"crlf"</c>, <c>"ident"</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Space-separated attribute specifications. Each entry is one of:
    /// <list type="bullet">
    /// <item><term><c>name</c></term><description>Load the attribute value; pass to <see cref="CheckAsync"/>.</description></item>
    /// <item><term><c>=value</c></term><description>Attribute must equal <c>value</c>.</description></item>
    /// <item><term><c>+name</c></term><description>Attribute must be TRUE.</description></item>
    /// <item><term><c>-name</c></term><description>Attribute must be FALSE.</description></item>
    /// <item><term><c>!name</c></term><description>Attribute must be UNSET.</description></item>
    /// <item><term><c>=*</c></term><description>Any string value matches.</description></item>
    /// </list>
    /// Matches <c>git_filter.attributes</c> (<c>sys/filter.h:305</c>).
    /// </summary>
    string Attributes { get; }

    /// <summary>
    /// Checks whether this filter applies to the given source. The
    /// <paramref name="attrValues"/> are the pre-resolved attribute values
    /// for the names declared in <see cref="Attributes"/>. Matches C's
    /// <c>git_filter_check_fn</c>.
    /// </summary>
    /// <returns><see cref="GitFilterResult.Apply"/> to apply the filter, or <see cref="GitFilterResult.Passthrough"/> to skip.</returns>
    ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the filter to the input buffer. The mode (smudge/clean) is
    /// in <see cref="GitFilterSource.Mode"/>. Matches C's
    /// <c>git_filter_apply_fn</c> (buffered — the streaming pipeline wraps
    /// this in a <see cref="BufferedFilterStream"/>).
    /// </summary>
    /// <returns>An <see cref="GitApplyResult"/> with the filtered output, or <see cref="GitApplyResult.Passthrough"/> to pass through unchanged.</returns>
    ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);
}
