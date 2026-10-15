// Copyright ©2026 Scott Blomfield

namespace RustArchon.Diagnostics;

/// <summary>The self-description the diagnostic log endpoint returns from its help address.</summary>
public static class LogHelp
{
    public const string Text = """
        RustArchon diagnostic logs (read only)
        ======================================

        GET {base}/diagnostics/logs        entries (newline-delimited JSON)
        GET {base}/diagnostics/logs/help   this text

        Authentication: header   X-RustArchon-Log-Token: <token>
        No token created -> 404. Any missing/wrong/replaced/revoked token -> 401 (identical). Never put the token in the URL.

        Sources (parameter "sources", comma separated or repeated; default api,worker,panel)
          api                         the Api process
          worker                      every Worker instance the Api has heard from (each is its own source: worker:<8 chars>)
          panel                       the Panel process
          plugin:<server id>          the RustArchon plugin on that game server, fetched on demand over RCON (never in the default set)

        Filters
          level=debug|information|warning|error|critical|0-5    minimum level (default information). Debug is captured on every source.
          since=15m|2h|1d|90s          window ending now        from=<ISO 8601>  to=<ISO 8601>   (since and from cannot be combined)
          category=<prefix>            logger category prefix, case-insensitive (e.g. RustArchon.Api.Services)
          text=<substring>             in message, exception or category, case-insensitive
          prop=Key:Value               structured property, repeatable, all must match (e.g. prop=ServerId:<guid>  prop=Plugin:Foo)
          count=<n>                    most entries PER SOURCE (default 1000, hard cap 5000)
          mode=entries|summary         mode=summary returns counts by source, level and category and no entries (good first look)

        Output (application/x-ndjson, one JSON object per line, ascending by time)
          entry: {"source","instance","seq","ts","level","category","message","exception"?,"props":{}}
          then one final line {"_meta":{"generated","summary","returned","sources":[ ... ]}}   (summary mode: a {"_summary":[...]} line first)
          each _meta source: name, available, and either
              instance, oldest, head, returnedThrough, lost, reset, returned, warning?     (available)
              reason                                                                       (not available - read it: it says why)

        Paging without missing anything
          Every source numbers its own entries 1,2,3... (seq) within one run of the process (instance). The first request (no "after") returns the
          most recent entries. To continue, pass for each source:   after=<name>:<instance>:<returnedThrough>   (repeat the parameter per source)
          Example:  after=api:9f3c2a...:48213  after=worker:ab12cd34:ab12cd34ef...:1177
          Each response says for each source:
              lost   > 0   entries after your cursor are gone (the buffer wrapped, or a Worker batch never arrived). You missed exactly that many.
              reset  true  the process restarted (new instance); your cursor was ignored and you got everything held. Sequence numbers start over.
              returnedThrough   the cursor to send next time; it advances past entries your filters skipped, so filtered paging never rescans.
          A page that returns nothing with lost=0 means you are caught up for that source.

        Things to know
          - Buffers are in memory (about 5000 entries per source) and are lost on restart.
          - Worker logs reach the Api over RabbitMQ. A Worker source that is missing, unavailable or "quiet" may mean RabbitMQ is the problem, not
            that the Worker has nothing to say.
          - plugin:<id> is unavailable (with a reason) when the plugin is not installed, too old to serve logs, or the server is not connected.
          - Secrets are redacted before storage and again on output; a value shown as [REDACTED] is deliberate.

        Example
          curl -sS -H "X-RustArchon-Log-Token: $TOKEN" "{base}/diagnostics/logs?sources=api,worker&level=warning&since=1h"
        """;
}
