// Copyright ©2026 Scott Blomfield

namespace RustArchon.Diagnostics;

/// <summary>
/// What one read of a <see cref="LogRing"/> returned and where it left off.
/// </summary>
/// <param name="Entries">The matching entries, ascending by sequence.</param>
/// <param name="InstanceId">The run of the process these sequence numbers belong to.</param>
/// <param name="Oldest">The lowest sequence still held (0 when empty).</param>
/// <param name="Head">The highest sequence ever assigned (0 when none).</param>
/// <param name="ReturnedThrough">The cursor to pass next time: every entry up to and including this sequence has been examined.</param>
/// <param name="Lost">Entries after the caller's cursor that are no longer held.</param>
/// <param name="Reset">The caller's cursor belonged to a different run of the process, so it was ignored and reading started from the oldest held entry.</param>
public sealed record LogPage(
    IReadOnlyList<LogEntry> Entries,
    string InstanceId,
    long Oldest,
    long Head,
    long ReturnedThrough,
    long Lost,
    bool Reset);
