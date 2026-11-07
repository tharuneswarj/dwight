#!/usr/bin/env python3
"""The shipped control for tools/check-python-names.py's helper self-test.

That gate takes every block fenced by the pure-helper markers out of tools/*.py, runs it with nothing imported, and calls
its selftest(). The probes that carry real blocks need Rhino and stay in the development repository; this file ships in
both trees, so the extraction is exercised wherever the gate runs. Nothing runs this file on its own.
"""

# --- pure-helpers-begin ---
def plural(n, word):
    """A count and its noun."""
    return "%d %s%s" % (n, word, "" if n == 1 else "s")


def selftest():
    cases = [((1, "file"), "1 file"), ((2, "file"), "2 files"), ((0, "problem"), "0 problems")]
    bad = [(args, want, plural(*args)) for args, want in cases if plural(*args) != want]
    return "FAIL %r" % bad if bad else "OK %d cases" % len(cases)
# --- pure-helpers-end ---
