"""Compare two frame rate logs (perf-*.csv), one from each player.

Usage: python compare_perf_logs.py <host.csv> <guest.csv>
       python compare_perf_logs.py --self-test

Turn "Log Frame Rate Details" on for both players, play the same session, then run this over the two
files. It first checks that both players had the same mods at the same versions: if they did not,
nothing below is a fair comparison, and the difference is worth fixing on its own because mods that
change the game are the usual cause of a desync.

It then lines the two files up by game tick (the only clock both players share) and, for each slow
frame, says what it was: a hitch on this computer while the other one waited, a garbage collection on
both at once, both waiting with nothing to blame locally (which points at the network or at message
batching), the per-tick entity hash, the detailed-logging work, or catching up after falling behind.

The last section is the one to read first: it says how much of each player's frame time the mod
accounts for at all. If most of a slow frame is unattributed, the cause is outside this mod.
"""
import sys
from collections import Counter

# The columns that measure work this mod does, in the order they are reported.
SPANS = [
    ("hashMs", "the per-tick entity hash"),
    ("traceMs", "detailed logging (water and moisture hashes, trace events)"),
    ("sendMs", "serializing, compressing and sending"),
    ("readMs", "receiving and deserializing"),
    ("replayMs", "replaying events"),
    ("ioUpdateMs", "draining the network queues"),
    ("steamPumpMs", "handing data to Steam"),
    ("waitCheckMs", "checking whether the other player's events arrived"),
]
INTS = ("tick", "frames", "ticksDone", "waitFrames", "ticksBehind", "sendBytes", "sendCount",
        "gc0", "gc1", "gc2", "hostPacingPct", "fpsPacingPct", "holding", "saving", "speedChanges")
FLOATS = ("frameMs", "maxFrameMs", "speed", "targetSpeed") + tuple(name for name, _ in SPANS)

# A spike on one player and a spike on the other within this many ticks are treated as the same event.
TOLERANCE = 3


def load(path):
    """Reads a perf CSV into {meta, mods, patches, spikes, summaries}."""
    meta, mods, patches, rows = {}, [], [], []
    with open(path, encoding="utf-8") as handle:
        header = None
        for line in handle:
            line = line.rstrip("\r\n")
            if not line:
                continue
            if line.startswith("#"):
                body = line[1:].strip()
                if body.startswith("mod="):
                    parts = body[4:].split("|")
                    mods.append((parts[0], parts[1] if len(parts) > 1 else parts[0],
                                 parts[2] if len(parts) > 2 else ""))
                elif body.startswith("patch=") or body.startswith("owner="):
                    patches.append(body)
                else:
                    for pair in body.split(" "):
                        if "=" in pair:
                            key, value = pair.split("=", 1)
                            meta[key] = value
                continue
            cells = line.split(",")
            if header is None:
                header = cells
                continue
            if len(cells) != len(header):
                continue
            rows.append(parse(dict(zip(header, cells))))
    return {
        "path": path,
        "meta": meta,
        "mods": mods,
        "patches": patches,
        "spikes": [r for r in rows if r["kind"] == "spike"],
        "summaries": [r for r in rows if r["kind"] == "summary"],
    }


def parse(row):
    for key in INTS:
        row[key] = int(row.get(key, 0) or 0)
    for key in FLOATS:
        row[key] = float(row.get(key, 0) or 0)
    return row


def role(log):
    return log["meta"].get("role", "?")


def compare_mods(host, guest, out=print):
    """Reports any difference. False means the rest of the comparison is not trustworthy."""
    a = {mod[0]: mod for mod in host["mods"]}
    b = {mod[0]: mod for mod in guest["mods"]}
    if not a or not b:
        out("! One of the files does not list its mods, so they cannot be compared.")
        return False
    only_host = sorted(set(a) - set(b))
    only_guest = sorted(set(b) - set(a))
    differing = sorted(key for key in set(a) & set(b) if a[key][2] != b[key][2])
    if not (only_host or only_guest or differing):
        out("Both players had the same %d mods at the same versions." % len(a))
        return True
    out("! The two players did not have the same mods. Nothing below is a fair comparison, and a")
    out("! difference in a mod that changes the game can cause a desync by itself.")
    for key in only_host:
        out("!   only on the %s: %s %s" % (role(host), a[key][1], a[key][2]))
    for key in only_guest:
        out("!   only on the %s: %s %s" % (role(guest), b[key][1], b[key][2]))
    for key in differing:
        out("!   different versions: %s %s (%s) vs %s (%s)"
            % (a[key][1], a[key][2], role(host), b[key][2], role(guest)))
    return False


def compare_settings(host, guest, out=print):
    for key, label in (("detailedLogging", "detailed logging"), ("version", "mod version")):
        left, right = host["meta"].get(key), guest["meta"].get(key)
        if left != right:
            out("! %s differs: %s on the %s, %s on the %s"
                % (label, left, role(host), right, role(guest)))
    if host["meta"].get("detailedLogging") == "1":
        out("Note: detailed logging was on. It hashes the whole water map and the moisture map every")
        out("      tick and captures a stack trace for each trace, which shows up as traceMs below.")


def dominant(row):
    """The measured span that took the longest, as (column, milliseconds)."""
    best, best_ms = None, 0.0
    for name, _ in SPANS:
        if row[name] > best_ms:
            best, best_ms = name, row[name]
    return best, best_ms


def measured(row):
    return sum(row[name] for name, _ in SPANS)


def summary_for(log, tick):
    """The summary row whose window contains this tick, or None."""
    best = None
    for row in log["summaries"]:
        if row["tick"] >= tick and (best is None or row["tick"] < best["tick"]):
            best = row
    return best


def peer_spike_near(log, tick):
    best = None
    for row in log["spikes"]:
        distance = abs(row["tick"] - tick)
        if distance <= TOLERANCE and (best is None or distance < abs(best["tick"] - tick)):
            best = row
    return best


def classify(row, peer, out_of=None):
    """What a slow frame was. Returns (label, detail)."""
    peer_spike = peer_spike_near(peer, row["tick"]) if peer else None
    peer_window = summary_for(peer, row["tick"]) if peer else None
    span, span_ms = dominant(row)
    local_work = span_ms >= row["frameMs"] * 0.3
    collected = row["gc0"] + row["gc1"] + row["gc2"] > 0
    peer_collected = bool(peer_spike and peer_spike["gc0"] + peer_spike["gc1"] + peer_spike["gc2"] > 0)
    peer_waited = bool((peer_spike and peer_spike["waitFrames"]) or
                       (peer_window and peer_window["waitFrames"] and not peer_spike))

    if row["saving"]:
        return "save", "a save was in progress (autosave collides with the frame)"
    if collected and peer_collected:
        return "gc-both", "a garbage collection on both computers at once"
    if row["waitFrames"] and peer_spike and (measured(peer_spike) >= peer_spike["frameMs"] * 0.3
                                             or peer_spike["gc0"] + peer_spike["gc1"] + peer_spike["gc2"]):
        return "peer-hitch", "waiting: the other player hitched at tick %d" % peer_spike["tick"]
    if row["waitFrames"] and peer_waited:
        return "both-waiting", "both players waiting with nothing to blame locally (network or batching)"
    if row["waitFrames"]:
        return "waiting", "waiting for the other player, who logged nothing here"
    if collected and not local_work:
        return "gc", "a garbage collection here (gen0 %d, gen1 %d, gen2 %d)" % (row["gc0"], row["gc1"], row["gc2"])
    if local_work:
        label = dict(SPANS)[span]
        extra = ""
        if span == "sendMs" and row["sendBytes"]:
            extra = " (%d bytes in %d messages)" % (row["sendBytes"], row["sendCount"])
        return span, "%.0f%% of the frame in %s%s" % (100 * span_ms / row["frameMs"], label, extra)
    if row["speedChanges"] or row["speed"] > row["targetSpeed"]:
        return "catch-up", ("running at speed %.1f for a chosen %.1f, %d ticks behind"
                            % (row["speed"], row["targetSpeed"], row["ticksBehind"]))
    return "elsewhere", "not in anything this mod measures (%.0f%% unattributed)" % (
        100 * (row["frameMs"] - measured(row)) / row["frameMs"] if row["frameMs"] else 0)


def report_side(log, peer, out=print):
    out("")
    out("--- %s (%s) ---" % (role(log), log["path"]))
    spikes = log["spikes"]
    if not spikes:
        out("No frame went over the threshold.")
    else:
        out("%d slow frames, worst %.0f ms." % (len(spikes), max(r["frameMs"] for r in spikes)))
        counts = Counter()
        for row in spikes:
            label, _ = classify(row, peer)
            counts[label] += 1
        out("What they were:")
        for label, count in counts.most_common():
            out("  %-14s %4d" % (label, count))
        out("The ten worst:")
        for row in sorted(spikes, key=lambda r: -r["frameMs"])[:10]:
            _, detail = classify(row, peer)
            out("  tick %-7d %7.0f ms  %s" % (row["tick"], row["frameMs"], detail))
        gaps = Counter()
        ticks = sorted(row["tick"] for row in spikes)
        for earlier, later in zip(ticks, ticks[1:]):
            if later != earlier:
                gaps[later - earlier] += 1
        repeated = [(gap, n) for gap, n in gaps.most_common(3) if n >= 3]
        if repeated:
            out("Spacing between slow frames, in ticks (a repeating gap means something periodic):")
            for gap, n in repeated:
                out("  every %d ticks: %d times" % (gap, n))
    report_baseline(log, out)


def report_baseline(log, out=print):
    windows = log["summaries"]
    if not windows:
        out("No summary rows, so there is no baseline to compare the spikes against.")
        return
    frames = sum(w["frames"] for w in windows)
    total = sum(w["frameMs"] for w in windows)
    if not frames or not total:
        return
    out("Baseline over %d frames (%.0f s of play):" % (frames, total / 1000))
    out("  mean frame %.1f ms (%.0f fps), worst %.0f ms" % (total / frames, 1000 * frames / total,
                                                            max(w["maxFrameMs"] for w in windows)))
    out("  ticks run %d, frames spent waiting %d" % (sum(w["ticksDone"] for w in windows),
                                                     sum(w["waitFrames"] for w in windows)))
    out("  collections: gen0 %d, gen1 %d, gen2 %d"
        % (sum(w["gc0"] for w in windows), sum(w["gc1"] for w in windows), sum(w["gc2"] for w in windows)))
    out("  where the time went:")
    accounted = 0.0
    for name, label in SPANS:
        spent = sum(w[name] for w in windows)
        accounted += spent
        if spent > 0:
            out("    %5.1f%%  %-12s %s" % (100 * spent / total, name, label))
    out("    %5.1f%%  %-12s everything else: the game's own simulation, drawing, and other mods"
        % (100 * (total - accounted) / total, "elsewhere"))


def compare(host, guest, out=print):
    out("Frame rate logs: %s vs %s" % (role(host), role(guest)))
    out("")
    matched = compare_mods(host, guest, out)
    compare_settings(host, guest, out)
    report_side(host, guest, out)
    report_side(guest, host, out)
    out("")
    if not matched:
        out("Remember: the mod lists differ, so treat the comparison above as a rough guide only.")
    return matched


def self_test():
    def log(role_name, mods, spikes=(), summaries=()):
        return {"path": role_name + ".csv", "meta": {"role": role_name, "detailedLogging": "0"},
                "mods": mods, "patches": [],
                "spikes": [row(**s) for s in spikes], "summaries": [row(kind="summary", **s) for s in summaries]}

    def row(kind="spike", **fields):
        base = {name: 0 for name in INTS}
        base.update({name: 0.0 for name in FLOATS})
        base["kind"] = kind
        base.update(fields)
        return base

    same = [("a", "Alpha", "1.0"), ("b", "Beta", "2.0")]
    lines = []
    assert compare_mods(log("host", same), log("guest", same), lines.append)
    assert "same 2 mods" in lines[-1]

    lines = []
    assert not compare_mods(log("host", same), log("guest", [("a", "Alpha", "1.1")]), lines.append)
    assert any("different versions: Alpha 1.0" in line for line in lines)
    assert any("only on the host: Beta" in line for line in lines)

    # A hitch on the host, and the guest waiting at the same tick.
    host = log("host", same, spikes=[dict(tick=100, frameMs=300, hashMs=200)])
    guest = log("guest", same, spikes=[dict(tick=101, frameMs=280, waitFrames=1)])
    assert classify(host["spikes"][0], guest)[0] == "hashMs"
    assert classify(guest["spikes"][0], host)[0] == "peer-hitch"
    assert "tick 100" in classify(guest["spikes"][0], host)[1]

    # Both waiting, nothing to blame locally.
    host = log("host", same, spikes=[dict(tick=50, frameMs=200, waitFrames=1)])
    guest = log("guest", same, spikes=[dict(tick=50, frameMs=200, waitFrames=1)])
    assert classify(host["spikes"][0], guest)[0] == "both-waiting"

    # A collection on both at once.
    host = log("host", same, spikes=[dict(tick=7, frameMs=150, gc0=2)])
    guest = log("guest", same, spikes=[dict(tick=7, frameMs=160, gc0=1, gc2=1)])
    assert classify(host["spikes"][0], guest)[0] == "gc-both"

    # A save beats everything else.
    saving = log("host", same, spikes=[dict(tick=9, frameMs=900, saving=1, gc0=3)])
    assert classify(saving["spikes"][0], guest)[0] == "save"

    # Nothing this mod does: the frame is long but the spans are empty.
    quiet = log("host", same, spikes=[dict(tick=11, frameMs=120)])
    label, detail = classify(quiet["spikes"][0], log("guest", same))
    assert label == "elsewhere" and "100% unattributed" in detail

    # Catching up after falling behind.
    fast = log("host", same, spikes=[dict(tick=13, frameMs=120, speed=7, targetSpeed=3, ticksBehind=5)])
    assert classify(fast["spikes"][0], log("guest", same))[0] == "catch-up"

    lines = []
    report_baseline(log("host", same, summaries=[dict(tick=100, frames=100, frameMs=2000, hashMs=500, maxFrameMs=80)]),
                    lines.append)
    assert any("mean frame 20.0 ms (50 fps)" in line for line in lines)
    assert any("25.0%  hashMs " in line for line in lines)
    assert any("75.0%  elsewhere " in line for line in lines)

    lines = []
    compare(log("host", same, summaries=[dict(tick=10, frames=10, frameMs=200)]),
            log("guest", same, summaries=[dict(tick=10, frames=10, frameMs=200)]), lines.append)
    assert any("No frame went over the threshold." in line for line in lines)
    print("self-test passed")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 3:
        compare(load(sys.argv[1]), load(sys.argv[2]))
    else:
        print(__doc__)
        sys.exit(2)
