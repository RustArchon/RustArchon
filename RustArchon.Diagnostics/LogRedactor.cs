// Copyright ©2026 Scott Blomfield

using System.Text.RegularExpressions;

namespace RustArchon.Diagnostics;

/// <summary>
/// Removes secret-looking values from log text before it is stored or returned. Pattern based, so it cannot be proven complete:
/// it errs towards masking too much (a value after any name containing "key" or "token" goes), keeps the name so the line stays
/// readable, and is idempotent. The plugin carries its own small copy of these rules (it is one compiled file and cannot reference this
/// library); both are held to the same vectors by tests.
/// </summary>
public static class LogRedactor
{
    public const string Mask = "[REDACTED]";

    /// <summary>Returned instead of the text if redaction itself runs out of time: a fully masked line is safe, an unmasked one is not.</summary>
    public const string Failed = "[REDACTION FAILED]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // name (containing a secret-ish word), separator, then the value: quoted (may hold spaces) or a run up to a delimiter. A Bearer/Basic
    // scheme in front belongs to the value. Never re-matches an already masked value.
    private static readonly Regex NameValue = new(
        """(?<name>[\w\-.]{0,64}(?:password|passwd|pwd|secret|token|apikey|api[-_]?key|credential|authorization|connectionstring|pass|cookie|session|signature|sig|auth|private|key)[\w\-.]{0,64})(?<sep>"?\s*[:=]\s*)(?!"?\[REDACTED\])(?<value>"(?:[^"\\]|\\.)*"|(?:(?:Bearer|Basic)\s+)?[^\s"&;,'}\]]+)""",
        Options, MatchTimeout);

    // A Cookie / Set-Cookie header carries several name=value pairs separated by semicolons, so the whole line goes.
    private static readonly Regex CookieLine = new(
        """(?<name>\b(?:set-)?cookie)(?<sep>[ \t]*:[ \t]*)(?!\[REDACTED\])(?<value>[^\r\n]+)""", Options, MatchTimeout);

    private static readonly Regex BearerScheme = new("""(?<scheme>\bBearer\s+)(?!\[REDACTED\])[\w\-.~+/=]+""", Options, MatchTimeout);

    private static readonly Regex Jwt = new("""\beyJ[\w\-]+\.[\w\-]+\.[\w\-]*""", Options, MatchTimeout);

    private static readonly Regex PemPrivateKey = new(
        """-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----""", Options, MatchTimeout);

    private static readonly Regex UrlUserInfo = new("""(?<scheme>://)[^/\s:@]+:[^/\s@]+(?=@)""", Options, MatchTimeout);

    // The per-server tokens that ride in the address (ADR-0001 and the plugin download).
    private static readonly Regex TokenInPath = new(
        """(?<prefix>/ingest/(?:reports|plugin)/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/)(?!\[REDACTED\])[^\s/?#]+""",
        Options, MatchTimeout);

    // Single-use tokens that follow an RCON update/upload command as its last argument.
    private static readonly Regex UpdateCommandToken = new(
        """(?<prefix>\barchon\.(?:updater\.)?update[ \t]+\S+[ \t]+\S+[ \t]+)(?!\[REDACTED\])\S+""", Options, MatchTimeout);

    private static readonly Regex MapUploadToken = new(
        """(?<prefix>\barchon\.map\.upload[ \t]+\S+[ \t]+)(?!\[REDACTED\])\S+""", Options, MatchTimeout);

    // A WebRCON address carries the RCON password as its path.
    private static readonly Regex WebRconPassword = new("""(?<prefix>\bwss?://[^/\s]+/)(?!\[REDACTED\])\S+""", Options, MatchTimeout);

    // Anything long, opaque and mixed letters and digits is treated as a token (ours are 43 characters). Plain hex (hashes) and long
    // names with no digits are left alone.
    private static readonly Regex OpaqueRun = new("""(?<![\w\-])[A-Za-z0-9_\-]{40,}(?![\w\-])""", Options, MatchTimeout);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            var result = PemPrivateKey.Replace(text, Mask);
            result = CookieLine.Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + Mask);
            result = NameValue.Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + MaskedLike(m.Groups["value"].Value));
            result = BearerScheme.Replace(result, m => m.Groups["scheme"].Value + Mask);
            result = Jwt.Replace(result, Mask);
            result = UrlUserInfo.Replace(result, m => m.Groups["scheme"].Value + Mask);
            result = TokenInPath.Replace(result, m => m.Groups["prefix"].Value + Mask);
            result = UpdateCommandToken.Replace(result, m => m.Groups["prefix"].Value + Mask);
            result = MapUploadToken.Replace(result, m => m.Groups["prefix"].Value + Mask);
            result = WebRconPassword.Replace(result, m => m.Groups["prefix"].Value + Mask);
            result = OpaqueRun.Replace(result, m => LooksLikeToken(m.Value) ? Mask : m.Value);
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return Failed;
        }
    }

    private static string MaskedLike(string value) => value.StartsWith('"') ? "\"" + Mask + "\"" : Mask;

    private static bool LooksLikeToken(string run)
    {
        var hasLetter = false;
        var hasDigit = false;
        var allHex = true;
        foreach (var c in run)
        {
            hasLetter |= char.IsLetter(c);
            hasDigit |= char.IsDigit(c);
            allHex &= Uri.IsHexDigit(c);
        }

        return hasLetter && hasDigit && !allHex;
    }
}
