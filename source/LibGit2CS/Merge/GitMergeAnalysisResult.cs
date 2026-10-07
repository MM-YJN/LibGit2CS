// SPDX-License-Identifier: MIT
// Copyright (c) 2026 LibGit2CS contributors.
// Licensed under the MIT license. See LICENSES/MIT.txt at the repository root.

namespace LibGit2CS.Merge;

/// <summary>
/// Result of a merge analysis. Matches the two output parameters of
/// <c>git_merge_analysis</c> / <c>git_merge_analysis_for_ref</c>.
/// </summary>
public sealed record GitMergeAnalysisResult(GitMergeAnalysis Analysis, GitMergePreference Preference);
