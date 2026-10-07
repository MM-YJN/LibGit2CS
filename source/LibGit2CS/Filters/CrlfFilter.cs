// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using LibGit2CS.Attributes;
using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Index;
using LibGit2CS.IO;
using LibGit2CS.Objects;
using LibGit2CS.Repository;

namespace LibGit2CS.Filters;

/// <summary>
/// CRLF line-ending filter. Managed port of <c>src/libgit2/crlf.c</c>
/// (426 LOC). Implements <see cref="IFilter"/> with attributes
/// <c>"crlf eol text"</c> and priority 0 (runs first on clean).
/// </summary>
/// <remarks>
/// The filter resolves <c>core.autocrlf</c>/<c>core.safecrlf</c>/<c>core.eol</c>
/// config + <c>text</c>/<c>crlf</c>/<c>eol</c> attributes to determine whether
/// to convert line endings. On clean (workdir→ODB), CRLF→LF. On smudge
/// (ODB→workdir), LF→CRLF. Binary files are always passthrough. The
/// <c>safecrlf</c> config can reject destructive conversions.
/// </remarks>
internal sealed class CrlfFilter : IFilter
{
    public string Name => FilterRegistry.CrlfName;
    public string Attributes => "crlf eol text";

    /// <summary>
    /// The CRLF action resolved from attributes + config. Matches
    /// <c>git_crlf_t</c> (crlf.c:21-30).
    /// </summary>
    private enum CrlfAction
    {
        Undefined = 0,
        Binary = 1,
        Text = 2,
        TextInput = 3,
        TextCrlf = 4,
        Auto = 5,
        AutoInput = 6,
        AutoCrlf = 7,
    }

    /// <summary>
    /// Resolved attributes + config values for a single file. Matches
    /// <c>struct crlf_attrs</c> (crlf.c:32-39).
    /// </summary>
    private sealed class CrlfAttrs
    {
        public CrlfAction AttrAction = CrlfAction.Undefined;
        public CrlfAction CrlfAction = CrlfAction.Undefined;
        public GitAutoCrlf AutoCrlf = GitAutoCrlf.False;
        public GitSafeCrlf SafeCrlf = GitSafeCrlf.False;
        public GitEol CoreEol = GitEol.Unset;
    }

    public async ValueTask<GitFilterResult> CheckAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken = default)
    {
        CrlfAttrs ca = await ConvertAttrsAsync(source, attrValues, cancellationToken).ConfigureAwait(false);
        if (ca.CrlfAction == CrlfAction.Binary)
        {
            return GitFilterResult.Passthrough;
        }

        return GitFilterResult.Apply;
    }

    public async ValueTask<GitApplyResult> ApplyAsync(GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        // Re-resolve attrs (Check may have been bypassed).
        AttributeCache? attrCache = source.Repo is not null
            ? await source.Repo.GetAttributeCacheAsync(cancellationToken).ConfigureAwait(false)
            : null;
        GitAttrValue[] attrValues = await ResolveAttrsAsync(source, attrCache, cancellationToken).ConfigureAwait(false);
        CrlfAttrs ca = await ConvertAttrsAsync(source, attrValues, cancellationToken).ConfigureAwait(false);

        if (source.Mode == GitFilterMode.ToWorktree)
        {
            return await ApplyToWorkdirAsync(ca, input, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyToOdbAsync(ca, source, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the attr values for this filter's declared attributes.
    /// When called from Apply (Check was bypassed), re-looks up the attrs.
    /// </summary>
    private static async ValueTask<GitAttrValue[]> ResolveAttrsAsync(GitFilterSource source, AttributeCache? attrCache, CancellationToken cancellationToken)
    {
        if (attrCache is null || source.Path is not { } sourcePath)
        {
            return [];
        }

        // C (attr.c:69-70): GIT_DIR_FLAG_FALSE for bare repos (or no repo),
        // otherwise UNKNOWN and the workdir path is stat'ed (attr_file.c:599-613).
        GitRepository? repo = source.Repo;
        var attrPath = new AttrPath();
        if (repo is not null && !repo.IsBare)
        {
            attrPath.Init(sourcePath, GitPath.FromUtf8String(repo.Workdir ?? string.Empty), AttrPath.DirFlag.Unknown);
        }
        else
        {
            attrPath.Init(sourcePath, default, AttrPath.DirFlag.False);
        }

        // C (crlf.c:347-390): check caches the resolved attrs in the payload and apply REUSES them — apply and check always see the same attribute
        // flags/commit. The port re-resolves, so it must pass the list's flags (HEAD/COMMIT/NO_SYSTEM) and the current commit id — a bare default-flags lookup
        // could disagree with Check.
        GitAttrCheckFlags attrFlags = GitFilterList.AttrFlagsFromFilterFlags(source.Flags);
        return await attrCache.LookupManyAsync(attrPath, ["crlf", "eol", "text"], attrFlags, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the CRLF action from config + attributes. Matches
    /// <c>convert_attrs</c> (crlf.c:287-345).
    /// </summary>
    private static async ValueTask<CrlfAttrs> ConvertAttrsAsync(GitFilterSource source, IReadOnlyList<GitAttrValue> attrValues, CancellationToken cancellationToken)
    {
        var ca = new CrlfAttrs();
        GitRepository? repo = source.Repo;

        if (repo is not null)
        {
            ca.AutoCrlf = await repo.Config.GetMappedAsync("core.autocrlf", GitConfigMaps.AutoCrlfMap, GitConfigMaps.AutoCrlfDefault, cancellationToken).ConfigureAwait(false);
            ca.SafeCrlf = await repo.Config.GetMappedAsync("core.safecrlf", GitConfigMaps.SafeCrlfMap, GitConfigMaps.SafeCrlfDefault, cancellationToken).ConfigureAwait(false);
            ca.CoreEol = await repo.Config.GetMappedAsync("core.eol", GitConfigMaps.EolMap, GitConfigMaps.EolDefault, cancellationToken).ConfigureAwait(false);
        }

        // Downgrade FAIL to WARN if ALLOW_UNSAFE option is used.
        if ((source.Flags & GitFilterListFlags.AllowUnsafe) != 0 && ca.SafeCrlf == GitSafeCrlf.Fail)
        {
            ca.SafeCrlf = GitSafeCrlf.Warn;
        }

        if (attrValues.Count >= 3)
        {
            // Attributes declared as "crlf eol text" — attrValues[0]=crlf, [1]=eol, [2]=text.
            ca.CrlfAction = CheckCrlf(attrValues[2]); // text attribute first

            if (ca.CrlfAction == CrlfAction.Undefined)
            {
                ca.CrlfAction = CheckCrlf(attrValues[0]); // crlf attribute fallback
            }

            if (ca.CrlfAction != CrlfAction.Binary)
            {
                GitEol eolAttr = CheckEol(attrValues[1]);

                if (ca.CrlfAction == CrlfAction.Auto && eolAttr == GitEol.Lf)
                {
                    ca.CrlfAction = CrlfAction.AutoInput;
                }
                else if (ca.CrlfAction == CrlfAction.Auto && eolAttr == GitEol.Crlf)
                {
                    ca.CrlfAction = CrlfAction.AutoCrlf;
                }
                else if (eolAttr == GitEol.Lf)
                {
                    ca.CrlfAction = CrlfAction.TextInput;
                }
                else if (eolAttr == GitEol.Crlf)
                {
                    ca.CrlfAction = CrlfAction.TextCrlf;
                }
            }

            ca.AttrAction = ca.CrlfAction;
        }
        else
        {
            ca.CrlfAction = CrlfAction.Undefined;
        }

        if (ca.CrlfAction == CrlfAction.Text)
        {
            ca.CrlfAction = TextEolIsCrlf(ca) ? CrlfAction.TextCrlf : CrlfAction.TextInput;
        }

        if (ca.CrlfAction == CrlfAction.Undefined && ca.AutoCrlf == GitAutoCrlf.False)
        {
            ca.CrlfAction = CrlfAction.Binary;
        }

        if (ca.CrlfAction == CrlfAction.Undefined && ca.AutoCrlf == GitAutoCrlf.True)
        {
            ca.CrlfAction = CrlfAction.AutoCrlf;
        }

        if (ca.CrlfAction == CrlfAction.Undefined && ca.AutoCrlf == GitAutoCrlf.Input)
        {
            ca.CrlfAction = CrlfAction.AutoInput;
        }

        return ca;
    }

    /// <summary>Maps the <c>text</c>/<c>crlf</c> attr value to a CrlfAction. Matches <c>check_crlf</c> (crlf.c:45-59).</summary>
    private static CrlfAction CheckCrlf(GitAttrValue value)
    {
        if (value.IsTrue)
        {
            return CrlfAction.Text;
        }

        if (value.IsFalse)
        {
            return CrlfAction.Binary;
        }

        if (value.IsUnspecified)
        {
            return CrlfAction.Undefined;
        }

        if (value.Kind == GitAttrValueKind.Value && value.Text is not null)
        {
            if (value.Text == "input")
            {
                return CrlfAction.TextInput;
            }

            if (value.Text == "auto")
            {
                return CrlfAction.Auto;
            }
        }

        return CrlfAction.Undefined;
    }

    /// <summary>Maps the <c>eol</c> attr value to an Eol. Matches <c>check_eol</c> (crlf.c:61-71).</summary>
    private static GitEol CheckEol(GitAttrValue value)
    {
        if (value.IsUnspecified)
        {
            return GitEol.Unset;
        }

        if (value.Kind == GitAttrValueKind.Value && value.Text is not null)
        {
            if (value.Text == "lf")
            {
                return GitEol.Lf;
            }

            if (value.Text == "crlf")
            {
                return GitEol.Crlf;
            }
        }

        return GitEol.Unset;
    }

    /// <summary>Determines whether text mode should use CRLF. Matches <c>text_eol_is_crlf</c> (crlf.c:115-128).</summary>
    private static bool TextEolIsCrlf(CrlfAttrs ca)
    {
        if (ca.AutoCrlf == GitAutoCrlf.True)
        {
            return true;
        }

        if (ca.AutoCrlf == GitAutoCrlf.Input)
        {
            return false;
        }

        if (ca.CoreEol == GitEol.Crlf)
        {
            return true;
        }

        if (ca.CoreEol == GitEol.Unset && GitConfigMaps.EolNative == GitEol.Crlf)
        {
            return true;
        }

        return false;
    }

    /// <summary>Determines the output EOL for the current action. Matches <c>output_eol</c> (crlf.c:130-151).</summary>
    private static GitEol OutputEol(CrlfAttrs ca)
    {
        return ca.CrlfAction switch
        {
            CrlfAction.Binary => GitEol.Unset,
            CrlfAction.TextCrlf => GitEol.Crlf,
            CrlfAction.TextInput => GitEol.Lf,
            CrlfAction.Undefined => GitEol.Crlf,
            CrlfAction.AutoCrlf => GitEol.Crlf,
            CrlfAction.AutoInput => GitEol.Lf,
            CrlfAction.Text => TextEolIsCrlf(ca) ? GitEol.Crlf : GitEol.Lf,
            CrlfAction.Auto => TextEolIsCrlf(ca) ? GitEol.Crlf : GitEol.Lf,
            _ => ca.CoreEol,
        };
    }

    /// <summary>
    /// Checks safecrlf constraints. Matches <c>check_safecrlf</c>
    /// (crlf.c:153-206). Returns false if the conversion should be rejected.
    /// </summary>
    private static bool CheckSafeCrlf(CrlfAttrs ca, GitFilterSource source, in GitTextStats stats)
    {
        if (ca.SafeCrlf == GitSafeCrlf.False)
        {
            return true;
        }

        GitEol outEol = OutputEol(ca);

        if (outEol == GitEol.Lf)
        {
            // CRLFs would be removed — check if we'd remove CRLFs.
            if (stats.Crlf > 0)
            {
                if (ca.SafeCrlf == GitSafeCrlf.Warn)
                {
                    // TODO: issue a warning
                }
                else
                {
                    string? filename = source.Path?.ToUtf8String();
                    string msg = !string.IsNullOrEmpty(filename)
                        ? $"CRLF would be replaced by LF in '{filename}'"
                        : "CRLF would be replaced by LF";
                    throw new GitException(GitErrorCode.Error, msg, GitErrorCategory.Filter);
                }
            }
        }
        else if (outEol == GitEol.Crlf)
        {
            // CRLFs would be added — check if we have "naked" LFs.
            if (stats.Crlf != stats.Lf)
            {
                if (ca.SafeCrlf == GitSafeCrlf.Warn)
                {
                    // TODO: issue a warning
                }
                else
                {
                    string? filename = source.Path?.ToUtf8String();
                    string msg = !string.IsNullOrEmpty(filename)
                        ? $"LF would be replaced by CRLF in '{filename}'"
                        : "LF would be replaced by CRLF";
                    throw new GitException(GitErrorCode.Error, msg, GitErrorCategory.Filter);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if the index entry for this path has CR in its blob. Matches
    /// <c>has_cr_in_index</c> (crlf.c:73-113). Used by the safer autocrlf
    /// handling — if the index already has CR, don't convert.
    /// </summary>
    private static async ValueTask<bool> HasCrInIndexAsync(GitFilterSource source, CancellationToken cancellationToken)
    {
        GitRepository? repo = source.Repo;
        GitPath? pathOpt = source.Path;
        if (repo is null || pathOpt is null || pathOpt.Value.IsEmpty)
        {
            return false;
        }

        GitPath path = pathOpt.Value;

        // C (crlf.c:73-90, has_cr_in_index): a corrupt index makes git_repository_index__weakptr fail — the error is CLEARED and the function returns false
        // (the CRLF filter proceeds with "no CR in index").
        GitIndex? index;
        try
        {
            index = await repo.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            return false;
        }

        GitIndexEntry? entry = index.EntryByPath(path, stage: 0);
        if (!entry.HasValue)
        {
            entry = index.EntryByPath(path, stage: 1);
        }

        if (!entry.HasValue)
        {
            return false;
        }

        // Don't CRLF filter non-blobs.
        GitFileMode mode = entry.Value.Mode;
        if (mode is not GitFileMode.Regular and not GitFileMode.Executable)
        {
            return true;
        }

        GitBlob? blob = await repo.Objects.LookupAsync<GitBlob>(entry.Value.Id, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return false;
        }

        ReadOnlySpan<byte> content = blob.Content.Span;
        return content.Length > 0 && content.Contains((byte)'\r');
    }

    /// <summary>
    /// Clean: CRLF→LF. Matches <c>crlf_apply_to_odb</c> (crlf.c:208-251).
    /// </summary>
    private static async ValueTask<GitApplyResult> ApplyToOdbAsync(CrlfAttrs ca, GitFilterSource source, ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        // Binary attribute? Empty file? Nothing to do.
        if (ca.CrlfAction == CrlfAction.Binary || input.Length == 0)
        {
            return GitApplyResult.Passthrough;
        }

        var stats = GitTextStats.Gather(input.Span);

        // Heuristics for AUTO modes.
        if (ca.CrlfAction is CrlfAction.Auto or CrlfAction.AutoInput or CrlfAction.AutoCrlf)
        {
            if (stats.IsBinary)
            {
                return GitApplyResult.Passthrough;
            }

            // If the file in the index has any CR, do not convert.
            if (await HasCrInIndexAsync(source, cancellationToken).ConfigureAwait(false))
            {
                return GitApplyResult.Passthrough;
            }
        }

        // Check safecrlf constraints.
        CheckSafeCrlf(ca, source, stats);

        // If there are no CR characters to filter out, pass through.
        if (stats.Crlf == 0)
        {
            return GitApplyResult.Passthrough;
        }

        // Actually drop the carriage returns.
        return GitApplyResult.WithOutput(GitTextStats.CrlfToLf(input.Span));
    }

    /// <summary>
    /// Smudge: LF→CRLF. Matches <c>crlf_apply_to_workdir</c> (crlf.c:253-284).
    /// </summary>
    private static ValueTask<GitApplyResult> ApplyToWorkdirAsync(CrlfAttrs ca, ReadOnlyMemory<byte> input, CancellationToken _ = default)
    {
        // Empty file or output EOL is not CRLF? Nothing to do.
        if (input.Length == 0 || OutputEol(ca) != GitEol.Crlf)
        {
            return ValueTask.FromResult(GitApplyResult.Passthrough);
        }

        var stats = GitTextStats.Gather(input.Span);

        // If there are no LFs, or all LFs are part of CRLF, nothing to do.
        if (stats.Lf == 0 || stats.Lf == stats.Crlf)
        {
            return ValueTask.FromResult(GitApplyResult.Passthrough);
        }

        if (ca.CrlfAction is CrlfAction.Auto or CrlfAction.AutoInput or CrlfAction.AutoCrlf)
        {
            // If we have any existing CR or CRLF line endings, do nothing.
            if (stats.Cr > 0)
            {
                return ValueTask.FromResult(GitApplyResult.Passthrough);
            }

            // Don't filter binary files.
            if (stats.IsBinary)
            {
                return ValueTask.FromResult(GitApplyResult.Passthrough);
            }
        }

        return ValueTask.FromResult(GitApplyResult.WithOutput(GitTextStats.LfToCrlf(input.Span)));
    }
}
