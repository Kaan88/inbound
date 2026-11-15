"""Pass/fail of one run-host.sh output folder: python check-host-result.py <host-dir>"""
import json, os, sys
d = sys.argv[1]
ok = True
for name in ("plain-add.json", "kill-resume.json"):
    f = os.path.join(d, name)
    try:
        j = json.load(open(f))
    except Exception as e:
        print(name, "MISSING/unreadable", e); ok = False; continue
    good = j.get("finalState") == "completed" and j.get("hashMatched") is True and j.get("partGone") is True and not j.get("fatal")
    print(name, "finalState=%s hashMatched=%s partGone=%s fatal=%s -> %s" % (j.get("finalState"), j.get("hashMatched"), j.get("partGone"), bool(j.get("fatal")), "PASS" if good else "FAIL"))
    if name == "kill-resume.json": print("   stateAtHello=%s receivedAtHello=%s" % (j.get("stateAtHello"), j.get("receivedAtHello")))
    ok &= good
print("RUN-HOST", "PASS" if ok else "FAIL")
