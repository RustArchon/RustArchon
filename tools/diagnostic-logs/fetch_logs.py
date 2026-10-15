#!/usr/bin/env python3
# Copyright (c) 2026 Scott Blomfield
"""Fetches RustArchon's read-only diagnostic logs (ADR-0006) into a local file and prints only a short summary.

Pages through the endpoint's per-source cursors from the oldest entry held to the newest, so nothing is missed and nothing is read twice, and says
plainly when entries were lost (the buffer wrapped, or a Worker batch never arrived), when a source was unavailable and why, and when a process
restarted mid-fetch. The output file holds one JSON entry per line; read it with grep/jq/anything, or ask for a narrower slice and run again.

The token is a header, never part of the address, and is never printed. Give it with --token or the RUSTARCHON_LOG_TOKEN environment variable.

    python fetch_logs.py --base https://panel.example.com --sources api,worker,panel --level warning --since 2h --out logs.ndjson
    python fetch_logs.py --base https://panel.example.com --summary-only --since 1h
    python fetch_logs.py --base https://panel.example.com --help-text
"""

import argparse
import collections
import datetime
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

TOKEN_HEADER = "X-RustArchon-Log-Token"

HTTP_EXPLANATIONS = {
    400: "the request was refused as malformed",
    401: "the token was refused (wrong, replaced or revoked)",
    404: "the diagnostic log address does not exist (no token has been created, or the base address is wrong)",
    429: "too many refused requests from this address; wait a minute",
    502: "the Panel could not understand the Api's answer",
    503: "the Api cannot be reached, so the token could not be checked",
}


class FetchError(Exception):
    pass


def main(argv=None, stdout=None, stderr=None):
    stdout = stdout or sys.stdout
    stderr = stderr or sys.stderr
    args = build_parser().parse_args(argv)

    token = args.token or os.environ.get("RUSTARCHON_LOG_TOKEN")
    if not token:
        print("error: no token. Give --token or set RUSTARCHON_LOG_TOKEN (it is sent as a header, never in the address).", file=stderr)
        return 2
    base = args.base or os.environ.get("RUSTARCHON_LOG_BASE")
    if not base:
        print("error: no --base address (for example https://panel.example.com).", file=stderr)
        return 2

    client = Client(base.rstrip("/"), token, args.timeout)
    try:
        if args.help_text:
            print(client.get_text("/diagnostics/logs/help"), file=stdout)
            return 0
        return run(args, client, stdout)
    except FetchError as error:
        print("error: %s" % error, file=stderr)
        return 1


def build_parser():
    p = argparse.ArgumentParser(description="Fetch RustArchon diagnostic logs into a file.")
    p.add_argument("--base", help="the Panel's public address, e.g. https://panel.example.com (or RUSTARCHON_LOG_BASE)")
    p.add_argument("--token", help="the log token (or RUSTARCHON_LOG_TOKEN)")
    p.add_argument("--sources", default="api,worker,panel", help="comma list: api, worker, panel, plugin:<server id>")
    p.add_argument("--level", default="information", help="minimum level: debug, information, warning, error, critical")
    p.add_argument("--since", help="a window ending now: 90s, 15m, 2h, 1d")
    p.add_argument("--from", dest="from_", help="ISO 8601 start")
    p.add_argument("--to", help="ISO 8601 end")
    p.add_argument("--category", help="logger category prefix")
    p.add_argument("--text", help="substring in message, exception or category")
    p.add_argument("--prop", action="append", default=[], help="structured property filter Key:Value (repeatable)")
    p.add_argument("--count", type=int, default=1000, help="entries per source per request (the server caps it at 5000)")
    p.add_argument("--out", help="output file (default logs-<UTC timestamp>.ndjson)")
    p.add_argument("--summary-only", action="store_true", help="print counts by source/level/category and write no entries")
    p.add_argument("--help-text", action="store_true", help="print the endpoint's own help and exit")
    p.add_argument("--max-pages", type=int, default=200, help="most requests to make while paging")
    p.add_argument("--timeout", type=float, default=60.0)
    return p


class Client:
    def __init__(self, base, token, timeout):
        self.base, self.token, self.timeout = base, token, timeout

    def get_text(self, path, params=()):
        url = self.base + path + (("?" + urllib.parse.urlencode(list(params))) if params else "")
        request = urllib.request.Request(url, headers={TOKEN_HEADER: self.token, "User-Agent": "rustarchon-fetch-logs/1"})
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            detail = HTTP_EXPLANATIONS.get(error.code, "unexpected answer")
            body = ""
            if error.code == 400:
                body = ": " + error.read().decode("utf-8", "replace").strip()
            error.close()
            raise FetchError("HTTP %d - %s%s" % (error.code, detail, body))
        except urllib.error.URLError as error:
            raise FetchError("could not connect to %s: %s" % (self.base, error.reason))

    def get_ndjson(self, params):
        entries, meta, summary = [], None, None
        for line in self.get_text("/diagnostics/logs", params).splitlines():
            if not line.strip():
                continue
            try:
                item = json.loads(line)
            except json.JSONDecodeError:
                raise FetchError("the answer was not newline-delimited JSON")
            if "_meta" in item:
                meta = item["_meta"]
            elif "_summary" in item:
                summary = item["_summary"]
            else:
                entries.append((line, item))
        if meta is None:
            raise FetchError("the answer had no _meta line, so it cannot be trusted as complete")
        return entries, meta, summary


def filter_params(args):
    params = [("sources", args.sources), ("level", args.level), ("count", str(args.count))]
    for name, value in (("since", args.since), ("from", args.from_), ("to", args.to), ("category", args.category), ("text", args.text)):
        if value:
            params.append((name, value))
    params += [("prop", p) for p in args.prop]
    return params


def run(args, client, stdout):
    base_params = filter_params(args)

    _, meta, summary = client.get_ndjson(base_params + [("mode", "summary")])
    if args.summary_only:
        print_summary(summary, meta, stdout)
        return 0

    out_path = args.out or "logs-%s.ndjson" % datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    state = {}
    for source in meta["sources"]:
        if not source.get("available"):
            state[source["name"]] = Result(source["name"], reason=source.get("reason", "unavailable"))
        else:
            state[source["name"]] = Result(source["name"], instance=source["instance"], cursor=0)

    levels = collections.Counter()
    pages, stopped = 0, False
    with open(out_path, "w", encoding="utf-8") as out:
        while any(r.active for r in state.values()):
            if pages >= args.max_pages:
                stopped = True
                break
            pages += 1
            params = [p for p in base_params if p[0] != "sources"] + [("sources", ",".join(n for n, r in state.items() if r.active))]
            params += [("after", "%s:%s:%d" % (r.name, r.instance, r.cursor)) for r in state.values() if r.active]
            entries, page_meta, _ = client.get_ndjson(params)
            for line, item in entries:
                out.write(line + "\n")
                levels[item.get("level", "?")] += 1
                state[item["source"]].fetched += 1
            for source in page_meta["sources"]:
                state[source["name"]].absorb(source)

    print_report(state, levels, out_path, pages, stopped, stdout)
    return 0


class Result:
    def __init__(self, name, instance=None, cursor=0, reason=None):
        self.name, self.instance, self.cursor, self.reason = name, instance, cursor, reason
        self.active = reason is None
        self.fetched = 0
        self.lost = 0
        self.resets = 0
        self.oldest = None
        self.head = None
        self.warning = None
        self.first = True

    def absorb(self, meta):
        if not meta.get("available"):
            self.active, self.reason = False, meta.get("reason", "unavailable")
            return
        self.warning = meta.get("warning") or self.warning
        self.lost += meta.get("lost", 0)
        if self.first:
            self.oldest = meta.get("oldest")
            self.first = False
        self.head = meta.get("head")
        if meta.get("reset"):
            # The process restarted while we were reading: start over on the new run rather than skip or repeat.
            self.resets += 1
            self.instance, self.cursor = meta["instance"], 0
            self.active = self.resets <= 3
            return
        through = meta.get("returnedThrough", self.cursor)
        progressed = through > self.cursor
        self.cursor = through
        self.active = progressed and through < meta.get("head", through)


def print_summary(summary, meta, stdout):
    for source in meta["sources"]:
        if not source.get("available"):
            print("%s: UNAVAILABLE - %s" % (source["name"], source.get("reason")), file=stdout)
    for item in summary or []:
        levels = ", ".join("%s %d" % (k, v) for k, v in item.get("byLevel", {}).items())
        print("%s: %d entries (%s), %s to %s" % (item["source"], item["total"], levels, item.get("first", "-"), item.get("last", "-")), file=stdout)
        for category in item.get("topCategories", [])[:10]:
            print("    %6d  %s" % (category["count"], category["category"]), file=stdout)


def print_report(state, levels, out_path, pages, stopped, stdout):
    total = sum(r.fetched for r in state.values())
    print("Wrote %d entries to %s (%d request%s)." % (total, out_path, pages, "" if pages == 1 else "s"), file=stdout)
    for r in state.values():
        if r.reason is not None:
            print("  %s: UNAVAILABLE - %s" % (r.name, r.reason), file=stdout)
            continue
        line = "  %s: %d entries" % (r.name, r.fetched)
        if r.oldest is not None:
            line += " (the oldest held was #%s, newest #%s)" % (r.oldest, r.head)
        print(line, file=stdout)
        if r.lost:
            print("    LOST %d entries: they were gone before they could be read (the buffer wrapped, or a Worker batch never arrived)." % r.lost, file=stdout)
        if r.resets:
            print("    the process restarted %d time(s) while reading; sequence numbers started over." % r.resets, file=stdout)
        if r.warning:
            print("    warning: %s" % r.warning, file=stdout)
    if levels:
        print("  by level: " + ", ".join("%s %d" % (k, v) for k, v in levels.most_common()), file=stdout)
    if stopped:
        print("  STOPPED at the page limit; run again with a narrower filter or a higher --max-pages for the rest.", file=stdout)


if __name__ == "__main__":
    sys.exit(main())
