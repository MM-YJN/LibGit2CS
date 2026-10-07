// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Text;

namespace LibGit2CS.IO;

/// <summary> Managed port of libgit2's precompose layer: the <c>git_fs_path_iconv</c> worker (<c>src/util/fs_path.c:1036-1088</c>) and the <c>__APPLE__</c> /
/// <c>GIT_USE_ICONV</c> gate (<c>src/util/fs_path.h:452-456</c>) that decides whether the macOS filesystem decomposition (NFD) needs to be folded back to the
/// precomposed form (NFC) at the filesystem ingress boundary. </summary> <remarks> <para> libgit2 keeps a per-iterator <c>git_fs_path_iconv_t</c> (an
/// <c>iconv_t</c> descriptor plus a scratch <c>git_str</c> output buffer) and calls <c>iconv_open("UTF-8", "UTF-8-MAC")</c> once per directory iterator,
/// applying it to each <c>de->d_name</c> in <c>git_fs_path_diriter_next</c> (<c>fs_path.c:1466-1469</c>). The descriptor and scratch buffer exist only because
/// libc <c>iconv</c> is stateful and needs a reusable output buffer. </para> <para> The .NET equivalent is stateless: <see
/// cref="string.Normalize(NormalizationForm)"/> with <see cref="NormalizationForm.FormC"/> (NFC) is a pure function with no per-call setup and no scratch
/// buffer to manage. This type therefore exposes two stateless statics — <see cref="PrecomposeCore"/> (the always-normalize worker, ports
/// <c>git_fs_path_iconv</c>) and <see cref="PrecomposeIfNeeded"/> (the platform-gated entry point, ports the <c>__APPLE__</c> / <c>GIT_USE_ICONV</c> build gate
/// as a runtime <see cref="OperatingSystem.IsMacOS"/> check). Callers that already know the platform decision (e.g. an iterator that has read
/// <c>core.precomposeunicode</c>) may call <see cref="PrecomposeCore"/> directly to bypass the repeated runtime probe; callers that want the faithful libgit2
/// default should call <see cref="PrecomposeIfNeeded"/>. </para> <para> <b>Short-circuits</b> (both faithful to <c>git_fs_path_iconv</c>,
/// <c>fs_path.c:1078</c>): pure-ASCII input is returned unchanged (no allocation, no normalization scan — <c>git_fs_path_has_non_ascii</c> equivalent);
/// already-NFC input is returned unchanged via <see cref="string.IsNormalized(NormalizationForm)"/> (avoids the allocation of <see
/// cref="string.Normalize(NormalizationForm)"/> when the input is in the desired form). Invalid byte sequences are passed through unchanged (ports
/// <c>iconv</c>'s <c>errno != E2BIG → return 0</c> path at <c>fs_path.c:1074</c>) — <see cref="string.Normalize(NormalizationForm)"/> does not throw on
/// malformed input. </para> </remarks>
internal static class PathPrecompose
{
    /// <summary>
    /// Precomposes (NFD → NFC) the given filesystem-native path unconditionally,
    /// with the ASCII / already-NFC short-circuits. Ports the worker body of
    /// <c>git_fs_path_iconv</c> (<c>fs_path.c:1036-1088</c>) — the parts inside
    /// the <c>if (!ic || ic->map == (iconv_t)-1 || !git_fs_path_has_non_ascii(...))</c>
    /// short-circuit guard and the <c>iconv</c> call itself. Use this overload
    /// when the caller has already made the platform decision (e.g. an
    /// iterator that has resolved <c>core.precomposeunicode</c>).
    /// </summary>
    /// <param name="value">The filesystem-native path (NFD on macOS HFS+/APFS, plain UTF-8 elsewhere).</param>
    /// <returns>
    /// The precomposed (NFC) string, or the input unchanged if it is pure
    /// ASCII, already NFC, or contains byte sequences that cannot be normalized.
    /// </returns>
    public static string PrecomposeCore(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // git_fs_path_has_non_ascii: pure-ASCII input is never decomposed by
        // any filesystem, so skip the normalization scan entirely.
        if (IsPureAscii(value))
        {
            return value;
        }

        // Short-circuit when the input is already in the canonical NFC form
        // (avoids the allocation of string.Normalize). There is no direct C
        // anchor — libgit2 always calls iconv, but iconv is itself a no-op for
        // already-NFC input; the IsNormalized check is a .NET-side allocation
        // saving that preserves the same observable result.
        //
        // Under <c>InvariantGlobalization</c> (the test projects' configuration),
        // <see cref="string.IsNormalized(NormalizationForm)"/> unconditionally
        // returns <see langword="true"/> and <see cref="string.Normalize(NormalizationForm)"/>
        // is a no-op; this matches iconv's silent-passthrough behavior on
        // platforms without a usable conversion table (fs_path.c:1074), so the
        // graceful-degradation path is the same in both cases.
        if (value.IsNormalized(NormalizationForm.FormC))
        {
            return value;
        }

        // iconv(UTF-8-MAC -> UTF-8). string.Normalize(FormC) is the .NET BCL
        // equivalent: it produces the canonical precomposed UTF-16 form, which
        // is then re-encoded as UTF-8 by GitPath.FromUtf8String. iconv returns
        // the original data unchanged for non-E2BIG failures (fs_path.c:1074);
        // string.Normalize does not throw, so the silent-passthrough behavior
        // is preserved.
        return value.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Precomposes the given filesystem-native path if and only if the current
    /// runtime is macOS. Ports the <c>__APPLE__</c> compile-time gate that
    /// selects <c>GIT_PATH_NATIVE_ENCODING = "UTF-8-MAC"</c> (decomposed) on
    /// macOS and <c>"UTF-8"</c> everywhere else (<c>fs_path.h:452-456</c>). On
    /// Linux/Windows the native encoding is already NFC, so no transcode is
    /// needed and the input is returned unchanged.
    /// </summary>
    /// <remarks>
    /// Callers that have already resolved the platform decision (e.g. an
    /// iterator holding a <c>core.precomposeunicode</c>-derived bool) should
    /// call <see cref="PrecomposeCore"/> directly to skip the per-call runtime
    /// probe; this entry point is for one-shot callers (e.g. <c>git_blob_create_from_disk</c>'s
    /// hint-path derivation at <c>blob.c:290-296</c>) that want the faithful
    /// libgit2 default.
    /// </remarks>
    /// <param name="value">The filesystem-native path.</param>
    /// <returns>The precomposed path on macOS; the input unchanged elsewhere.</returns>
    public static string PrecomposeIfNeeded(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OperatingSystem.IsMacOS() ? PrecomposeCore(value) : value;
    }

    /// <summary>
    /// True if <paramref name="value"/> contains only ASCII characters (all
    /// <c>char</c>s &lt;= 0x7F). Ports <c>git_fs_path_has_non_ascii</c>
    /// (<c>fs_path.c:1010-1016</c>): pure-ASCII paths can never be decomposed
    /// by any filesystem and skip the iconv call entirely.
    /// </summary>
    private static bool IsPureAscii(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F)
            {
                return false;
            }
        }

        return true;
    }
}
