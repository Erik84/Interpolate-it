#!/usr/bin/env python
"""Where does RIFE put a moving object for a given timestep?

A white bar moves `--step` pixels between two frames. For each timestep t the bar's position in the generated
picture is measured and compared with the ideal t * step. A model that is not linear in t makes motion uneven
whenever the timesteps are not the same every frame (25 fps shown at 60 fps uses 12 different ones).

    tools\\export\\.venv\\Scripts\\python.exe -I tools\\export\\measure_timestep_linearity.py --model 4.25
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import torch  # noqa: E402
import torch.nn.functional as F  # noqa: E402

from export_rife_flow import REPO, load_net  # noqa: E402
from rife_arch import FlowOnly  # noqa: E402
from rife_arch.flow_only import compose  # noqa: E402


def scene(h: int, w: int, bar_x: int, device) -> torch.Tensor:
    """The TestPattern layout: gradient background, black band with a 24 px white bar."""
    ys = torch.linspace(0, 1, h, device=device).view(1, 1, h, 1)
    img = torch.cat([0.10 + 0.15 * ys, 0.12 + 0.08 * ys, 0.20 - 0.05 * ys], 1).expand(1, 3, h, w).clone()
    top = int(h * 0.30)
    img[:, :, top:top + 60, :] = 0.02
    img[:, :, top:top + 60, bar_x:bar_x + 24] = 1.0
    return img


def bar_centre(img: torch.Tensor, h: int) -> float:
    row = img[0, :, int(h * 0.30) + 30, :].mean(0)
    weight = (row - 0.15).clamp(min=0)
    xs = torch.arange(row.numel(), device=row.device, dtype=torch.float32)
    return float((weight * xs).sum() / weight.sum()) + 0.5


def texture(h: int, w: int, device, seed: int = 3) -> torch.Tensor:
    """A wide multi-scale texture (natural-image-like spectrum) to pan across; wider than the frame."""
    g = torch.Generator().manual_seed(seed)
    wide = w + 512
    img = torch.zeros(1, 3, h, wide)
    for cells, weight in ((6, 0.45), (24, 0.25), (96, 0.18), (384, 0.12)):
        z = torch.rand(1, 3, max(2, h * cells // wide + 2), cells + 2, generator=g)
        img += weight * F.interpolate(z, size=(h, wide), mode="bicubic", align_corners=False)
    return img.clamp(0, 1).to(device)


def pan_shift(out: torch.Tensor, wide: torch.Tensor, x0: int, step: int) -> float:
    """Horizontal shift (pixels, sub-pixel by parabola fit) at which the panned texture best matches `out`."""
    h, w = out.shape[2:]
    crop = slice(w // 4, 3 * w // 4)
    best, errors = None, {}
    for s in range(-3, step + 4):
        ref = wide[:, :, :, x0 + s: x0 + s + w]
        errors[s] = float(((out[:, :, :, crop] - ref[:, :, :, crop]) ** 2).mean())
        if best is None or errors[s] < errors[best]:
            best = s
    if best - 1 in errors and best + 1 in errors:
        l, c, r = errors[best - 1], errors[best], errors[best + 1]
        denom = l - 2 * c + r
        if denom > 0:
            return best + 0.5 * (l - r) / denom
    return float(best)


@torch.no_grad()
def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", default="4.25", choices=["4.6", "4.25"])
    ap.add_argument("--step", type=int, default=19, help="movement between the two frames, in pixels")
    ap.add_argument("--size", default="1216x704")
    ap.add_argument("--scene", default="bar", choices=["bar", "pan"],
                    help="bar: a white bar on a black band; pan: the whole textured picture moves (a camera pan)")
    args = ap.parse_args()
    w, h = (int(v) for v in args.size.split("x"))
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    net, _ = load_net(args.model, REPO / "models" / "weights" / f"flownet_v{args.model}.pkl")
    wrapper = FlowOnly(net.to(device), torch.float32).eval()

    x0 = 200
    if args.scene == "bar":
        a, b = scene(h, w, 400, device), scene(h, w, 400 + args.step, device)
        start = bar_centre(a, h)
        measure = lambda out: bar_centre(out, h) - start      # noqa: E731
    else:
        wide = texture(h, w, device)
        a, b = wide[:, :, :, x0:x0 + w].clone(), wide[:, :, :, x0 + args.step:x0 + args.step + w].clone()
        measure = lambda out: pan_shift(out, wide, x0, args.step)      # noqa: E731

    print(f"RIFE {args.model}, {w}x{h}, {args.scene} moves {args.step} px")
    print("    t   ideal px  measured px  error px   measured as t")
    worst = 0.0
    for i in range(1, 20):
        t = i / 20
        flow, logit = wrapper(a, b, torch.full((1, 1, 1, 1), t, device=device))
        moved = measure(compose(a, b, flow, logit))
        error = moved - t * args.step
        worst = max(worst, abs(error))
        print(f"  {t:4.2f}  {t * args.step:8.2f}  {moved:11.2f}  {error:+8.2f}   {moved / args.step:6.3f}")
    print(f"largest deviation from linear motion: {worst:.2f} px")
    return 0


if __name__ == "__main__":
    sys.exit(main())
