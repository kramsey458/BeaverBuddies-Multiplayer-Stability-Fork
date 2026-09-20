#!/usr/bin/env python3
"""Reads the frame rate logs of two BeaverBuddies players and says where slow frames came from.

    python compare_perf_logs.py host.csv guest.csv

Standard library only. Read PERFORMANCE-LOG.md for what the columns mean.

What it does, in order:
  1. Compares the two players' mod lists, mod builds and game versions. If they differ, nothing else is comparable
     (and the difference can cause desyncs by itself), so it says so and stops. --force goes on anyway.
  2. Describes each player's normal: frame time, where it goes, garbage collection, allocation.
  3. Lines the two logs up by game tick (never by clock: the two computers' clocks do not agree) and puts each
     stretch of slow frames and long waits into one of these classes:
       local hitch here, matching wait on the other player     (a stall that the other player felt)
       simultaneous garbage collection on both
       waits on both players with no slow frame to explain them (network, or how messages are batched)
       wait, then a catch-up burst                              (the sawtooth: a stall, then a fast catch-up)
       local hitch the other player never noticed               (drawing, another mod, the system)
       wait on one player with no hitch on the other            (network, or a hitch shorter than the threshold)
  4. Looks for a rhythm in the slow frames and compares it with the periodic things that exist: a 1 Hz status
     message, a 10 Hz cursor message, the game's save, garbage collection. This mod has no fixed hash interval:
     the entity hash runs every tick, and with detailed logging on so do the whole-map hashes.

Everything it concludes is a pointer, not a proof: it says what the numbers are consistent with.
"""
import argparse
import collections
import os
import statistics
import sys

SLOTS = ["gameMs", "tebMs", "replayMs", "serMs", "hashMs", "compMs", "sendMs", "recvMs", "deserMs",
         "steamMs", "logMs", "detailMs", "uiMs", "saveMs"]
SIM_SLOTS = ["gameMs", "tebMs", "replayMs"]
SYNC_SLOTS = ["serMs", "hashMs", "compMs", "sendMs", "recvMs", "deserMs", "steamMs"]
SLOT_MEANING = {
    "gameMs": "the game's own ticking (and other mods' patches on it)", "tebMs": "the per-tick entity hash",
    "replayMs": "replaying events", "serMs": "serializing events", "hashMs": "hashing events",
    "compMs": "JSON and gzip for sending", "sendMs": "writing to the connection",
    "recvMs": "receiving on the game thread", "deserMs": "deserializing events", "steamMs": "Steam networking",
    "logMs": "log lines", "detailMs": "detailed-logging work", "uiMs": "connection panel and overlay",
    "saveMs": "saving the game", "otherMs": "outside this mod's probes",
}
FORMAT_LINE = "BeaverBuddies frame rate log, format 1"

# The periodic things that exist, in seconds. A rhythm is compared with these.
KNOWN_PERIODS = [
    (1.0, "the 1 Hz status message (ping probe and player roster)"),
    (0.1, "the 10 Hz cursor message"),
    (0.5, "the connection panel refresh, or this log's own file writer (both every 0.5 s)"),
]


# ---------------------------------------------------------------- reading

class Log:
    def __init__(self, path):
        self.path = path
        self.name = os.path.basename(path)
        self.header = {}
        self.mods = {}
        self.patches = []
        self.notes = []
        self.columns = []
        self.rows = []
        self.ended = False
        self.skipped_rows = 0
        self.format_ok = False

    @property
    def role(self):
        return self.header.get("role", "?")

    @property
    def player(self):
        return self.header.get("player", "?")

    @property
    def label(self):
        return "%s (%s)" % (self.role, self.player)

    def rows_of(self, kind):
        return [r for r in self.rows if r["type"] == kind]


def parse_log(path):
    log = Log(path)
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
        lines = f.read().splitlines()
    for line in lines:
        if not line.strip():
            continue
        if line.startswith("#"):
            _parse_comment(log, line)
            continue
        fields = line.split(",")
        if not log.columns:
            log.columns = fields
            continue
        if len(fields) != len(log.columns):
            log.skipped_rows += 1  # a file cut short by a crash ends in half a line
            continue
        row = {}
        for name, value in zip(log.columns, fields):
            if name == "type":
                row[name] = value
            else:
                try:
                    row[name] = float(value)
                except ValueError:
                    row[name] = 0.0
        log.rows.append(row)
    return log


def _parse_comment(log, line):
    text = line[1:].strip()
    if text == FORMAT_LINE:
        log.format_ok = True
    elif text == "end":
        log.ended = True
    elif text.startswith("mod|"):
        parts = text.split("|")
        if len(parts) >= 4:
            log.mods[parts[1]] = (parts[2], parts[3])
    elif text.startswith("patch|"):
        parts = text.split("|")
        if len(parts) >= 11:
            log.patches.append({"tag": parts[1], "method": parts[2], "kind": parts[3], "owner": parts[4],
                                "priority": parts[5], "index": parts[6], "assembly": parts[9], "patch": parts[10]})
    elif text.startswith("rows dropped"):
        log.notes.append(text)
    elif ": " in text and not text.startswith(("note:", "patches-note:")):
        key, _, value = text.partition(": ")
        log.header[key.strip()] = value.strip()


# ---------------------------------------------------------------- 1. are the two comparable?

def parse_build(text):
    parts = {}
    for piece in text.split(";"):
        key, _, value = piece.partition("=")
        if key:
            parts[key.strip()] = value.strip()
    return parts


def compare_environment(a, b):
    """Returns (problems, warnings). Any problem means the two logs cannot be compared."""
    problems, warnings = [], []
    for log in (a, b):
        if not log.format_ok:
            warnings.append("%s does not start with the expected header line; it may be from another version." % log.name)
        if not log.mods:
            problems.append("%s lists no mods, so the mod lists cannot be compared." % log.name)
    only_a = sorted(set(a.mods) - set(b.mods))
    only_b = sorted(set(b.mods) - set(a.mods))
    for mod_id in only_a:
        problems.append("Only %s has mod '%s' (%s %s)." % (a.label, mod_id, a.mods[mod_id][0], a.mods[mod_id][1]))
    for mod_id in only_b:
        problems.append("Only %s has mod '%s' (%s %s)." % (b.label, mod_id, b.mods[mod_id][0], b.mods[mod_id][1]))
    for mod_id in sorted(set(a.mods) & set(b.mods)):
        if a.mods[mod_id][1] != b.mods[mod_id][1]:
            problems.append("Mod '%s' is version %s for %s but %s for %s." %
                            (mod_id, a.mods[mod_id][1], a.label, b.mods[mod_id][1], b.label))
    ba, bb = parse_build(a.header.get("build", "")), parse_build(b.header.get("build", ""))
    for key, what in (("game", "game version"), ("mod", "this mod's version"),
                      ("modBuild", "compiled build of this mod"), ("netBuild", "compiled build of its network library")):
        if ba.get(key) != bb.get(key):
            problems.append("The %s differs: %s for %s, %s for %s." % (what, ba.get(key, "?"), a.label, bb.get(key, "?"), b.label))
    if a.header.get("detailedLogging") != b.header.get("detailedLogging"):
        warnings.append("Detailed logging is %s for %s but %s for %s. It is the heaviest optional work this mod does, so the two "
                        "players are not doing the same work." % (a.header.get("detailedLogging"), a.label,
                                                                   b.header.get("detailedLogging"), b.label))
    for key, what in (("thresholdMs", "slow-frame threshold"), ("summaryTicks", "summary interval"), ("unity", "Unity version")):
        if a.header.get(key) != b.header.get(key):
            warnings.append("The %s differs: %s for %s, %s for %s." % (what, a.header.get(key), a.label, b.header.get(key), b.label))
    if a.role == b.role:
        warnings.append("Both logs say they are from a %s; expected one host and one guest." % a.role)
    return problems, warnings


# ---------------------------------------------------------------- 2. what is normal

def wall_seconds(rows):
    stamps = [r["utcMs"] for r in rows if r.get("utcMs")]
    return (max(stamps) - min(stamps)) / 1000.0 if len(stamps) > 1 else 0.0


def baseline(log):
    """Numbers about ordinary frames, from the summary rows (which cover every frame, not just the slow ones)."""
    summaries = log.rows_of("S")
    out = {"summaries": len(summaries)}
    frames = sum(r["frames"] for r in summaries)
    if not summaries or frames <= 0:
        return out
    total_ms = sum(r["frameMs"] * r["frames"] for r in summaries)
    out["frames"] = frames
    out["mean_ms"] = total_ms / frames
    out["worst_ms"] = max(r["maxFrameMs"] for r in summaries)
    out["shares"] = {}
    for slot in SLOTS + ["otherMs"]:
        out["shares"][slot] = sum(r[slot] * r["frames"] for r in summaries) / total_ms if total_ms else 0.0
    seconds = wall_seconds(summaries) or wall_seconds(log.rows)
    out["seconds"] = seconds
    out["ticks"] = max(r["tick"] for r in summaries) - min(r["tick"] for r in summaries)
    gc = sum(r["gcDelta"] for r in summaries)
    out["gc"] = gc
    out["gc_per_minute"] = gc / (seconds / 60.0) if seconds > 0 else 0.0
    out["alloc_kb_per_s"] = sum(r["allocKB"] for r in summaries) / seconds if seconds > 0 else 0.0
    heaps = [r["heapMB"] for r in summaries]
    out["heap_min"], out["heap_max"] = min(heaps), max(heaps)
    out["wait_ms_per_tick"] = (sum(r["waitMs"] for r in summaries) / max(1.0, sum(r["ticks"] for r in summaries)))
    out["probe_us"] = sum(r["probeUs"] * r["frames"] for r in summaries) / frames
    out["probe_share"] = (out["probe_us"] / 1000.0) / out["mean_ms"] if out["mean_ms"] else 0.0
    return out


# ---------------------------------------------------------------- 3. causes and alignment

def local_cause(row):
    """(label, detail) for one slow frame, from where its time went. A pointer, not a proof."""
    frame = row["frameMs"] or 1.0
    share = {slot: row[slot] / frame for slot in SLOTS + ["otherMs"]}
    sim = sum(share[s] for s in SIM_SLOTS)
    sync = sum(share[s] for s in SYNC_SLOTS)
    top = max(SLOTS + ["otherMs"], key=lambda s: row[s])
    catching_up = row["speed"] > row["target"] + 0.5 and row["ticks"] >= 2
    if row["saving"] or share["saveMs"] >= 0.4:
        return "save", "saving took %.0f ms" % row["saveMs"]
    if row["gcDelta"] > 0:
        return "GC", "%d collection(s); the time landed mostly in %s" % (row["gcDelta"], SLOT_MEANING[top])
    if catching_up:
        return "catch-up", "%d ticks in one frame at speed %.1f (chosen %.1f)" % (row["ticks"], row["speed"], row["target"])
    if share["detailMs"] + share["logMs"] >= 0.4:
        return "detailed logging", "detail %.0f ms, log lines %.0f ms" % (row["detailMs"], row["logMs"])
    if sync >= 0.4:
        top_sync = max(SYNC_SLOTS, key=lambda s: row[s])
        return "sync work", "%s %.0f ms" % (SLOT_MEANING[top_sync], row[top_sync])
    if share["uiMs"] >= 0.4:
        return "panel or overlay", "%.0f ms" % row["uiMs"]
    if sim >= 0.5:
        return "simulation", "ticking took %.0f ms for %d tick(s)" % (row["gameMs"] + row["tebMs"] + row["replayMs"], row["ticks"])
    if share["otherMs"] >= 0.5:
        return "outside this mod", "%.0f ms is not in any probe (drawing, other mods, the system)" % row["otherMs"]
    return "mixed", "largest is %s at %.0f ms" % (SLOT_MEANING[top], row[top])


def spans(log, window, include_unfocused):
    """One entry per slow frame and per long wait: which player, what, and which ticks it covers."""
    out = []
    ignored = 0
    for row in log.rows:
        if row["type"] == "F":
            if not row["focused"] and not include_unfocused:
                ignored += 1
                continue
            lo = int(row["tick"] - max(row["ticks"], 0))
            out.append({"log": log, "row": row, "kind": "F", "lo": lo - window, "hi": int(row["tick"]) + window,
                        "at": (lo, int(row["tick"]))})
        elif row["type"] == "W":
            hi = int(row["tick"])
            out.append({"log": log, "row": row, "kind": "W", "lo": hi - 1 - window, "hi": hi + window, "at": (hi - 1, hi)})
    return out, ignored


def cluster(entries):
    """Groups spans that overlap in ticks, whichever player they came from."""
    clusters = []
    for entry in sorted(entries, key=lambda e: (e["lo"], e["hi"])):
        if clusters and entry["lo"] <= clusters[-1]["hi"]:
            clusters[-1]["entries"].append(entry)
            clusters[-1]["hi"] = max(clusters[-1]["hi"], entry["hi"])
        else:
            clusters.append({"entries": [entry], "hi": entry["hi"]})
    return clusters


def classify(members, logs):
    """(class, one-line description) for a cluster. members are its spans."""
    per_log = {id(l): {"F": [], "W": []} for l in logs}
    for m in members:
        per_log[id(m["log"])][m["kind"]].append(m)

    def worst(items):
        return max(items, key=lambda m: m["row"]["frameMs"])

    hitching = [l for l in logs if per_log[id(l)]["F"]]
    waiting = [l for l in logs if per_log[id(l)]["W"]]

    def cause_of(log):
        return local_cause(worst(per_log[id(log)]["F"])["row"])

    def wait_ms(log):
        return sum(m["row"]["waitMs"] for m in per_log[id(log)]["W"])

    if len(hitching) == len(logs) and len(logs) > 1:
        causes = [cause_of(l) for l in logs]
        if all(c[0] == "GC" for c in causes):
            return "simultaneous_gc", "garbage collection on both players at the same time"
        real = [(l, c) for l, c in zip(logs, causes) if c[0] != "catch-up"]
        if len(real) == 1:
            l, c = real[0]
            other = [x for x in logs if x is not l][0]
            return "local_hitch_with_remote_wait", "%s: %s (%s); %s waited and then caught up" % (l.label, c[0], c[1], other.label)
        return "both_slow", "both players had slow frames: %s" % "; ".join("%s: %s" % (l.label, c[0]) for l, c in zip(logs, causes))
    if len(hitching) == 1:
        h = hitching[0]
        others = [l for l in logs if l is not h]
        cause = cause_of(h)
        if not others:
            return "local_hitch", "%s: %s (%s)" % (h.label, cause[0], cause[1])
        remote_wait = sum(wait_ms(o) for o in others)
        if cause[0] == "catch-up":
            if per_log[id(h)]["W"]:
                return "wait_then_catch_up", "%s waited %.0f ms, then ran ahead to catch up (%s)" % (h.label, wait_ms(h), cause[1])
            return "wait_then_catch_up", "%s ran a catch-up burst (%s); the stall behind it is not in the other log (shorter than its threshold?)" % (h.label, cause[1])
        if remote_wait > 0:
            return "local_hitch_with_remote_wait", "%s: %s (%s); %s waited %.0f ms" % (h.label, cause[0], cause[1], others[0].label, remote_wait)
        return "local_hitch_no_remote_wait", "%s: %s (%s); the other player did not wait" % (h.label, cause[0], cause[1])
    if len(waiting) == len(logs) and len(logs) > 1:
        return "waits_both_no_cause", "both players waited (%s) with no slow frame to explain it: network or how messages are batched" % \
            ", ".join("%s %.0f ms" % (l.label, wait_ms(l)) for l in logs)
    w = waiting[0]
    if len(logs) == 1:
        return "wait", "%s waited %.0f ms for the other player" % (w.label, wait_ms(w))
    return "wait_no_remote_hitch", "%s waited %.0f ms and the other player has no slow frame then: network, batching, or a hitch under the threshold" % (w.label, wait_ms(w))


def analyse(logs, window, include_unfocused):
    entries, ignored = [], 0
    for log in logs:
        found, skipped = spans(log, window, include_unfocused)
        entries.extend(found)
        ignored += skipped
    results = []
    for c in cluster(entries):
        kind, text = classify(c["entries"], logs)
        lows = [e["at"][0] for e in c["entries"]]
        highs = [e["at"][1] for e in c["entries"]]
        results.append({"class": kind, "text": text, "lo": min(lows), "hi": max(highs), "entries": c["entries"]})
    return results, ignored


# ---------------------------------------------------------------- 4. rhythm

def episodes(rows, gap_frames=5):
    groups = []
    for row in sorted(rows, key=lambda r: r["frame"]):
        if groups and row["frame"] - groups[-1][-1]["frame"] <= gap_frames:
            groups[-1].append(row)
        else:
            groups.append([row])
    return groups


def rhythm(rows, seconds_of=lambda r: r["utcMs"] / 1000.0):
    """Looks for regular spacing in slow frames. Returns None or a dict describing it."""
    groups = episodes(rows)
    # A beat needs several repeats: four events can be evenly spaced by chance.
    if len(groups) < 5:
        return None
    starts = [seconds_of(g[0]) for g in groups]
    ticks = [g[0]["tick"] for g in groups]
    gaps = [b - a for a, b in zip(starts, starts[1:])]
    tick_gaps = [b - a for a, b in zip(ticks, ticks[1:])]
    median = statistics.median(gaps)
    if median <= 0:
        return None
    near = sum(1 for g in gaps if abs(g - median) <= 0.2 * median) / len(gaps)
    mean = statistics.mean(gaps)
    cv = statistics.pstdev(gaps) / mean if mean else 0.0
    return {"episodes": len(groups), "period_s": median, "period_ticks": statistics.median(tick_gaps),
            "regular_share": near, "cv": cv, "regular": near >= 0.7}


def explain_period(period, extra=()):
    """Names the periodic thing a period matches, if any."""
    for seconds, name in list(extra) + KNOWN_PERIODS:
        if seconds <= 0:
            continue
        if abs(period - seconds) <= 0.15 * seconds:
            return name
        # Only the first couple of multiples mean anything: nearly any period is close to some whole number of seconds.
        for k in (2, 3):
            if abs(period - k * seconds) <= 0.05 * k * seconds:
                return "%d times %s" % (k, name)
    return None


# ---------------------------------------------------------------- report

def fmt_ms(v):
    return "%.0f ms" % v if v >= 10 else "%.1f ms" % v


def describe_baseline(log):
    b = baseline(log)
    lines = ["%s  [%s]" % (log.label, log.name)]
    if "mean_ms" not in b:
        lines.append("  no summary rows: the session was shorter than one summary interval")
        return lines, b
    fps = 1000.0 / b["mean_ms"] if b["mean_ms"] else 0
    lines.append("  %d frames over %.0f s and %d ticks; mean frame %.1f ms (%.0f fps), worst single frame %.0f ms" %
                 (b["frames"], b["seconds"], b["ticks"], b["mean_ms"], fps, b["worst_ms"]))
    top = sorted(b["shares"].items(), key=lambda kv: -kv[1])[:5]
    lines.append("  where an ordinary frame goes: " + "; ".join("%s %.0f%%" % (SLOT_MEANING[k], v * 100) for k, v in top))
    lines.append("  garbage collection: %d collections, %.1f per minute; allocating about %.0f KB/s; heap ranged %.0f-%.0f MB" %
                 (b["gc"], b["gc_per_minute"], b["alloc_kb_per_s"], b["heap_min"], b["heap_max"]))
    lines.append("  waiting for the other player: %.1f ms per tick on average" % b["wait_ms_per_tick"])
    lines.append("  this log's own cost: %.0f us per frame (%.2f%% of a frame)" % (b["probe_us"], b["probe_share"] * 100))
    if log.header.get("detailedLogging") == "on":
        lines.append("  detailed logging was ON: the detail and log columns below are work that only exists for that")
    dropped = [n for n in log.notes if n.startswith("rows dropped")]
    if dropped:
        lines.append("  WARNING: " + dropped[-1])
    if not log.ended:
        lines.append("  note: the file has no closing line (the game closed abnormally, or the file was copied while the game ran)")
    if log.skipped_rows:
        lines.append("  note: %d unreadable row(s) skipped" % log.skipped_rows)
    return lines, b


def where_spike_time_goes(log, rows):
    total = sum(r["frameMs"] for r in rows)
    if not rows or total <= 0:
        return None
    shares = {slot: sum(r[slot] for r in rows) / total for slot in SLOTS + ["otherMs"]}
    top = sorted(shares.items(), key=lambda kv: -kv[1])[:4]
    return "%s: %d slow frames, %.1f s in total; time went to %s" % (
        log.label, len(rows), total / 1000.0, "; ".join("%s %.0f%%" % (SLOT_MEANING[k], v * 100) for k, v in top)), shares["otherMs"]


def report(logs, args, out):
    p = lambda text="": out.write(text + "\n")
    p("=" * 78)
    p("FRAME RATE LOG COMPARISON")
    p("=" * 78)
    for log in logs:
        p("%s: %s, mod %s, game %s" % (log.name, log.label, log.header.get("mod", "?"),
                                        parse_build(log.header.get("build", "")).get("game", "?")))
    p()

    if len(logs) == 2:
        problems, warnings = compare_environment(*logs)
        p("1. ARE THE TWO SESSIONS COMPARABLE?")
        if problems:
            p("   NO. The players did not run the same thing, so a difference between their logs may be caused by that:")
            for x in problems:
                p("   - " + x)
        else:
            p("   Yes: the same %d mods at the same versions, and the same build of this mod." % len(logs[0].mods))
        for x in warnings:
            p("   warning: " + x)
        p()
        if problems and not args.force:
            p("Stopping here. Make both players run the same mods and the same build (a mismatch can cause desyncs by itself),")
            p("record another session, or run again with --force to see the numbers anyway.")
            return 2

    p("2. WHAT IS NORMAL")
    stats = {}
    for log in logs:
        lines, stats[log.name] = describe_baseline(log)
        for x in lines:
            p("   " + x)
    p()

    if len(logs) == 2:
        both = "the two logs are lined up by game tick, allowing +-%d ticks" % args.tick_window
    else:
        both = "one log only, so there is nothing to line up with"
    p("3. SLOW FRAMES AND LONG WAITS (%s)" % both)
    results, ignored = analyse(logs, args.tick_window if len(logs) == 2 else 0, args.include_unfocused)
    if ignored:
        p("   %d slow frame(s) were with the game window in the background and are left out (--include-unfocused keeps them)." % ignored)
    if not results:
        p("   None. Either nothing was slow, or the threshold was higher than the slowest frame.")
    counts = collections.Counter(r["class"] for r in results)
    labels = [("local_hitch", "a slow frame (one log only, so nothing to match it with)"),
              ("wait", "a long wait for the other player (one log only)"),
              ("local_hitch_with_remote_wait", "a hitch on one player and a matching wait on the other"),
              ("simultaneous_gc", "garbage collection on both at once"),
              ("waits_both_no_cause", "waits on both, no local cause (network or batching)"),
              ("wait_then_catch_up", "a wait followed by a catch-up burst"),
              ("local_hitch_no_remote_wait", "a hitch the other player never noticed"),
              ("wait_no_remote_hitch", "a wait on one player with no hitch on the other"),
              ("both_slow", "slow frames on both, for different reasons")]
    if results:
        p("   %d episodes:" % len(results))
        for key, text in labels:
            if counts.get(key):
                p("     %3d  %s" % (counts[key], text))
        p()
        shown = results if args.all else results[:args.top]
        p("   Episodes in tick order%s:" % ("" if args.all else " (first %d of %d; --all shows every one)" % (len(shown), len(results))))
        for r in shown:
            ticks = "tick %d" % r["lo"] if r["lo"] == r["hi"] else "ticks %d-%d" % (r["lo"], r["hi"])
            p("     %-16s %s" % (ticks, r["text"]))
            for e in r["entries"]:
                row = e["row"]
                if e["kind"] == "F":
                    p("         %s slow frame %.0f ms  (gc %d, heap %+.1f MB, %d tick(s), speed %.1f/%.1f)" %
                      (e["log"].role, row["frameMs"], row["gcDelta"], row["allocKB"] / 1024.0, row["ticks"], row["speed"], row["target"]))
                else:
                    p("         %s waited %.0f ms for the other player (%d frame(s))" % (e["log"].role, row["waitMs"], row["waiting"]))
    p()

    p("4. WHERE THE TIME WENT IN SLOW FRAMES")
    unattributed = {}
    for log in logs:
        rows = [r for r in log.rows_of("F") if r["focused"] or args.include_unfocused]
        found = where_spike_time_goes(log, rows)
        if found:
            p("   " + found[0])
            unattributed[log.name] = found[1]
        else:
            p("   %s: no slow frames" % log.label)
    p()

    p("5. RHYTHM")
    any_rhythm = False
    for log in logs:
        rows = [r for r in log.rows_of("F") if r["focused"] or args.include_unfocused]
        found = rhythm(rows)
        b = stats.get(log.name, {})
        extra = []
        saves = rhythm([r for r in rows if r["saving"]])
        if saves and saves["regular"]:
            extra.append((saves["period_s"], "this player's save (the game's autosave)"))
        if b.get("gc") and b.get("seconds"):
            extra.append((b["seconds"] / b["gc"], "garbage collection"))
        if found and found["regular"]:
            any_rhythm = True
            name = explain_period(found["period_s"], extra)
            p("   %s: slow frames recur about every %.1f s (%d ticks), %d episodes, %.0f%% within 20%% of that. %s" %
              (log.label, found["period_s"], found["period_ticks"], found["episodes"], found["regular_share"] * 100,
               ("That matches " + name + ".") if name else "That matches none of the periodic things this mod does."))
        else:
            p("   %s: no regular rhythm in the slow frames%s" % (log.label, "" if found else " (too few to tell)"))
        wrows = log.rows_of("W")
        wfound = rhythm(wrows)
        if wfound and wfound["regular"]:
            any_rhythm = True
            p("   %s: long waits recur about every %.1f s (%d ticks)" % (log.label, wfound["period_s"], wfound["period_ticks"]))
    if not any_rhythm:
        p("   Nothing regular: the drops are not on a timer that this analysis knows about.")
    p("   (Hash interval: this mod has none. The entity hash runs every tick; whole-map hashing runs every tick only with detailed logging on.)")
    p()

    p("6. WHAT THIS POINTS TO")
    for line in verdict(logs, results, stats, unattributed):
        p("   - " + line)
    p()
    if args.events_csv:
        write_events(args.events_csv, results)
        p("Episodes written to %s" % args.events_csv)
    return 0


def verdict(logs, results, stats, unattributed):
    lines = []
    for log in logs:
        share = unattributed.get(log.name)
        if share is not None and share >= 0.5:
            lines.append("%s: %.0f%% of the time in slow frames is outside every probe in this mod (drawing, other UI, another mod, the system). "
                         "The cause is more likely there than in this mod." % (log.label, share * 100))
        b = stats.get(log.name, {})
        if b.get("probe_share", 0) > 0.02:
            lines.append("%s: the log itself costs %.1f%% of a frame; treat small differences with care." % (log.label, b["probe_share"] * 100))
        if log.header.get("detailedLogging") == "on":
            rows = log.rows_of("F")
            detail = sum(r["detailMs"] + r["logMs"] for r in rows)
            total = sum(r["frameMs"] for r in rows)
            if total and detail / total >= 0.2:
                lines.append("%s: %.0f%% of slow-frame time is detailed-logging work. Record a session with detailed logging off to see what remains." %
                             (log.label, 100 * detail / total))
    counts = collections.Counter(r["class"] for r in results)
    total = sum(counts.values())
    if total:
        gc_rows = sum(1 for r in results for e in r["entries"] if e["kind"] == "F" and e["row"]["gcDelta"] > 0)
        f_rows = sum(1 for r in results for e in r["entries"] if e["kind"] == "F")
        if f_rows and gc_rows / f_rows >= 0.5:
            lines.append("%d of %d slow frames contain a garbage collection. Compare the heap range and the collections per minute above: "
                         "a heap that climbs and drops on the same rhythm as the slow frames is the usual sign." % (gc_rows, f_rows))
        if counts["local_hitch_with_remote_wait"] + counts["wait_then_catch_up"] >= max(2, total // 3):
            lines.append("Stalls on one player are being felt by the other (%d episodes): the frame-rate drop on one computer is largely a "
                         "consequence of a stall on the other, and the stall is what to find." %
                         (counts["local_hitch_with_remote_wait"] + counts["wait_then_catch_up"]))
        if counts["local_hitch_no_remote_wait"] >= max(2, total // 2):
            lines.append("Most hitches were never felt by the other player, so they are local to one computer and not part of the sync.")
        if counts["waits_both_no_cause"] + counts["wait_no_remote_hitch"] >= max(2, total // 2):
            lines.append("Most long waits have no slow frame behind them: look at the network path and message batching rather than at either computer's frame.")
    if not lines:
        lines.append("Nothing stands out. If the drops are real, record a longer session or lower the slow-frame threshold in the settings.")
    return lines


def write_events(path, results):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("class,tickFrom,tickTo,description\n")
        for r in results:
            f.write("%s,%d,%d,\"%s\"\n" % (r["class"], r["lo"], r["hi"], r["text"].replace('"', "'")))


def main(argv=None, out=None):
    out = out or sys.stdout
    parser = argparse.ArgumentParser(description="Compare two BeaverBuddies frame rate logs (or read one).")
    parser.add_argument("logs", nargs="+", help="one or two CSV files, normally the host's and the guest's")
    parser.add_argument("--force", action="store_true", help="carry on even if the two sessions are not comparable")
    parser.add_argument("--tick-window", type=int, default=3, help="how many ticks apart two events can be and still be one (default 3)")
    parser.add_argument("--top", type=int, default=25, help="how many episodes to list (default 25)")
    parser.add_argument("--all", action="store_true", help="list every episode")
    parser.add_argument("--include-unfocused", action="store_true", help="keep slow frames from while the game window was in the background")
    parser.add_argument("--events-csv", help="also write the episodes to this file")
    args = parser.parse_args(argv)
    if len(args.logs) > 2:
        parser.error("give one or two logs")
    try:
        logs = [parse_log(path) for path in args.logs]
    except OSError as error:
        out.write("Cannot read the file: %s\n" % error)
        return 1
    for log in logs:
        if not log.columns:
            out.write("%s has no column header and does not look like a frame rate log.\n" % log.name)
            return 1
    return report(logs, args, out)


if __name__ == "__main__":
    sys.exit(main())
