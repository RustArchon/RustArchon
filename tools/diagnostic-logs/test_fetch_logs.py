# Copyright (c) 2026 Scott Blomfield
"""Tests for fetch_logs.py against a small fake of the diagnostic log endpoint. Run: python -m unittest discover -s tools/diagnostic-logs"""

import http.server
import io
import json
import os
import tempfile
import threading
import unittest
import urllib.parse

import fetch_logs

TOKEN = "abcd1234.TESTTOKEN"


class FakeSource:
    """One source: entries numbered 1..n within an instance, with a ring capacity so old ones can be 'lost'."""

    def __init__(self, name, instance, count, capacity=10_000, available=True, reason=None):
        self.name, self.instance, self.available, self.reason = name, instance, available, reason
        self.head = count
        self.oldest = max(1, count - capacity + 1) if count else 0

    def read(self, after, count, level_min):
        if not self.available:
            return {"name": self.name, "available": False, "reason": self.reason}, []
        lost, reset = 0, False
        cursor = 0
        if after is not None:
            if after[0] != self.instance:
                reset = True
            else:
                cursor = after[1]
                lost = max(0, self.oldest - (cursor + 1))
        start = max(cursor + 1, self.oldest)
        entries = [self.entry(seq) for seq in range(start, self.head + 1) if self.entry(seq)["_l"] >= level_min]
        taken = entries[:count]
        through = taken[-1]["seq"] if len(taken) == count and len(entries) > count else self.head
        meta = {"name": self.name, "available": True, "instance": self.instance, "oldest": self.oldest, "head": self.head,
                "returnedThrough": through, "lost": lost, "reset": reset, "returned": len(taken)}
        return meta, [{k: v for k, v in e.items() if k != "_l"} for e in taken]

    def entry(self, seq):
        level = 4 if seq % 5 == 0 else 2
        return {"source": self.name, "instance": self.instance, "seq": seq, "ts": "2026-10-14T12:00:%02d.000Z" % (seq % 60),
                "level": "error" if level == 4 else "information", "category": "Cat", "message": "m%d" % seq, "props": {}, "_l": level}


class Server:
    def __init__(self, sources, token=TOKEN, status=None):
        self.sources, self.token, self.status = {s.name: s for s in sources}, token, status
        self.requests = []
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                parsed = urllib.parse.urlparse(self.path)
                outer.requests.append((parsed.path, urllib.parse.parse_qs(parsed.query), {k.lower(): v for k, v in self.headers.items()}))
                if outer.status:
                    self.send_response(outer.status)
                    self.end_headers()
                    return
                if self.headers.get("X-RustArchon-Log-Token") != outer.token:
                    self.send_response(401)
                    self.end_headers()
                    return
                if parsed.path.endswith("/help"):
                    self.send_response(200)
                    self.send_header("Content-Type", "text/plain")
                    self.end_headers()
                    self.wfile.write(b"HELP TEXT")
                    return
                query = urllib.parse.parse_qs(parsed.query)
                wanted = query.get("sources", ["api,worker"])[0].split(",")
                count = int(query.get("count", ["1000"])[0])
                level_min = {"debug": 1, "information": 2, "warning": 3, "error": 4}.get(query.get("level", ["information"])[0], 2)
                afters = {}
                for value in query.get("after", []):
                    parts = value.split(":")
                    afters[":".join(parts[:-2])] = (parts[-2], int(parts[-1]))
                summary = query.get("mode", ["entries"])[0] == "summary"
                metas, lines = [], []
                for name in wanted:
                    source = outer.sources.get(name)
                    if source is None:
                        continue
                    meta, entries = source.read(None if summary else afters.get(name), count, level_min)
                    metas.append(meta)
                    lines += entries
                if summary:
                    totals = [{"source": m["name"], "total": m.get("returned", 0), "byLevel": {"information": m.get("returned", 0)},
                               "topCategories": [{"category": "Cat", "count": m.get("returned", 0)}]} for m in metas if m.get("available")]
                    body = json.dumps({"_summary": totals}) + "\n"
                else:
                    body = "".join(json.dumps(e) + "\n" for e in lines)
                body += json.dumps({"_meta": {"returned": len(lines), "summary": summary, "sources": metas}}) + "\n"
                self.send_response(200)
                self.send_header("Content-Type", "application/x-ndjson")
                self.end_headers()
                self.wfile.write(body.encode())

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.base = "http://127.0.0.1:%d" % self.httpd.server_port
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.httpd.shutdown()
        self.httpd.server_close()


def run(argv):
    out, err = io.StringIO(), io.StringIO()
    code = fetch_logs.main(argv, stdout=out, stderr=err)
    return code, out.getvalue(), err.getvalue()


class FetchLogsTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.out = os.path.join(self.dir.name, "logs.ndjson")

    def tearDown(self):
        self.dir.cleanup()

    def entries(self):
        with open(self.out, encoding="utf-8") as f:
            return [json.loads(line) for line in f if line.strip()]

    def test_pages_every_entry_exactly_once_in_order_across_pages(self):
        with Server([FakeSource("api", "boot1", 25)]) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--count", "7", "--level", "debug", "--out", self.out])
        self.assertEqual(code, 0)
        self.assertEqual([e["seq"] for e in self.entries()], list(range(1, 26)))
        self.assertIn("25", out)

    def test_starts_from_the_oldest_entry_held_not_just_the_latest(self):
        with Server([FakeSource("api", "boot1", 100)]) as s:
            run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--count", "10", "--level", "debug", "--out", self.out])
        self.assertEqual(self.entries()[0]["seq"], 1)

    def test_several_sources_are_all_fetched(self):
        with Server([FakeSource("api", "boot1", 12), FakeSource("worker:abcd1234", "abcd1234ffff", 9)]) as s:
            run(["--base", s.base, "--token", TOKEN, "--sources", "api,worker:abcd1234", "--count", "5", "--level", "debug", "--out", self.out])
        got = self.entries()
        self.assertEqual(len([e for e in got if e["source"] == "api"]), 12)
        self.assertEqual(len([e for e in got if e["source"] == "worker:abcd1234"]), 9)

    def test_asking_for_one_source_never_returns_another_even_when_the_server_default_is_wider(self):
        with Server([FakeSource("api", "boot1", 12), FakeSource("worker", "w1", 9)]) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--count", "5", "--level", "debug", "--out", self.out])
            for _, query, _ in s.requests:
                self.assertEqual(query.get("sources"), ["api"])
        self.assertEqual(code, 0)
        self.assertEqual({e["source"] for e in self.entries()}, {"api"})

    def test_the_token_goes_in_the_header_and_never_in_the_address(self):
        with Server([FakeSource("api", "boot1", 3)]) as s:
            run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--out", self.out])
            for path, query, headers in s.requests:
                self.assertEqual(headers.get("x-rustarchon-log-token"), TOKEN)
                self.assertNotIn(TOKEN, path + str(query))

    def test_the_token_can_come_from_the_environment(self):
        os.environ["RUSTARCHON_LOG_TOKEN"] = TOKEN
        try:
            with Server([FakeSource("api", "boot1", 3)]) as s:
                code, _, _ = run(["--base", s.base, "--sources", "api", "--out", self.out])
        finally:
            del os.environ["RUSTARCHON_LOG_TOKEN"]
        self.assertEqual(code, 0)

    def test_a_missing_token_is_refused_before_any_request(self):
        os.environ.pop("RUSTARCHON_LOG_TOKEN", None)
        with Server([FakeSource("api", "boot1", 3)]) as s:
            code, _, err = run(["--base", s.base, "--sources", "api", "--out", self.out])
            self.assertNotEqual(code, 0)
            self.assertIn("token", err.lower())
            self.assertEqual(s.requests, [])

    def test_the_token_is_never_printed(self):
        with Server([FakeSource("api", "boot1", 3)]) as s:
            code, out, err = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--out", self.out])
        self.assertNotIn(TOKEN, out + err)

    def test_a_refused_token_is_reported_plainly_and_nothing_is_written(self):
        with Server([FakeSource("api", "boot1", 3)]) as s:
            code, _, err = run(["--base", s.base, "--token", "wrong", "--sources", "api", "--out", self.out])
        self.assertNotEqual(code, 0)
        self.assertIn("401", err)
        self.assertFalse(os.path.exists(self.out))

    def test_feature_off_and_throttled_and_down_are_named(self):
        for status, word in ((404, "404"), (429, "429"), (503, "503")):
            with Server([FakeSource("api", "boot1", 3)], status=status) as s:
                code, _, err = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--out", self.out])
            self.assertNotEqual(code, 0)
            self.assertIn(word, err)

    def test_a_source_that_is_unavailable_is_reported_with_its_reason_and_the_rest_still_fetched(self):
        sources = [FakeSource("api", "boot1", 4), FakeSource("worker", "x", 0, available=False, reason="rabbit down")]
        with Server(sources) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--sources", "api,worker", "--level", "debug", "--out", self.out])
        self.assertEqual(code, 0)
        self.assertEqual(len(self.entries()), 4)
        self.assertIn("rabbit down", out)

    def test_lost_entries_are_called_out(self):
        with Server([FakeSource("api", "boot1", 50, capacity=10)]) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--count", "4", "--level", "debug", "--out", self.out])
        self.assertEqual([e["seq"] for e in self.entries()][0], 41)
        self.assertIn("oldest", out.lower())

    def test_summary_mode_prints_the_counts_and_writes_no_entries(self):
        with Server([FakeSource("api", "boot1", 10)]) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--summary-only", "--out", self.out])
        self.assertEqual(code, 0)
        self.assertFalse(os.path.exists(self.out))
        self.assertIn("api", out)

    def test_help_text_can_be_fetched(self):
        with Server([FakeSource("api", "boot1", 1)]) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--help-text"])
        self.assertEqual(code, 0)
        self.assertIn("HELP TEXT", out)

    def test_the_page_limit_stops_a_runaway_fetch_and_says_so(self):
        with Server([FakeSource("api", "boot1", 1000)]) as s:
            code, out, _ = run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--count", "10", "--level", "debug", "--max-pages", "3", "--out", self.out])
        self.assertEqual(len(self.entries()), 30)
        self.assertIn("stopped", out.lower())

    def test_filters_are_passed_through(self):
        with Server([FakeSource("api", "boot1", 3)]) as s:
            run(["--base", s.base, "--token", TOKEN, "--sources", "api", "--level", "warning", "--since", "15m", "--text", "needle",
                 "--category", "RustArchon.Api", "--prop", "ServerId:abc", "--out", self.out])
            query = s.requests[-1][1]
        self.assertEqual(query["level"], ["warning"])
        self.assertEqual(query["since"], ["15m"])
        self.assertEqual(query["text"], ["needle"])
        self.assertEqual(query["category"], ["RustArchon.Api"])
        self.assertEqual(query["prop"], ["ServerId:abc"])


if __name__ == "__main__":
    unittest.main()
