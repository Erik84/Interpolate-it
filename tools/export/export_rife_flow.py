#!/usr/bin/env python
"""Export a RIFE 4.x flow network to ONNX (flow + mask logit, no final warp/blend) and validate it.

Run isolated, from the repository root:

    tools\\export\\.venv\\Scripts\\python.exe -I tools\\export\\export_rife_flow.py --model 4.25 --verify --golden

Outputs (under models\\):
    rife-v<ver>-flow-fp16.onnx   fp16 network, float32 warp island   (GPU backends)
    rife-v<ver>-flow-fp32.onnx   fp32 network                         (CPU backend)
    manifest.json                model list consumed by the app
    golden\\...                   reference frames and outputs for the C# benchmark

Both variants use fp16 planar NCHW tensors at the boundary so the GPU transport code is identical.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))  # the interpreter runs with -I, so add our own package directory explicitly
for _stream in (sys.stdout, sys.stderr):  # the exporter prints emoji; a cp1252 console would raise
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

import numpy as np  # noqa: E402
import torch  # noqa: E402
import torch.nn.functional as F  # noqa: E402
from PIL import Image  # noqa: E402

from rife_arch import FlowOnly, build_net, reference_forward  # noqa: E402
from rife_arch.flow_only import compose  # noqa: E402

REPO = HERE.parent.parent
INPUT_NAMES = ["img0", "img1", "timestep"]
OUTPUT_NAMES = ["flow", "mask_logit"]
STEP = 64  # every exported model accepts heights/widths that are multiples of 64
IO_DTYPE = torch.float16


# --------------------------------------------------------------------------- model loading

def load_net(version: str, weights: Path):
    net = build_net(version)
    state = torch.load(weights, map_location="cpu", weights_only=True)  # data only, never unpickles code
    state = {(k[len("module."):] if k.startswith("module.") else k): v for k, v in state.items()}
    own = net.state_dict()
    missing = [k for k in own if k not in state]
    wrong = [k for k in own if k in state and tuple(own[k].shape) != tuple(state[k].shape)]
    if missing or wrong:
        raise SystemExit(f"checkpoint does not match RIFE {version}: missing={missing[:5]} shape-mismatch={wrong[:5]}")
    net.load_state_dict({k: state[k] for k in own}, strict=True)
    unused = sorted(k for k in state if k not in own)
    return net.eval(), unused


# --------------------------------------------------------------------------- test content

def synth_scene(h: int, w: int, t: float, seed: int = 7) -> torch.Tensor:
    """Textured static background, static thin lines, and three textured rectangles moving linearly with t."""
    g = torch.Generator().manual_seed(seed)

    def field(gh: int, gw: int, mode: str) -> torch.Tensor:
        z = torch.rand(1, 3, gh, gw, generator=g)
        return F.interpolate(z, size=(h, w), mode=mode, align_corners=False).clamp(0, 1)

    img = 0.65 * field(h // 48 + 2, w // 48 + 2, "bicubic") + 0.35 * field(h // 6 + 2, w // 6 + 2, "bilinear")

    ys, xs = slice(int(0.75 * h), int(0.95 * h)), slice(int(0.05 * w), int(0.25 * w))
    patch = img[:, :, ys, xs]
    patch[:, :, ::6, :] = 1.0
    patch[:, :, :, ::9] = 0.0

    s = w / 1280.0  # displacements are defined at 1280 px width; even numbers keep t=0.5 on the pixel grid
    objects = [(0.25, 0.30, 90, 60, 48, 0), (0.60, 0.55, 70, 70, -32, 24), (0.80, 0.25, 50, 80, 0, -40)]
    for i, (cx, cy, half_w, half_h, dx, dy) in enumerate(objects):
        gi = torch.Generator().manual_seed(seed * 100 + i)
        ph, pw = max(8, int(2 * half_h * s)), max(8, int(2 * half_w * s))
        tex = torch.rand(1, 3, ph // 5 + 2, pw // 5 + 2, generator=gi)
        tex = (F.interpolate(tex, size=(ph, pw), mode="bilinear", align_corners=False) * 1.4 - 0.2).clamp(0, 1)
        x0 = int(round(cx * w + t * dx * s)) - pw // 2
        y0 = int(round(cy * h + t * dy * s)) - ph // 2
        img[:, :, y0:y0 + ph, x0:x0 + pw] = tex
    return img.clamp(0, 1)


def to_u8(img: torch.Tensor) -> np.ndarray:
    return (img[0].detach().float().cpu().clamp(0, 1) * 255.0 + 0.5).byte().permute(1, 2, 0).numpy()


def from_u8(arr: np.ndarray, device) -> torch.Tensor:
    return torch.from_numpy(arr).to(device).permute(2, 0, 1)[None].float() / 255.0


def quantise(img: torch.Tensor) -> torch.Tensor:
    """Round-trip through 8 bit, as captured frames are."""
    return from_u8(to_u8(img), img.device)


def psnr(a: torch.Tensor, b: torch.Tensor) -> float:
    mse = torch.mean((a.float() - b.float()) ** 2).item()
    return 99.0 if mse <= 1e-12 else float(10.0 * np.log10(1.0 / mse))


def timestep_tensor(t: float, device, dtype=IO_DTYPE) -> torch.Tensor:
    return torch.full((1, 1, 1, 1), t, device=device, dtype=dtype)


# --------------------------------------------------------------------------- checks in PyTorch

@torch.no_grad()
def check_wrapper_matches_upstream(net, device) -> list[str]:
    """The export wrapper + compose() must reproduce upstream RIFE's frame."""
    problems = []
    wrapper = FlowOnly(net, torch.float32).to(device).eval()
    for (h, w), t in [((256, 448), 0.5), ((704, 1280), 0.25)]:
        a, b = synth_scene(h, w, 0.0).to(device), synth_scene(h, w, 1.0).to(device)
        ref = reference_forward(net, a, b, t)
        flow, logit = wrapper(a, b, timestep_tensor(t, device, torch.float32))
        out = compose(a, b, flow, logit)
        err, quality = (out - ref).abs().max().item(), psnr(out, ref)
        print(f"  wrapper vs upstream  {w}x{h} t={t}: max|diff|={err:.2e}  PSNR={quality:.1f} dB")
        # Judged on PSNR: the worst pixel moves by ~1e-3 between two identical cuDNN runs of RIFE 4.6 on this
        # content, so a max-abs limit would be testing GPU nondeterminism. A wrong graph lands far below 80 dB.
        if quality < 80.0:
            problems.append(f"wrapper deviates from upstream at {w}x{h}: PSNR {quality:.1f} dB, max {err:.3e}")
    return problems


# --------------------------------------------------------------------------- export

def export_onnx(wrapper, path: Path, device, opset: int, legacy: bool) -> str:
    h, w = 256, 448
    args = (
        torch.rand(1, 3, h, w, device=device).to(IO_DTYPE),
        torch.rand(1, 3, h, w, device=device).to(IO_DTYPE),
        timestep_tensor(0.5, device),
    )
    path.parent.mkdir(parents=True, exist_ok=True)

    if not legacy:
        try:
            from torch.export import Dim

            hb, wb = Dim("hb", min=2, max=64), Dim("wb", min=2, max=64)
            shapes = {"img0": {2: STEP * hb, 3: STEP * wb}, "img1": {2: STEP * hb, 3: STEP * wb}, "timestep": None}
            torch.onnx.export(
                wrapper, args, str(path), dynamo=True, dynamic_shapes=shapes,
                input_names=INPUT_NAMES, output_names=OUTPUT_NAMES,
                opset_version=opset, external_data=False, optimize=True,
            )
            return "dynamo"
        except Exception as exc:  # noqa: BLE001 - any exporter failure falls back to the tracing exporter
            print(f"  dynamo exporter failed ({type(exc).__name__}: {str(exc).splitlines()[0][:160]}); using legacy exporter")

    axes = {2: "height", 3: "width"}
    torch.onnx.export(
        wrapper, args, str(path), dynamo=False,
        input_names=INPUT_NAMES, output_names=OUTPUT_NAMES, opset_version=min(opset, 20),
        dynamic_axes={"img0": axes, "img1": axes, "flow": axes, "mask_logit": axes},
        do_constant_folding=True,
    )
    return "legacy"


def finalize(path: Path, meta: dict) -> dict:
    """Give the dynamic dims stable names, attach metadata, validate, and describe the file."""
    import onnx

    model = onnx.load(str(path))
    for vi in list(model.graph.input) + list(model.graph.output):
        dims = vi.type.tensor_type.shape.dim
        if vi.name != "timestep" and len(dims) == 4:
            dims[2].dim_param = "height"
            dims[3].dim_param = "width"
    del model.graph.value_info[:]  # intermediate shapes still carry the exporter's symbol names
    del model.metadata_props[:]
    for k, v in meta.items():
        entry = model.metadata_props.add()
        entry.key, entry.value = k, str(v)
    onnx.checker.check_model(model)
    onnx.save(model, str(path))

    ops: dict[str, int] = {}
    for node in model.graph.node:
        ops[node.op_type] = ops.get(node.op_type, 0) + 1
    opset = max((o.version for o in model.opset_import if o.domain in ("", "ai.onnx")), default=0)
    return {
        "opset": opset,
        "nodes": len(model.graph.node),
        "grid_sample_nodes": ops.get("GridSample", 0),
        "functions": len(model.functions),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "bytes": path.stat().st_size,
    }


# --------------------------------------------------------------------------- checks in ONNX Runtime

def ort_session(path: Path, provider: str, h: int, w: int):
    """Static-shape session, the way the app creates one per resolution bucket."""
    import onnxruntime as ort

    so = ort.SessionOptions()
    so.log_severity_level = 4
    so.add_free_dimension_override_by_name("height", h)
    so.add_free_dimension_override_by_name("width", w)
    if provider == "DmlExecutionProvider":
        so.enable_mem_pattern = False
        so.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    return ort.InferenceSession(str(path), so, providers=[provider])


@torch.no_grad()
def check_onnx(path: Path, net, device, provider: str, limits: tuple[float, float, float]) -> list[str]:
    """Compare the ONNX model with the fp32 PyTorch wrapper at two sizes (also proves the dynamic axes work)."""
    max_flow, max_logit, min_psnr = limits
    problems = []
    wrapper = FlowOnly(net, torch.float32).to(device).eval()
    for (h, w), t in [((256, 448), 0.5), ((704, 1280), 0.25), ((1088, 1920), 0.5)]:
        try:
            sess = ort_session(path, provider, h, w)
        except Exception as exc:  # noqa: BLE001
            problems.append(f"{path.name} [{provider}] {w}x{h}: session failed: {str(exc).splitlines()[0][:200]}")
            continue
        a = quantise(synth_scene(h, w, 0.0)).to(device)
        b = quantise(synth_scene(h, w, 1.0)).to(device)
        a16, b16 = a.to(IO_DTYPE), b.to(IO_DTYPE)
        ref_flow, ref_logit = wrapper(a16, b16, timestep_tensor(t, device))
        ref_img = compose(a, b, ref_flow, ref_logit)
        feeds = {
            "img0": a16.cpu().numpy(), "img1": b16.cpu().numpy(),
            "timestep": np.full((1, 1, 1, 1), t, dtype=np.float16),
        }
        try:
            flow_np, logit_np = sess.run(OUTPUT_NAMES, feeds)
        except Exception as exc:  # noqa: BLE001
            problems.append(f"{path.name} [{provider}] {w}x{h}: run failed: {str(exc).splitlines()[0][:200]}")
            continue
        flow = torch.from_numpy(flow_np.astype(np.float32)).to(device)
        logit = torch.from_numpy(logit_np.astype(np.float32)).to(device)
        # 99.9th percentile rather than the worst pixel: where the flow is ill-conditioned (flat or noisy
        # texture) a rounding difference between runtimes is amplified through the pyramid at a few pixels.
        e_flow = (flow - ref_flow.float()).abs().flatten()
        e_logit = (logit - ref_logit.float()).abs().flatten()
        d_flow, d_logit = torch.quantile(e_flow, 0.999).item(), torch.quantile(e_logit, 0.999).item()
        quality = psnr(compose(a, b, flow, logit), ref_img)
        ok = d_flow <= max_flow and d_logit <= max_logit and quality >= min_psnr
        print(f"  {path.name} [{provider}] {w}x{h} t={t}: p99.9|dflow|={d_flow:.4f} px (max {e_flow.max().item():.3f})"
              f"  p99.9|dlogit|={d_logit:.4f}  image PSNR={quality:.1f} dB  {'ok' if ok else 'FAIL'}")
        if not ok:
            problems.append(f"{path.name} [{provider}] {w}x{h}: dflow={d_flow:.4f} dlogit={d_logit:.4f} psnr={quality:.1f}")
    return problems


# --------------------------------------------------------------------------- golden data for the C# bench

@torch.no_grad()
def pipeline_reference(wrapper, full0, full1, t: float, inf_h: int, inf_w: int):
    """What Interpolate-it computes: area-downscale -> flow net -> upsample flow/logit -> full-resolution warp."""
    H, W = full0.shape[2:]
    same = (H, W) == (inf_h, inf_w)
    s0 = full0 if same else F.interpolate(full0, size=(inf_h, inf_w), mode="area")
    s1 = full1 if same else F.interpolate(full1, size=(inf_h, inf_w), mode="area")
    flow, logit = wrapper(s0.to(IO_DTYPE), s1.to(IO_DTYPE), timestep_tensor(t, full0.device))
    flow16, logit16 = flow.to(IO_DTYPE), logit.to(IO_DTYPE)
    flow, logit = flow16.float(), logit16.float()
    if not same:
        scale = torch.tensor([W / inf_w, H / inf_h, W / inf_w, H / inf_h], device=flow.device).view(1, 4, 1, 1)
        flow = F.interpolate(flow, size=(H, W), mode="bilinear", align_corners=False) * scale
        logit = F.interpolate(logit, size=(H, W), mode="bilinear", align_corners=False)
    return compose(full0, full1, flow, logit), flow16, logit16, s0, s1


@torch.no_grad()
def write_golden(net, version: str, out: Path, device) -> None:
    wrapper = FlowOnly(net, torch.float32).to(device).eval()
    root = out / "golden"
    cases = []
    for name, (H, W), (ih, iw), t in [
        ("x1", (704, 1280), (704, 1280), 0.5),
        ("x2", (1408, 2560), (704, 1280), 0.5),
        ("x2-t025", (1408, 2560), (704, 1280), 0.25),
    ]:
        frames = root / "frames" / f"{W}x{H}"
        frames.mkdir(parents=True, exist_ok=True)
        f0, f1, gt = (to_u8(synth_scene(H, W, x)) for x in (0.0, 1.0, t))
        for fname, arr in (("frame0.png", f0), ("frame1.png", f1), (f"gt_t{int(t * 1000):04d}.png", gt)):
            if not (frames / fname).exists():
                Image.fromarray(arr).save(frames / fname)
        a, b = from_u8(f0, device), from_u8(f1, device)
        img, flow16, logit16, _, _ = pipeline_reference(wrapper, a, b, t, ih, iw)
        case_dir = root / f"rife-v{version}" / name
        case_dir.mkdir(parents=True, exist_ok=True)
        Image.fromarray(to_u8(img)).save(case_dir / "ref.png")
        flow16.cpu().numpy().astype("<f2").tofile(case_dir / "flow.f16")
        logit16.cpu().numpy().astype("<f2").tofile(case_dir / "mask_logit.f16")
        quality = psnr(quantise(img), from_u8(gt, device))
        cases.append({
            "name": name, "model": f"rife-v{version}", "t": t,
            "width": W, "height": H, "inference_width": iw, "inference_height": ih,
            "frame0": f"frames/{W}x{H}/frame0.png", "frame1": f"frames/{W}x{H}/frame1.png",
            "ground_truth": f"frames/{W}x{H}/gt_t{int(t * 1000):04d}.png",
            "reference": f"rife-v{version}/{name}/ref.png",
            "flow": f"rife-v{version}/{name}/flow.f16", "mask_logit": f"rife-v{version}/{name}/mask_logit.f16",
            "psnr_vs_ground_truth": round(quality, 2),
        })
        print(f"  golden {name}: {W}x{H} via {iw}x{ih}, t={t}: PSNR vs true in-between frame = {quality:.1f} dB")
    index = root / "golden.json"
    data = json.loads(index.read_text()) if index.exists() else {"cases": []}
    data["cases"] = [c for c in data["cases"] if c["model"] != f"rife-v{version}"] + cases
    index.write_text(json.dumps(data, indent=2))


# --------------------------------------------------------------------------- main

def update_manifest(out: Path, entries: list[dict]) -> None:
    path = out / "manifest.json"
    data = json.loads(path.read_text()) if path.exists() else {"models": []}
    files = {e["file"] for e in entries}
    data["models"] = sorted([m for m in data["models"] if m["file"] not in files] + entries, key=lambda m: m["file"])
    path.write_text(json.dumps(data, indent=2))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", required=True, choices=["4.6", "4.25"])
    ap.add_argument("--weights", type=Path, help="default: models/weights/flownet_v<model>.pkl")
    ap.add_argument("--out", type=Path, default=REPO / "models")
    ap.add_argument("--precision", choices=["fp16", "fp32", "both"], default="both")
    # Opset 18 on purpose: at opset 20 GridSample is a new operator version that the DirectML provider does
    # not implement, so all warps silently ran on the CPU (measured: 740 ms instead of < 1 ms per inference).
    ap.add_argument("--opset", type=int, default=18)
    ap.add_argument("--legacy-exporter", action="store_true", help="use the tracing exporter instead of dynamo")
    ap.add_argument("--verify", action="store_true", help="validate against PyTorch; non-zero exit on mismatch")
    ap.add_argument("--golden", action="store_true", help="write reference frames/outputs for the C# benchmark")
    args = ap.parse_args()

    weights = args.weights or (args.out / "weights" / f"flownet_v{args.model}.pkl")
    if not weights.exists():
        raise SystemExit(f"{weights} not found; run tools\\export\\fetch_weights.ps1 first")
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"RIFE {args.model}  torch {torch.__version__}  device {device}")

    net, unused = load_net(args.model, weights)
    net = net.to(device)
    if unused:
        print(f"  checkpoint keys not used by the flow network: {len(unused)} (e.g. {unused[0]})")

    problems: list[str] = []
    if args.verify:
        problems += check_wrapper_matches_upstream(net, device)

    import onnxruntime as ort

    providers = ort.get_available_providers()
    precisions = ["fp16", "fp32"] if args.precision == "both" else [args.precision]
    entries = []
    for precision in precisions:
        core = torch.float16 if precision == "fp16" else torch.float32
        if precision == "fp16" and device.type != "cuda":
            print("  fp16 export needs CUDA for tracing; skipped")
            continue
        wrapper = FlowOnly(build_net(args.model), core)
        wrapper.net.load_state_dict(net.state_dict())
        wrapper = wrapper.to(device).eval()
        if precision == "fp16":
            wrapper.net.half()
        path = args.out / f"rife-v{args.model}-flow-{precision}.onnx"
        with torch.no_grad():
            exporter = export_onnx(wrapper, path, device, args.opset, args.legacy_exporter)
        info = finalize(path, {"rife_version": args.model, "precision": precision, "multiple": STEP,
                               "exporter": exporter, "interpolatit_format": 1})
        print(f"  wrote {path.name}: {info['bytes'] / 1e6:.1f} MB, opset {info['opset']}, {info['nodes']} nodes, "
              f"{info['grid_sample_nodes']} GridSample, exporter={exporter}")
        entries.append({
            "id": f"rife-v{args.model}", "version": args.model, "file": path.name, "precision": precision,
            "multiple": STEP, "io_dtype": "float16", "layout": "NCHW",
            "inputs": INPUT_NAMES, "outputs": OUTPUT_NAMES, **info, "exporter": exporter,
        })
        if args.verify:
            # Limits are (p99.9 flow error in px, p99.9 logit error, image PSNR in dB) against fp32 PyTorch.
            # The image PSNR is the real criterion (a wrong graph gives < 30 dB); the flow/logit bounds only
            # catch gross errors, because RIFE 4.6 is ill-conditioned on the noise-textured test objects and
            # amplifies runtime rounding differences to ~0.5 px at a few thousand pixels.
            if precision == "fp32":
                problems += check_onnx(path, net, device, "CPUExecutionProvider", (1.0, 1.0, 55.0))
            elif "DmlExecutionProvider" in providers:
                problems += check_onnx(path, net, device, "DmlExecutionProvider", (1.0, 1.0, 45.0))
            else:
                print(f"  {path.name}: no fp16-capable provider in this Python environment; "
                      "numeric check is done by the C# benchmark")

    update_manifest(args.out, entries)
    if args.golden:
        write_golden(net, args.model, args.out, device)

    if problems:
        print("\nFAILED:")
        for p in problems:
            print("  - " + p)
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
