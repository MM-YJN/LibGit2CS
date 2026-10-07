// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Buffers;
using System.Text;

using LibGit2CS.Core;

namespace LibGit2CS.Config;

/// <summary>
/// Type-safe configmap helper for mapping config string values to enum or
/// numeric values. Managed equivalent of libgit2's <c>git_configmap</c> +
/// <c>git_config_lookup_map_value</c>.
/// </summary>
/// <typeparam name="T">The result type (typically an enum).</typeparam>
public sealed record GitConfigurationMap<T>(IReadOnlyList<GitConfigurationMapItem<T>> Items)
{
    /// <summary>
    /// Looks up <paramref name="value"/> against this map's items and returns
    /// the mapped result. Matches libgit2's <c>git_config_lookup_map_value</c>.
    /// </summary>
    /// <exception cref="GitException">
    /// <see cref="GitErrorCode.Error"/> if no map item matches <paramref name="value"/>.
    /// </exception>
    public T Lookup(string? value)
    {
        foreach (GitConfigurationMapItem<T> item in Items)
        {
            switch (item.Type)
            {
                case GitConfigurationMapType.False:
                case GitConfigurationMapType.True:
                    if (ConfigurationValueParser.TryParseBool(value, out bool boolVal)
                        && boolVal == (item.Type == GitConfigurationMapType.True))
                    {
                        return item.Value;
                    }

                    break;
                case GitConfigurationMapType.Int32:
                    // C (config.c:1402-1405): the parsed integer is returned,
                    // not the item's construction-time value.
                    if (ConfigurationValueParser.TryParseInt32(value, out int parsedInt))
                    {
                        return (T)(object)parsedInt;
                    }

                    break;
                case GitConfigurationMapType.String:
                    if (value is not null
                        && string.Equals(value, item.StringMatch, StringComparison.OrdinalIgnoreCase))
                    {
                        return item.Value;
                    }

                    break;
            }
        }

        throw new GitException(
            GitErrorCode.Error,
            $"failed to map '{value ?? "(null)"}'",
            GitErrorCategory.Config);
    }

    /// <summary> Byte-domain <see cref="Lookup(string?)"/> over the raw config value bytes. </summary> <exception cref="GitException"> <see
    /// cref="GitErrorCode.Error"/> if no map item matches <paramref name="value"/>. </exception>
    public T Lookup(ReadOnlySpan<byte> value)
    {
        foreach (GitConfigurationMapItem<T> item in Items)
        {
            switch (item.Type)
            {
                case GitConfigurationMapType.False:
                case GitConfigurationMapType.True:
                    if (ConfigurationValueParser.TryParseBool(value, out bool boolVal)
                        && boolVal == (item.Type == GitConfigurationMapType.True))
                    {
                        return item.Value;
                    }

                    break;
                case GitConfigurationMapType.Int32:
                    // C (config.c:1402-1405): the parsed integer is returned,
                    // not the item's construction-time value.
                    if (ConfigurationValueParser.TryParseInt32(value, out int parsedInt))
                    {
                        return (T)(object)parsedInt;
                    }

                    break;
                case GitConfigurationMapType.String:
                    if (item.StringMatch is not null)
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(item.StringMatch));
                        int bytesWritten = Encoding.UTF8.GetBytes(item.StringMatch, buffer);

                        if (ConfigKeyName.AsciiEqualsIgnoreCase(value, buffer.AsSpan(0, bytesWritten)))
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            return item.Value;
                        }

                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    break;
            }
        }

        throw new GitException(
            GitErrorCode.Error,
            $"failed to map '{Encoding.UTF8.GetString(value)}'",
            GitErrorCategory.Config);
    }
}
