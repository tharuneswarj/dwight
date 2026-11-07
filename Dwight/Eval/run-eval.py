# Dwight to-script eval harness - the LAMCP driver (RR20 #46e, ADR-066).
#
# "It sometimes fails" is not a fact anyone can act on. This turns it into a NUMBER, and two numbers at that:
#   COMPILE-RATE - did the script BUILD?      (hallucinated API, wrong types)
#   VERIFY-RATE  - did it REPRODUCE the originals, output for output?  (wrong port semantics, dropped logic)
# They fail for different reasons, and a fix can move one while leaving the other flat - which is exactly what we
# cannot see without measuring.
#
# HOW TO RUN (paste each block into the LAMCP bridge; the run is ASYNC, so poll Status()):
#
#   1. Open the definition you want as the corpus (a REAL one - the failure taxonomy only shows on real logic).
#   2. Build the corpus, run it, then poll:
#
#        import clr, System
#        dw = [a for a in System.AppDomain.CurrentDomain.GetAssemblies() if a.GetName().Name == 'Dwight'][0]
#        Eval = dw.GetType('Dwight.Eval.ToScriptEval')
#        inv = lambda name, *args: Eval.GetMethod(name).Invoke(None, System.Array[object](list(args)))
#
#        print(inv('BuildCorpus', 3, 10, 100))                 # minSize, maxSize, maxSelections
#        print(inv('Run', 'csharp', 50, 'after', None, None, False))   # lang, limit, arm, model, provider, ORACLE
#        print(inv('Status'))                                  # poll this until running == false
#
#   3. The rows stream to %APPDATA%\Dwight\eval\toscript-<lang>-<stamp>.jsonl as they complete, so a crashed or
#      cancelled run still leaves its data.
#
# THE WP-0 MEASUREMENT (Phase-8 #94): the OUTPUT ORACLE is the ONE variable; RR20 is held ON (arm='after') in both.
#   WP-0-before = Run('csharp', 50, 'after', oracle=False)   -> the shipped ~80/~38 row (the #108 baseline)
#   WP-0-after  = Run('csharp', 50, 'after', oracle=True)    -> the treatment (the output oracle feeds the diff back)
# Both are forced to claude-sonnet-4-6, temp 0, and every row is stamped (model id + corpus hash). Compare the two.
# ('before' -> EvalFlags.AllOff() is the LEGACY Phase-7 RR20-off arm, 23/15, kept only for reference.)
#
# Every trial runs OFF-CANVAS and is removed: the corpus definition is never mutated, nothing is committed, and no
# undo record is written. It drives the REAL pipeline (same payload, same prompt, same compile gate, same bit-exact
# comparison) - a harness that tested a mock would measure the mock.

import System

def _eval_type():
    dw = [a for a in System.AppDomain.CurrentDomain.GetAssemblies() if a.GetName().Name == 'Dwight']
    if not dw:
        raise RuntimeError("Dwight.gha is not loaded - open Grasshopper and make sure the plugin loaded.")
    t = dw[0].GetType('Dwight.Eval.ToScriptEval')
    if t is None:
        raise RuntimeError("Dwight.Eval.ToScriptEval not found - is this a fresh build? (close Rhino, rebuild)")
    return t

def _invoke(name, *args):
    t = _eval_type()
    m = t.GetMethod(name)
    if m is None:
        raise RuntimeError("no such method: " + name)
    return m.Invoke(None, System.Array[object](list(args)))

def build_corpus(min_size=3, max_size=10, max_selections=100):
    return _invoke('BuildCorpus', min_size, max_size, max_selections)

def run(language='csharp', limit=50, arm='after', model=None, provider=None, oracle=False):
    # WP-0 (#94): arm='after' holds RR20 ON; `oracle` is the ONE variable (False = the ~80/~38 baseline, True = the
    # output-diff treatment). model/provider stay None so the eval FORCES claude-sonnet-4-6 (never reads Settings).
    return _invoke('Run', language, limit, arm, model, provider, oracle)

def status():
    return _invoke('Status')
