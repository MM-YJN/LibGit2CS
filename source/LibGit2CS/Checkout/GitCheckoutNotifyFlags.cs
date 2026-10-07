// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

namespace LibGit2CS.Checkout;

/// <summary>
/// Checkout notification flags. Maps 1:1 to <c>git_checkout_notify_t</c> in
/// <c>include/git2/checkout.h:72-83</c>.
/// </summary>
[Flags]
public enum GitCheckoutNotifyFlags
{
    /// <summary>No notifications. (<c>GIT_CHECKOUT_NOTIFY_NONE = 0</c>)</summary>
    None = 0,

    /// <summary>Notify on conflicts. (<c>GIT_CHECKOUT_NOTIFY_CONFLICT = 1u &lt;&lt; 0</c>)</summary>
    Conflict = 1 << 0,

    /// <summary>Notify on dirty files (modified, not matching baseline). (<c>GIT_CHECKOUT_NOTIFY_DIRTY = 1u &lt;&lt; 1</c>)</summary>
    Dirty = 1 << 1,

    /// <summary>Notify on any updated file. (<c>GIT_CHECKOUT_NOTIFY_UPDATED = 1u &lt;&lt; 2</c>)</summary>
    Updated = 1 << 2,

    /// <summary>Notify on untracked files. (<c>GIT_CHECKOUT_NOTIFY_UNTRACKED = 1u &lt;&lt; 3</c>)</summary>
    Untracked = 1 << 3,

    /// <summary>Notify on ignored files. (<c>GIT_CHECKOUT_NOTIFY_IGNORED = 1u &lt;&lt; 4</c>)</summary>
    Ignored = 1 << 4,

    /// <summary>All notifications. (<c>GIT_CHECKOUT_NOTIFY_ALL</c>)</summary>
    All = Conflict | Dirty | Updated | Untracked | Ignored,
}
