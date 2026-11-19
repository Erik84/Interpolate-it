#!/usr/bin/env python
"""Joins the screen probe's samples with the engine's pacing log.

    python tools/analyze_pacing.py artifacts/test/probe.csv artifacts/test/pacing.csv [--step 16]

For every picture the probe saw on screen it prints how long it stayed (in refreshes) and whether it was a real
or a generated frame; then it summarises when pictures were held for the wrong number of refreshes, and what the
engine thought it did at those moments.
"""
import csv
import sys
from collections import Counter

BAR_WIDTH = 24


def load_probe(path, step, span):
    pictures = []
    last_bar = None
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            if row["valid"] != "1" or row["bar_x"] in ("NaN", "nan", ""):
                continue
            bar = float(row["bar_x"])
            if last_bar is not None and abs(bar - last_bar) < 0.5:
                continue
            last_bar = bar
            index = int(row["index"])
            left = bar - BAR_WIDTH / 2
            real = row["mid_cells"] == "0" and index >= 0 and abs(left - (index * step) % span) < 0.6
            pictures.append({"refresh": int(float(row["refresh"])), "time": float(row["time_s"]), "bar": bar,
                             "index": index, "real": real, "static": int(row["static_diff"])})
    for a, b in zip(pictures, pictures[1:]):
        a["hold"] = b["refresh"] - a["refresh"]
    return pictures[:-1]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    step = 16
    span = 1280 - BAR_WIDTH
    args = sys.argv[1:]
    if "--step" in args:
        step = int(args[args.index("--step") + 1])
    pictures = load_probe(args[0], step, span)
    if not pictures:
        print("no pictures decoded")
        return 1

    by_kind = {True: Counter(), False: Counter()}
    for p in pictures[1:]:
        by_kind[p["real"]][p["hold"]] += 1
    print("holds of real frames     :", dict(sorted(by_kind[True].items())))
    print("holds of generated frames:", dict(sorted(by_kind[False].items())))

    usual = Counter(p["hold"] for p in pictures[1:]).most_common(1)[0][0]
    odd = [i for i, p in enumerate(pictures) if i > 0 and p["hold"] != usual]
    print(f"usual hold {usual} refreshes; {len(odd)} of {len(pictures) - 1} pictures differ")
    if odd:
        i = odd[0]
        print("first irregular stretch (kind:hold):",
              " ".join(f"{'R' if p['real'] else 'g'}:{p['hold']}" for p in pictures[max(1, i - 6): i + 14]))
        gaps = [b - a for a, b in zip(odd, odd[1:])]
        print("pictures between irregular ones:", dict(sorted(Counter(gaps).items())[:8]))

    if len(args) >= 2 and not args[1].startswith("--"):
        rows = list(csv.DictReader(open(args[1], newline="")))
        presents = [r for r in rows if r["action"] == "present"]
        late = Counter(int(r["late"]) for r in presents)
        kinds = Counter(r["kind"] for r in presents)
        spacing = Counter()
        for a, b in zip(presents, presents[1:]):
            spacing[int(b["refresh"]) - int(a["refresh"])] += 1
        lead = Counter(int(r["planned_refresh"]) - int(r["refresh"]) for r in presents)
        print(f"engine: {len(presents)} presents {dict(kinds)}; late {dict(sorted(late.items()))}; "
              f"ticks between presents {dict(sorted(spacing.items()))}; planned-minus-presented tick {dict(sorted(lead.items()))}")
        # When did a present reach the screen according to DXGI frame statistics, relative to its plan?
        shown = Counter()
        by_id = {}
        for r in rows:
            pid = int(r["stat_present_count"])
            if pid and pid not in by_id:
                by_id[pid] = (float(r["stat_sync_time_s"]), int(r["stat_present_refresh"]) - int(r["stat_sync_refresh"]))
        period = None
        times = [float(r["time_s"]) for r in rows]
        if len(times) > 10:
            period = (times[-1] - times[0]) / (int(rows[-1]["refresh"]) - int(rows[0]["refresh"]))
        if period:
            tick_time = {int(r["refresh"]): float(r["time_s"]) for r in rows}
            for r in presents:
                pid = int(r["present_count"])
                if pid in by_id:
                    planned = int(r["planned_refresh"])
                    if planned in tick_time:
                        sync_time, extra = by_id[pid]
                        shown[round((sync_time + extra * period - tick_time[planned]) / period)] += 1
            print("frame statistics: shown refresh minus planned refresh:", dict(sorted(shown.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
