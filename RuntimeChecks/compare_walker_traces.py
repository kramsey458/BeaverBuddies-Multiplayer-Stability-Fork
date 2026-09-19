"""Compare two walker desync diagnostics (walkers-*.tsv), one from each player.

Usage: python compare_walker_traces.py <host.tsv> <guest.tsv>
       python compare_walker_traces.py --self-test

Prints the first tick both files cover in which any walking character differs, and for each such
character which columns differ. Values are written as "<bits>=<value>", so the comparison is exact.
Reading the result: if only x/y/z differ, the cause is in an earlier tick the files do not cover;
if nextCorner/corners/last* differ, the two computers found different paths; if baseSpeed/bonus
differ, a speed input differed; animatedOnZipline differing while onZiplineEdge agrees is the
frame-timed zipline flag (harmless from 1.0.8 on, the cause of the desync before it).
"""
import sys


def load(path):
    ticks = {}
    with open(path, encoding="utf-8") as handle:
        header = handle.readline().rstrip("\r\n").split("\t")
        for line in handle:
            cells = line.rstrip("\r\n").split("\t")
            if len(cells) != len(header):
                continue
            row = dict(zip(header, cells))
            ticks.setdefault(int(row["tick"]), {})[row["entity"]] = row
    return header, ticks


def compare(host, guest, out=print):
    header, a = host
    _, b = guest
    shared = sorted(set(a) & set(b))
    if not shared:
        out("The files share no tick: host covers %s, guest covers %s." % (span(a), span(b)))
        return None
    out("Host covers ticks %s, guest %s; comparing %d shared ticks." % (span(a), span(b), len(shared)))
    for tick in shared:
        problems = []
        for entity in sorted(set(a[tick]) | set(b[tick])):
            left, right = a[tick].get(entity), b[tick].get(entity)
            if left is None or right is None:
                problems.append("%s only on the %s" % (entity, "guest" if left is None else "host"))
                continue
            columns = [c for c in header if c not in ("tick", "entity") and left[c] != right[c]]
            if columns:
                detail = "; ".join("%s host %s guest %s" % (c, left[c], right[c]) for c in columns)
                problems.append("%s (%s): %s" % (entity, left["name"], detail))
        if problems:
            out("First difference at the start of tick %d (so it arose during tick %d):" % (tick, tick - 1))
            for problem in problems:
                out("  " + problem)
            return tick
    out("No difference in any shared tick.")
    return -1


def span(ticks):
    return "%d-%d" % (min(ticks), max(ticks)) if ticks else "none"


def self_test():
    header = ["tick", "entity", "name", "x", "animatedOnZipline"]
    host = (header, {5: {"a": dict(tick="5", entity="a", name="Ann", x="1", animatedOnZipline="0")},
                     6: {"a": dict(tick="6", entity="a", name="Ann", x="2", animatedOnZipline="0")}})
    same = (header, {6: {"a": dict(tick="6", entity="a", name="Ann", x="2", animatedOnZipline="0")}})
    differs = (header, {5: {"a": dict(tick="5", entity="a", name="Ann", x="1", animatedOnZipline="1")},
                        6: {"a": dict(tick="6", entity="a", name="Ann", x="3", animatedOnZipline="0")}})
    lines = []
    assert compare(host, same, lines.append) == -1
    assert compare(host, differs, lines.append) == 5 and "animatedOnZipline host 0 guest 1" in lines[-1]
    assert compare(host, (header, {9: {}}), lines.append) is None
    print("self-test passed")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 3:
        compare(load(sys.argv[1]), load(sys.argv[2]))
    else:
        print(__doc__)
        sys.exit(2)
