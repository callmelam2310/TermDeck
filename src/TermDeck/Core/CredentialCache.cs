using System;
using System.Collections.Generic;

namespace TermDeck.Core;

/// <summary>
/// Remembers "run as user" passwords for the lifetime of the process only, when the user ticks "remember".
/// Nothing is ever written to disk — the config never contains a password.
/// </summary>
public static class CredentialCache
{
    static readonly Dictionary<string, string> _byAccount = new(StringComparer.OrdinalIgnoreCase);

    static string Key(string account) => account.Trim();

    public static bool TryGet(string account, out string password) =>
        _byAccount.TryGetValue(Key(account), out password!);

    public static void Remember(string account, string password) => _byAccount[Key(account)] = password;

    public static void Forget(string account) => _byAccount.Remove(Key(account));

    public static void Clear() => _byAccount.Clear();
}
