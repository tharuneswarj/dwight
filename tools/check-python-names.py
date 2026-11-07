#!/usr/bin/env python3
"""Undefined-name gate for the repo's Python tools (#334).

WHY THIS EXISTS. tools/probe-deploy.py is the one instrument in this project CI can never run, because it
needs Rhino. Its correctness rests on review alone - which is exactly the property that lets an instrument
fail quietly. Updating it for the #291/D1 cycle I wrote `flags` where the constant is `FLAGS`; ast.parse
accepts that happily, and it would have thrown NameError on the test machine, reporting as "the probe
errored" rather than as a build verdict. That kills BOTH halves of the probe at once - the symbol checks
AND the timestamp - which is precisely what the two-instrument design was meant to prevent.

C# has a compiler. The shells got one at #275 and again at #317. This is the same remedy for the third
language in the repo: the check a Python script never gets until it runs, run at authorship instead.

WHAT IT CHECKS, and deliberately no more: every name READ must be bound somewhere that could plausibly
reach it - builtins, module scope, or an enclosing function. It is not a type checker and not a linter; it
catches the typo class that costs a run, and stays quiet otherwise.

Scope model, stated because it is an approximation: a function's bindings are treated as visible anywhere
inside it (which is how Python locals actually behave) and are inherited by nested functions. Class bodies
are treated as their own scope. Comprehension, `with`, `for` and `except` targets all bind.
"""
import ast
import builtins
import glob
import os
import sys

ROOTS = ["tools", "Dwight"]
SKIP_DIRS = {"bin", "obj", "node_modules", ".git", ".vs", "__pycache__"}


SCOPE_NODES = (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef, ast.Lambda)


def iter_own(node):
    """Every node belonging to THIS scope, not descending into nested ones.

    The distinction is the whole correctness of this gate: a nested function's parameters are bound in the
    nested scope, so checking its body against the parent's names reports every parameter as undefined.
    (That is exactly what the first run of this file did - 17 findings, all of them the instrument's.)
    """
    stack = list(ast.iter_child_nodes(node))
    while stack:
        n = stack.pop()
        yield n
        if not isinstance(n, SCOPE_NODES):
            stack.extend(ast.iter_child_nodes(n))


def bindings_of(node):
    """Names bound directly by this node (not descending into nested scopes)."""
    found = set()

    def targets(t):
        if isinstance(t, ast.Name):
            found.add(t.id)
        elif isinstance(t, (ast.Tuple, ast.List)):
            for e in t.elts:
                targets(e)
        elif isinstance(t, ast.Starred):
            targets(t.value)

    for child in iter_own(node):
        if isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            found.add(child.name)
        elif isinstance(child, (ast.Import, ast.ImportFrom)):
            for a in child.names:
                found.add((a.asname or a.name).split(".")[0])
        elif isinstance(child, ast.Assign):
            for t in child.targets:
                targets(t)
        elif isinstance(child, (ast.AnnAssign, ast.AugAssign)):
            targets(child.target)
        elif isinstance(child, (ast.For, ast.AsyncFor)):
            targets(child.target)
        elif isinstance(child, ast.comprehension):
            targets(child.target)
        elif isinstance(child, ast.withitem):
            if child.optional_vars is not None:
                targets(child.optional_vars)
        elif isinstance(child, ast.ExceptHandler):
            if child.name:
                found.add(child.name)
        elif isinstance(child, (ast.Global, ast.Nonlocal)):
            found.update(child.names)
        elif isinstance(child, ast.Lambda):
            args = child.args
            for a in list(args.args) + list(args.posonlyargs) + list(args.kwonlyargs):
                found.add(a.arg)
            if args.vararg:
                found.add(args.vararg.arg)
            if args.kwarg:
                found.add(args.kwarg.arg)

    return found


def params_of(fn):
    args = fn.args
    names = {a.arg for a in list(args.args) + list(args.posonlyargs) + list(args.kwonlyargs)}
    if args.vararg:
        names.add(args.vararg.arg)
    if args.kwarg:
        names.add(args.kwarg.arg)
    return names


def check(path):
    src = open(path, encoding="utf-8").read()
    try:
        tree = ast.parse(src, filename=path)
    except SyntaxError as e:
        return [(e.lineno or 0, "syntax error: " + str(e.msg))]

    problems = []

    # Module scope, then each function/class body as its own scope inheriting the module's.
    module_names = set(dir(builtins)) | {"__file__", "__name__", "__doc__", "__builtins__"}
    module_names |= bindings_of(tree)

    def scope(node, visible):
        own = set(visible)
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda)):
            own |= params_of(node)
        own |= bindings_of(node)

        # This scope's own names only - a nested scope is checked with ITS bindings, below.
        for n in iter_own(node):
            if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Load) and n.id not in own:
                problems.append((n.lineno, "undefined name " + repr(n.id)))
                own.add(n.id)   # report each name once per scope, not once per use

        for child in iter_own(node):
            if isinstance(child, SCOPE_NODES):
                scope(child, own)

    scope(tree, module_names)
    return sorted(set(problems))


BEGIN, END = "# --- pure-helpers-begin ---", "# --- pure-helpers-end ---"


def probe_selftest():
    """
    THE SECOND GATE (#695): run every probe's own helper self-test.

    A probe cannot run in CI - it needs Rhino - and that is exactly how one failed on the test machine: a row threw
    AmbiguousMatchException on an overloaded method name and took the whole run, verdict included, with it. The logic
    that must not break is written PURE and fenced by markers, so this gate extracts each block, executes it with no
    Rhino at all, and runs its self-test.

    IT DISCOVERS THE BLOCKS RATHER THAN NAMING ONE (#741). This used to point at a single hard-coded probe, so the
    second probe to carry markers would have been gated by nobody: its fenced block would sit there looking like a
    guard, running never. That is the shape every lapse in this repo has taken - an instrument that cannot express
    its own check goes quiet rather than failing. Any tools/*.py carrying the markers is now gated automatically.

    BOUND, stated: this proves the fenced logic and its self-test. That each row's wrapper catches a real throw is
    checked by reading, and by the probe printing this same self-test line on every live run.
    """
    # This file is excluded from its own discovery: it MENTIONS the markers (they are its constants, three lines
    # up) without carrying a block, and on the first run of the generalised form it duly found itself and failed.
    # A gate that cannot tell a definition of a pattern from an instance of it reports its own text as a defect.
    me = os.path.abspath(__file__)
    probes = sorted(
        path for path in glob.glob(os.path.join("tools", "*.py"))
        if os.path.abspath(path) != me and BEGIN in open(path, encoding="utf-8").read()
    )
    if not probes:
        return ["no tools/*.py carries the pure-helper markers, so no self-test could be extracted"]

    problems = []
    for probe in probes:
        src = open(probe, encoding="utf-8").read()
        if END not in src:
            problems.append("%s opens a pure-helper block and never closes it" % probe)
            continue

        block = src.split(BEGIN, 1)[1].split(END, 1)[0]
        namespace = {}
        try:
            exec(compile(block, probe, "exec"), namespace)
            if "selftest" not in namespace:
                problems.append("%s has a pure-helper block with no selftest() in it" % probe)
                continue
            line = namespace["selftest"]()
        except Exception as ex:
            problems.append("%s helper self-test raised: %s" % (probe, ex))
            continue

        print("   OK   %s  (%s)" % (probe, line) if "OK" in line else "  FAIL  %s  (%s)" % (probe, line))
        if "OK" not in line:
            problems.append("%s %s" % (probe, line))
    return problems


def main():
    files = []
    for root in ROOTS:
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for f in filenames:
                if f.endswith(".py"):
                    files.append(os.path.join(dirpath, f))

    failures = 0
    for path in sorted(files):
        problems = check(path)
        if problems:
            for line, msg in problems:
                print("  FAIL  %s:%d  %s" % (path, line, msg))
                failures += 1
        else:
            print("   OK   %s" % path)

    selftest_problems = probe_selftest()
    for msg in selftest_problems:
        print("  FAIL  %s" % msg)
    failures += len(selftest_problems)

    print("\n%d Python file(s) scanned; %d problem(s) found." % (len(files), failures))
    if failures:
        print("A name read but never bound is a NameError the script only finds when it runs -")
        print("and the probe that prompted this gate cannot be run by CI at all.")
        sys.exit(1)


main()
