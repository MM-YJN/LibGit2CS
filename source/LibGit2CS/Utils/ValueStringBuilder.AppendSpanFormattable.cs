// SPDX-License-Identifier: MIT
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Copyright (c) .NET Foundation and Contributors
// Modifications Copyright (c) 2026 LibGit2CS contributors.
// See LICENSES/dotnet-runtime-MIT.txt and THIRD-PARTY-NOTICES.txt at the repository root.

namespace LibGit2CS.Utils;

internal ref partial struct ValueStringBuilder
{
    internal void AppendSpanFormattable<T>(T value, string? format = null, IFormatProvider? provider = null) where T : ISpanFormattable
    {
        if (value.TryFormat(_chars.Slice(_pos), out int charsWritten, format, provider))
        {
            _pos += charsWritten;
        }
        else
        {
            Append(value.ToString(format, provider));
        }
    }
}
