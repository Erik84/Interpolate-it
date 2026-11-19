# Backward warp. Adapted from vs-rife's warplayer.py (MIT); the grid is built from the runtime shape so the
# exported graph stays shape-dynamic, and everything runs in float32 regardless of the model precision
# (an fp16 normalised grid has ~0.5 px spacing near the edges at 1920 px and produces visible stripes).
import torch
import torch.nn.functional as F


def make_grid(h, w, device=None):
    """Returns (grid, norm).

    grid: normalised sampling positions in [-1, 1], shape [1, 2, H, W], float32 (x in channel 0, y in channel 1).
    norm: [1, 2, 1, 1] factors 2/(W-1), 2/(H-1) that convert a pixel flow into normalised units.
    Only tensor arithmetic is used (no Python maths on symbolic sizes) so every exporter can follow it.
    """
    xs = torch.arange(w, dtype=torch.float32, device=device)
    ys = torch.arange(h, dtype=torch.float32, device=device)
    sx = 2.0 / xs[-1]
    sy = 2.0 / ys[-1]
    gx = (xs * sx - 1.0)[None, None, None, :].expand(1, 1, h, w)
    gy = (ys * sy - 1.0)[None, None, :, None].expand(1, 1, h, w)
    grid = torch.cat([gx, gy], 1)
    norm = torch.stack([sx, sy])[None, :, None, None]
    return grid, norm


def warp(ten_input, flow_xy, grid, norm):
    """Sample `ten_input` at pixel + flow (flow in pixels, xy), bilinear, border padding, align_corners=True."""
    dtype = ten_input.dtype
    x = ten_input.to(torch.float32)
    g = (grid + flow_xy.to(torch.float32) * norm).permute(0, 2, 3, 1)
    out = F.grid_sample(x, g, mode="bilinear", padding_mode="border", align_corners=True)
    return out.to(dtype)
