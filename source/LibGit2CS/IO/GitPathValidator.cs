// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

using LibGit2CS.Config;
using LibGit2CS.Core;
using LibGit2CS.Repository;

namespace LibGit2CS.IO;

/// <summary> Path validator. Managed port of libgit2's <c>src/libgit2/path.c</c> + <c>src/util/fs_path.c</c> (validation subset). </summary> <remarks> <para>
/// Validates paths before writing to the workdir or index: rejects <c>.git</c> (with HFS/NTFS normalization), directory traversal, NUL bytes, and
/// platform-specific forbidden characters. Used by checkout, index-add, and other write paths. </para> <para> <b>Byte-faithful.</b> The primary entry point
/// <see cref="IsValidAsync(GitPath, GitPathRejectFlags, GitRepository?, ushort, CancellationToken)"/> operates on <see cref="GitPath"/> (raw UTF-8 bytes),
/// walking the path byte-by-byte. The HFS <c>.git</c> normalization (<see cref="NextHfsChar"/>) iterates UTF-8 codepoints over the raw bytes — a faithful port
/// of <c>next_hfs_char</c> (<c>path.c:20-59</c>). Lowercasing non-ASCII chars directly was a latent bug for
/// non-ASCII paths. The NTFS <c>.git</c>/<c>GIT~1</c> checks and DOS reserved-name checks are byte-domain. The <see cref="string"/> overload remains as a
/// convenience overload (encodes to UTF-8 and delegates). </para> </remarks>
public static class GitPathValidator
{
    /// <summary>
    /// Validates a byte-faithful path against the given reject flags. Matches
    /// <c>git_path_str_is_valid</c> + <c>git_fs_path_str_is_valid_ext</c>.
    /// </summary>
    public static async ValueTask<bool> IsValidAsync(
        GitPath path,
        GitPathRejectFlags flags,
        GitRepository? repo = null,
        ushort fileMode = 0,
        CancellationToken cancellationToken = default)
    {
        // C (fs_path.c:1711-1712): with no reject flags every string is
        // valid - including empty ones and strings with embedded NULs.
        if (flags == 0)
        {
            return true;
        }

        if (path.IsEmpty)
        {
            return (flags & GitPathRejectFlags.EmptyComponent) == 0;
        }

        // Upgrade .git checks based on platform/config.
        if ((flags & GitPathRejectFlags.DotGit) != 0)
        {
            flags = await UpgradeDotGitFlagsAsync(repo, flags, cancellationToken).ConfigureAwait(false);
        }

        // On non-Windows, clear the long-paths flag (no length limit).
        if (!OperatingSystem.IsWindows())
        {
            flags &= ~GitPathRejectFlags.LongPaths;
        }

        ReadOnlySpan<byte> span = path.Span;

        // Walk the path byte-by-byte, validating each byte and each component
        // (delimited by '/').
        int start = 0;
        for (int i = 0; i < span.Length; i++)
        {
            byte c = span[i];

            // NUL byte is always rejected (implicit in C via string termination).
            if (c == 0)
            {
                return false;
            }

            if (!ValidateChar(c, flags))
            {
                return false;
            }

            if (c != (byte)'/')
            {
                continue;
            }

            // Validate the component [start, i).
            if (!ValidateComponent(span.Slice(start, i - start), flags))
            {
                return false;
            }

            if (!ValidateRepoComponent(span.Slice(start, i - start), flags, repo, fileMode))
            {
                return false;
            }

            start = i + 1;
        }

        // Validate the final component [start, end).
        ReadOnlySpan<byte> lastComponent = span.Slice(start);
        if (!ValidateComponent(lastComponent, flags))
        {
            return false;
        }

        if (!ValidateRepoComponent(lastComponent, flags, repo, fileMode))
        {
            return false;
        }

        // C (fs_path.c:1750-1756): on Windows, GIT_FS_PATH_REJECT_LONG_PATHS
        // enforces MAX_PATH (260) UTF-8 characters over the whole path. The
        // flag is cleared for non-Windows above, so this only runs on Windows.
        if ((flags & GitPathRejectFlags.LongPaths) != 0 && Utf8CharLength(span) > 260)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a string path. Encodes to UTF-8 and delegates to the
    /// byte-faithful <see cref="LibGit2CS.IO.GitPathValidator.IsValidAsync(LibGit2CS.IO.GitPath, LibGit2CS.IO.GitPathRejectFlags, LibGit2CS.Repository.GitRepository?, ushort, System.Threading.CancellationToken)"/>.
    /// </summary>
    public static async ValueTask<bool> IsValidAsync(
        string path,
        GitPathRejectFlags flags,
        GitRepository? repo = null,
        ushort fileMode = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return await IsValidAsync(GitPath.FromUtf8String(path), flags, repo, fileMode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that a path is not too long. Matches
    /// <c>git_path_validate_str_length</c>. On POSIX this always returns true.
    /// </summary>
    public static ValueTask<bool> IsValidLengthAsync(string path, GitRepository? repo = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        // On POSIX (the majority target) this always returns true.
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(true);
        }

        return new ValueTask<bool>(IsValidLengthSlowAsync(path, repo, cancellationToken));
    }

    /// <summary>Slow path of <see cref="IsValidLengthAsync"/>: Windows longpaths config check.</summary>
    private static async Task<bool> IsValidLengthSlowAsync(string path, GitRepository? repo, CancellationToken cancellationToken)
    {
        // On Windows, MAX_PATH = 260. With longpaths config, the limit is lifted.
        if (repo is not null && await repo.Config.GetBoolAsync("core.longpaths", false, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return path.Length <= 260;
    }

    /// <summary>
    /// Counts UTF-8 characters in a byte span, treating each byte of an
    /// invalid sequence as one character. Matches <c>git_utf8_char_length</c>
    /// (utf8.c:117-131).
    /// </summary>
    private static int Utf8CharLength(ReadOnlySpan<byte> span)
    {
        int count = 0;
        int i = 0;
        while (i < span.Length)
        {
            if (!TryDecodeUtf8(span[i..], out _, out int byteCount))
            {
                byteCount = 1;
            }

            i += byteCount;
            count++;
        }

        return count;
    }

    // --- Per-byte validation (validate_char, fs_path.c:1610-1635) ---

    private static bool ValidateChar(byte c, GitPathRejectFlags flags)
    {
        if ((flags & GitPathRejectFlags.Backslash) != 0 && c == (byte)'\\')
        {
            return false;
        }

        if ((flags & GitPathRejectFlags.Slash) != 0 && c == (byte)'/')
        {
            return false;
        }

        if ((flags & GitPathRejectFlags.NtChars) != 0)
        {
            if (c < 32)
            {
                return false;
            }

            switch (c)
            {
                case (byte)'<':
                case (byte)'>':
                case (byte)':':
                case (byte)'"':
                case (byte)'|':
                case (byte)'?':
                case (byte)'*':
                    return false;
            }
        }

        return true;
    }

    // --- Per-component validation (validate_component, fs_path.c:1646-1685) ---

    private static bool ValidateComponent(ReadOnlySpan<byte> component, GitPathRejectFlags flags)
    {
        if (component.Length == 0)
        {
            return (flags & GitPathRejectFlags.EmptyComponent) == 0;
        }

        if ((flags & GitPathRejectFlags.Traversal) != 0)
        {
            if (component.Length == 1 && component[0] == (byte)'.')
            {
                return false;
            }

            if (component.Length == 2 && component[0] == (byte)'.' && component[1] == (byte)'.')
            {
                return false;
            }
        }

        if ((flags & GitPathRejectFlags.TrailingDot) != 0 && component[^1] == (byte)'.')
        {
            return false;
        }

        if ((flags & GitPathRejectFlags.TrailingSpace) != 0 && component[^1] == (byte)' ')
        {
            return false;
        }

        if ((flags & GitPathRejectFlags.TrailingColon) != 0 && component[^1] == (byte)':')
        {
            return false;
        }

        if ((flags & GitPathRejectFlags.DosPaths) != 0)
        {
            if (!ValidateDosPath(component, "CON"u8)
                || !ValidateDosPath(component, "PRN"u8)
                || !ValidateDosPath(component, "AUX"u8)
                || !ValidateDosPath(component, "NUL"u8)
                || !ValidateDosPathWithSuffix(component, "COM"u8)
                || !ValidateDosPathWithSuffix(component, "LPT"u8))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a component against a DOS reserved device name (CON, PRN, AUX,
    /// NUL). Matches <c>validate_dospath</c> (fs_path.c:1545-1590). Byte-port:
    /// case fold is ASCII-only via <see cref="char.ToLowerInvariant"/>.
    /// </summary>
    private static bool ValidateDosPath(ReadOnlySpan<byte> component, ReadOnlySpan<byte> reserved)
    {
        if (component.Length < reserved.Length)
        {
            return true;
        }

        for (int i = 0; i < reserved.Length; i++)
        {
            if (char.ToLowerInvariant((char)component[i]) != char.ToLowerInvariant((char)reserved[i]))
            {
                return true;
            }
        }

        // The component matches the reserved name. Check what follows:
        // it's only reserved if followed by '.', ':', end-of-string, or a number
        // (for COM/LPT). For fixed names (CON/PRN/AUX/NUL), any of those is bad.
        if (component.Length == reserved.Length)
        {
            return false; // exact match → reject
        }

        byte after = component[reserved.Length];
        if (after is (byte)'.' or (byte)':')
        {
            return false;
        }

        return true; // followed by something else → OK
    }

    /// <summary>
    /// Validates a component against DOS reserved names with a numeric suffix
    /// (COM1–COM9, LPT1–LPT9). Matches <c>validate_dospath(..., true)</c>.
    /// </summary>
    private static bool ValidateDosPathWithSuffix(ReadOnlySpan<byte> component, ReadOnlySpan<byte> prefix)
    {
        if (component.Length < prefix.Length + 1)
        {
            return true;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (char.ToLowerInvariant((char)component[i]) != char.ToLowerInvariant((char)prefix[i]))
            {
                return true;
            }
        }

        // Must be followed by a digit 1-9.
        if (component[prefix.Length] is >= (byte)'1' and <= (byte)'9')
        {
            if (component.Length == prefix.Length + 1)
            {
                return false; // exact match like "COM1" → reject
            }

            byte after = component[prefix.Length + 1];
            if (after is (byte)'.' or (byte)':')
            {
                return false;
            }
        }

        return true;
    }

    // --- .git component validation (validate_repo_component, path.c:215-261) ---

    private static bool ValidateRepoComponent(
        ReadOnlySpan<byte> component,
        GitPathRejectFlags flags,
        GitRepository? _,
        ushort fileMode)
    {
        if ((flags & GitPathRejectFlags.DotGitHfs) != 0)
        {
            if (!ValidateDotGitHfs(component))
            {
                return false;
            }

            // Symlink .gitmodules check on HFS.
            if (IsSymlink(fileMode) && IsGitFileHfs(component, "gitmodules"u8))
            {
                return false;
            }
        }

        if ((flags & GitPathRejectFlags.DotGitNtfs) != 0)
        {
            if (!ValidateDotGitNtfs(component))
            {
                return false;
            }

            if (IsSymlink(fileMode) && IsGitFileNtfs(component, "gitmodules"u8))
            {
                return false;
            }
        }

        // Literal .git check (only if HFS/NTFS didn't already run).
        if ((flags & GitPathRejectFlags.DotGitHfs) == 0
            && (flags & GitPathRejectFlags.DotGitNtfs) == 0
            && (flags & GitPathRejectFlags.DotGitLiteral) != 0)
        {
            if (component.Length >= 4
                && component[0] == (byte)'.'
                && (component[1] == (byte)'g' || component[1] == (byte)'G')
                && (component[2] == (byte)'i' || component[2] == (byte)'I')
                && (component[3] == (byte)'t' || component[3] == (byte)'T'))
            {
                if (component.Length == 4)
                {
                    return false;
                }

                // Symlink .gitmodules check (literal).
                if (IsSymlink(fileMode) && CommonPrefixIcase(component, ".gitmodules"u8) == component.Length)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsSymlink(ushort fileMode)
        => (fileMode & 0xF000) == 0xA000; // S_IFLNK = 0120000

    // --- HFS .git normalization (path.c:20-83) ---

    /// <summary>
    /// Returns the next significant HFS character from a UTF-8 byte span,
    /// skipping ignored codepoints and folding to lowercase. Returns 0 at
    /// end-of-string. Faithful byte-port of <c>next_hfs_char</c>
    /// (<c>path.c:20-59</c>): iterates UTF-8 codepoints (1-4 bytes) over the
    /// raw bytes, skips the ignored set (ZWNJ, ZWJ, directional marks, BOM,
    /// etc.), and folds via <see cref="char.ToLowerInvariant"/> (ASCII-only —
    /// the git folder name is ASCII).
    /// </summary>
    /// <remarks>
    /// Advances <paramref name="span"/> past the consumed bytes. Returns -1
    /// on invalid UTF-8 (matches the C <c>git_utf8_iterate</c> failure).
    /// </remarks>
    private static int NextHfsChar(ref ReadOnlySpan<byte> span)
    {
        while (span.Length > 0)
        {
            if (!TryDecodeUtf8(span, out uint codepoint, out int cpLen))
            {
                return -1;
            }

            span = span[cpLen..];

            // Ignored codepoints (path.c:32-50). These are all > 0x7F so they
            // only appear as multi-byte UTF-8 sequences; the char-based port
            // could not detect them.
            switch (codepoint)
            {
                case 0x200C: // ZERO WIDTH NON-JOINER
                case 0x200D: // ZERO WIDTH JOINER
                case 0x200E: // LEFT-TO-RIGHT MARK
                case 0x200F: // RIGHT-TO-LEFT MARK
                case 0x202A: // LEFT-TO-RIGHT EMBEDDING
                case 0x202B: // RIGHT-TO-LEFT EMBEDDING
                case 0x202C: // POP DIRECTIONAL FORMATTING
                case 0x202D: // LEFT-TO-RIGHT OVERRIDE
                case 0x202E: // RIGHT-TO-LEFT OVERRIDE
                case 0x206A: // INHIBIT SYMMETRIC SWAPPING
                case 0x206B: // ACTIVATE SYMMETRIC SWAPPING
                case 0x206C: // INHIBIT ARABIC FORM SHAPING
                case 0x206D: // ACTIVATE ARABIC FORM SHAPING
                case 0x206E: // NATIONAL DIGIT SHAPES
                case 0x206F: // NOMINAL DIGIT SHAPES
                case 0xFEFF: // ZERO WIDTH NO-BREAK SPACE (BOM)
                    continue;
            }

            // Fold into lowercase — ASCII-only (the git folder name is ASCII).
            // git__tolower on an int codepoint folds 0x41-0x5A → 0x61-0x7A;
            // all other codepoints pass through unchanged.
            return codepoint <= 0x7F ? char.ToLowerInvariant((char)codepoint) : (int)codepoint;
        }

        return 0; // end of string
    }

    /// <summary>
    /// Decodes a 1-4 byte UTF-8 sequence. Ports <c>git_utf8_iterate</c>.
    /// </summary>
    private static bool TryDecodeUtf8(ReadOnlySpan<byte> span, out uint codepoint, out int byteCount)
    {
        codepoint = 0;
        byteCount = 0;
        if (span.IsEmpty)
        {
            return false;
        }

        byte b0 = span[0];
        if (b0 < 0x80)
        {
            codepoint = b0;
            byteCount = 1;
            return true;
        }

        if ((b0 & 0xE0) == 0xC0)
        {
            if (span.Length < 2 || (span[1] & 0xC0) != 0x80)
            {
                return false;
            }

            codepoint = ((uint)(b0 & 0x1F) << 6) | (uint)(span[1] & 0x3F);
            byteCount = 2;
            return codepoint >= 0x80; // reject overlong
        }

        if ((b0 & 0xF0) == 0xE0)
        {
            if (span.Length < 3 || (span[1] & 0xC0) != 0x80 || (span[2] & 0xC0) != 0x80)
            {
                return false;
            }

            codepoint = ((uint)(b0 & 0x0F) << 12) | ((uint)(span[1] & 0x3F) << 6) | (uint)(span[2] & 0x3F);
            byteCount = 3;
            return codepoint >= 0x800; // reject overlong
        }

        if ((b0 & 0xF8) == 0xF0)
        {
            if (span.Length < 4 || (span[1] & 0xC0) != 0x80 || (span[2] & 0xC0) != 0x80 || (span[3] & 0xC0) != 0x80)
            {
                return false;
            }

            codepoint = ((uint)(b0 & 0x07) << 18) | ((uint)(span[1] & 0x3F) << 12) | ((uint)(span[2] & 0x3F) << 6) | (uint)(span[3] & 0x3F);
            byteCount = 4;
            return codepoint is >= 0x10000 and <= 0x10FFFF; // reject overlong/out-of-range
        }

        return false;
    }

    /// <summary>
    /// Validates that a path component is not <c>.git</c> under HFS Unicode
    /// normalization. Returns true if the component is safe (NOT .git).
    /// Matches <c>validate_dotgit_hfs</c> (path.c:85-88).
    /// </summary>
    private static bool ValidateDotGitHfs(ReadOnlySpan<byte> component)
        => ValidateDotGitHfsGeneric(component, "git"u8);

    /// <summary>
    /// Generic HFS dotgit validation. Returns true if the component does NOT
    /// match <c>.{needle}</c> after normalization. Matches
    /// <c>validate_dotgit_hfs_generic</c> (path.c:61-83).
    /// </summary>
    private static bool ValidateDotGitHfsGeneric(ReadOnlySpan<byte> path, ReadOnlySpan<byte> needle)
    {
        ReadOnlySpan<byte> span = path;

        if (NextHfsChar(ref span) != '.')
        {
            return true;
        }

        foreach (byte expected in needle)
        {
            if (NextHfsChar(ref span) != expected)
            {
                return true;
            }
        }

        if (NextHfsChar(ref span) != 0)
        {
            return true;
        }

        return false; // matches .{needle} → reject
    }

    // --- NTFS .git validation (path.c:90-195) ---

    /// <summary>
    /// Validates that a path component is not <c>.git</c> under NTFS rules.
    /// Returns true if the component is safe. Matches
    /// <c>validate_dotgit_ntfs</c> (path.c:90-130).
    /// </summary>
    private static bool ValidateDotGitNtfs(ReadOnlySpan<byte> component)
    {
        // Check against reserved names (.git, GIT~1). The C code reads from
        // git_repository__reserved_names; we hardcode the default set here.
        ReadOnlySpan<byte> dotgit = ".git"u8;
        ReadOnlySpan<byte> gitTilde = "GIT~1"u8;

        int start = 0;
        if (component.Length >= dotgit.Length && StrncasecmpBytes(component, dotgit, dotgit.Length) == 0)
        {
            start = dotgit.Length;
        }
        else if (component.Length >= gitTilde.Length && StrncasecmpBytes(component, gitTilde, gitTilde.Length) == 0)
        {
            start = gitTilde.Length;
        }

        if (start == 0)
        {
            return true;
        }

        // Reject paths starting with .git\ or .git:
        if (start < component.Length)
        {
            if (component[start] is (byte)'\\' or (byte)':')
            {
                return false;
            }
        }

        // Reject paths like ".git " or ".git." (trailing spaces/dots only).
        for (int i = start; i < component.Length; i++)
        {
            if (component[i] is not (byte)' ' and not (byte)'.')
            {
                return true;
            }
        }

        return false; // trailing spaces/dots → reject
    }

    private static int StrncasecmpBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int len)
    {
        int cmpLen = Math.Min(Math.Min(a.Length, b.Length), len);
        for (int i = 0; i < cmpLen; i++)
        {
            int diff = char.ToLowerInvariant((char)a[i]) - char.ToLowerInvariant((char)b[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
    }

    // --- .gitmodules symlink check (path.c:334-374) ---

    private static bool IsGitFileHfs(ReadOnlySpan<byte> component, ReadOnlySpan<byte> filename)
        => !ValidateDotGitHfsGeneric(component, filename);

    /// <summary> NTFS gitfile check for <c>.gitmodules</c> symlinks. Matches <c>git_path_is_gitfile</c> with <c>GIT_PATH_FS_NTFS</c> (path.c:344-374): the
    /// negation of <see cref="ValidateDotGitNtfsGeneric"/> — true when the component is an NTFS-equivalent spelling of the gitfile (literal, 8.3 shortname, or
    /// fallback hash), so the caller rejects it. Covers the literal form, the
    /// shortname (<c>gitmod~N</c>) form, and the fallback-hash (<c>gi7eba~N</c>)
    /// form; C negates the generic check. </summary>
    private static bool IsGitFileNtfs(ReadOnlySpan<byte> component, ReadOnlySpan<byte> filename)
        => !ValidateDotGitNtfsGeneric(component, filename, "gi7eba"u8);

    /// <summary>
    /// Port of <c>validate_dotgit_ntfs_generic</c> (path.c:155-195): returns
    /// true when <paramref name="name"/> is NOT an NTFS-equivalent spelling
    /// of the gitfile, false when it is (dangerous). Mirrors C's three
    /// branches: the literal <c>.&lt;name&gt;</c>, the 8.3 shortname (first
    /// six chars + <c>~1</c>..<c>~4</c>), and the fallback hash
    /// (<c>&lt;hash&gt;~N</c>, <c>N</c> in 1..9).
    /// </summary>
    private static bool ValidateDotGitNtfsGeneric(ReadOnlySpan<byte> name, ReadOnlySpan<byte> dotgitName, ReadOnlySpan<byte> shortnamePfix)
    {
        // Literal: ".gitmodules" etc. (path.c:164-166). C reads the full
        // dotgit_len chars after the dot, so the component must be at least
        // dotgit_len + 1 bytes (a shorter component hits the NUL terminator
        // and mismatches — mirrored by the length guard).
        if (name.Length > 0 && name[0] == (byte)'.' && name.Length >= dotgitName.Length + 1 &&
            StrncasecmpBytes(name[1..], dotgitName, dotgitName.Length) == 0)
        {
            return !NtfsEndOfFilename(name[(dotgitName.Length + 1)..]);
        }

        // 8.3 shortname: first six chars of the gitfile + '~' + 1..4
        // (path.c:169-172). Requires >= 8 chars so name[6]/name[7] are in
        // bounds; a 7-char name mismatches in C at the NUL vs the 8th char.
        if (name.Length >= 8 && StrncasecmpBytes(name[..6], dotgitName[..6], 6) == 0 &&
            name[6] == (byte)'~' && name[7] is >= (byte)'1' and <= (byte)'4')
        {
            return !NtfsEndOfFilename(name[8..]);
        }

        // Fallback hash names: shortname_pfix + '~' + digit (path.c:174-193).
        bool sawTilde = false;
        for (int i = 0; i < 8; i++)
        {
            if (i >= name.Length || name[i] == 0)
            {
                return true;
            }
            else if (sawTilde)
            {
                if (name[i] is < (byte)'0' or > (byte)'9')
                {
                    return true;
                }
            }
            else if (name[i] == (byte)'~')
            {
                // C reads name[i+1]; a short name hits the NUL (mismatch).
                if (i + 1 >= name.Length || name[i + 1] is < (byte)'1' or > (byte)'9')
                {
                    return true;
                }

                sawTilde = true;
            }
            else if (i >= 6)
            {
                return true;
            }
            else if (name[i] > 127)
            {
                return true;
            }
            else if (char.ToLowerInvariant((char)name[i]) != shortnamePfix[i])
            {
                return true;
            }
        }

        return !NtfsEndOfFilename(name[Math.Min(8, name.Length)..]);
    }

    private static bool NtfsEndOfFilename(ReadOnlySpan<byte> path)
    {
        foreach (byte c in path)
        {
            if (c is (byte)'\0' or (byte)':')
            {
                return true;
            }

            if (c is not (byte)' ' and not (byte)'.')
            {
                return false;
            }
        }

        return true;
    }

    // --- Helpers ---

    /// <summary>
    /// Returns the length of the case-insensitive common prefix. Matches
    /// <c>common_prefix_icase</c> (path.c:201-213). Byte-port: ASCII-only fold.
    /// </summary>
    private static int CommonPrefixIcase(ReadOnlySpan<byte> str, ReadOnlySpan<byte> prefix)
    {
        int count = 0;
        int minLen = Math.Min(str.Length, prefix.Length);
        while (count < minLen
            && char.ToLowerInvariant((char)str[count]) == char.ToLowerInvariant((char)prefix[count]))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Upgrades the <c>.git</c> reject flags based on platform and config.
    /// Matches <c>dotgit_flags</c> (path.c:263-287).
    /// </summary>
    private static async ValueTask<GitPathRejectFlags> UpgradeDotGitFlagsAsync(GitRepository? repo, GitPathRejectFlags flags, CancellationToken cancellationToken)
    {
        flags |= GitPathRejectFlags.DotGitLiteral;

        bool protectHfs;
        bool protectNtfs;

        if (repo is null)
        {
            // C (path.c:266-287): with no repo, no config lookups happen;
            // protectHFS stays 0 (or 1 on Apple) and protectNTFS stays 1.
            protectHfs = OperatingSystem.IsMacOS();
            protectNtfs = true;
        }
        else
        {
            // C (path.c:272-277): on Apple the HFS protection is forced on
            // and core.protectHFS is NOT read; elsewhere the config default
            // is GIT_PROTECTHFS_DEFAULT = false. A lookup error leaves the
            // flag off (path.c:278's "!error" gate swallows it).
            protectHfs = OperatingSystem.IsMacOS()
                || (await TryGetBoolAsync(repo, "core.protectHFS", defaultValue: false, cancellationToken).ConfigureAwait(false) ?? false);

            // C (path.c:279-283): core.protectNTFS default
            // GIT_PROTECTNTFS_DEFAULT = true; a lookup error leaves the flag
            // off and is swallowed.
            protectNtfs = await TryGetBoolAsync(repo, "core.protectNTFS", defaultValue: true, cancellationToken).ConfigureAwait(false) ?? false;
        }

        if (protectHfs)
        {
            flags |= GitPathRejectFlags.DotGitHfs;
        }

        if (protectNtfs)
        {
            flags |= GitPathRejectFlags.DotGitNtfs;
        }

        return flags;
    }

    /// <summary>
    /// Reads a bool config value with C configmap semantics: null on a parse
    /// error (C's <c>dotgit_flags</c> swallows the lookup error and leaves
    /// the protection flag OFF — path.c:278, 283), and the given default
    /// when the key is absent. NOTE: this deliberately does NOT use
    /// <c>GitConfiguration.GetBoolAsync</c>, whose contract maps unparseable
    /// values to the default; here an unparseable value must behave like C's
    /// failed <c>git_config_parse_bool</c>.
    /// </summary>
    private static async ValueTask<bool?> TryGetBoolAsync(GitRepository repo, string key, bool defaultValue, CancellationToken cancellationToken)
    {
        GitConfigEntry? entry = await repo.Config.GetEntryAsync(key, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return defaultValue;
        }

        if (entry.Value.ValueBytes is null)
        {
            return true; // lone variable → true (git__parse_bool, util.c:649-651)
        }

        return ConfigurationValueParser.TryParseBool(entry.Value.ValueBytes.GetValueOrDefault().Span, out bool result)
            ? result
            : null; // parse error — swallowed by dotgit_flags
    }
}
