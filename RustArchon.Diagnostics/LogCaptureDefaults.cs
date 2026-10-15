// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>
/// What the diagnostic buffer leaves out by default. At Debug a busy process writes thousands of framework lines (every SQL command, every socket,
/// every routing decision) in seconds, which would push out the entries that explain a problem within minutes. Our own code is kept at Debug and
/// above; everything else is kept from Warning up (the framework saying something is wrong), except the host's own lifetime messages, which mark
/// when a process started.
/// </summary>
public static class LogCaptureDefaults
{
    private static readonly string[] Ours = ["RustArchon", "JumpStart"];

    public static bool IsFrameworkNoise(string category, LogLevel level)
    {
        if (level >= LogLevel.Warning)
        {
            return false;
        }

        foreach (var prefix in Ours)
        {
            if (category.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // The process starting and stopping is worth keeping.
        return !(category.StartsWith("Microsoft.Hosting.", StringComparison.Ordinal) && level >= LogLevel.Information);
    }
}
