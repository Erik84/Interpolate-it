# Verbatim upstream behaviour (vs-rife IFNet_HDv3_v4_6.py / IFNet_HDv3_v4_25.py, MIT), kept only to validate the
# export wrapper: scale_factor-based resizes, linspace grid, final warp + sigmoid blend. Returns the RGB frame.
import torch
import torch.nn.functional as F


def _ref_warp(ten_input, ten_flow, ten_flow_div, backwarp_grid):
    dtype = ten_input.dtype
    ten_input = ten_input.to(torch.float)
    ten_flow = ten_flow.to(torch.float)
    ten_flow = torch.cat([ten_flow[:, 0:1] / ten_flow_div[0], ten_flow[:, 1:2] / ten_flow_div[1]], 1)
    g = (backwarp_grid + ten_flow).permute(0, 2, 3, 1)
    return F.grid_sample(input=ten_input, grid=g, mode="bilinear", padding_mode="border", align_corners=True).to(dtype)


def _ref_block(blk, x, flow, scale):
    x = F.interpolate(x, scale_factor=1.0 / scale, mode="bilinear")
    if flow is not None:
        flow = F.interpolate(flow, scale_factor=1.0 / scale, mode="bilinear") / scale
        x = torch.cat((x, flow), 1)
    feat = blk.conv0(x)
    feat = blk.convblock(feat)
    tmp = blk.lastconv(feat)
    tmp = F.interpolate(tmp, scale_factor=scale, mode="bilinear")
    return tmp[:, :4] * scale, tmp[:, 4:5], tmp[:, 5:]


@torch.no_grad()
def reference_forward(net, img0, img1, t: float):
    """Upstream RIFE output for one timestep. img0/img1: [1,3,H,W] float in [0,1], sizes already padded."""
    _, _, h, w = img0.shape
    dev, dt = img0.device, img0.dtype
    timestep = torch.full([1, 1, h, w], t, dtype=dt, device=dev)
    ten_flow_div = torch.tensor([(w - 1.0) / 2.0, (h - 1.0) / 2.0], dtype=torch.float, device=dev)
    ten_h = torch.linspace(-1.0, 1.0, w, dtype=torch.float, device=dev).view(1, 1, 1, w).expand(-1, -1, h, -1)
    ten_v = torch.linspace(-1.0, 1.0, h, dtype=torch.float, device=dev).view(1, 1, h, 1).expand(-1, -1, -1, w)
    grid = torch.cat([ten_h, ten_v], 1)

    img0 = img0.clamp(0.0, 1.0)
    img1 = img1.clamp(0.0, 1.0)
    warped0, warped1 = img0, img1
    flow = mask = feat = None
    blocks, scales = net.blocks, net.scales

    if net.has_encoder:
        f0, f1 = net.encode(img0), net.encode(img1)
        for i, blk in enumerate(blocks):
            if flow is None:
                flow, mask, feat = _ref_block(blk, torch.cat((img0, img1, f0, f1, timestep), 1), None, scales[i])
            else:
                wf0 = _ref_warp(f0, flow[:, :2], ten_flow_div, grid)
                wf1 = _ref_warp(f1, flow[:, 2:4], ten_flow_div, grid)
                fd, m0, feat = _ref_block(
                    blk, torch.cat((warped0, warped1, wf0, wf1, timestep, mask, feat), 1), flow, scales[i])
                mask = m0
                flow = flow + fd
            warped0 = _ref_warp(img0, flow[:, :2], ten_flow_div, grid)
            warped1 = _ref_warp(img1, flow[:, 2:4], ten_flow_div, grid)
    else:
        for i, blk in enumerate(blocks):
            if flow is None:
                flow, mask, _ = _ref_block(blk, torch.cat((img0, img1, timestep), 1), None, scales[i])
            else:
                f0, m0, _ = _ref_block(blk, torch.cat((warped0, warped1, timestep, mask), 1), flow, scales[i])
                flow = flow + f0
                mask = mask + m0
            warped0 = _ref_warp(img0, flow[:, :2], ten_flow_div, grid)
            warped1 = _ref_warp(img1, flow[:, 2:4], ten_flow_div, grid)

    mask = torch.sigmoid(mask)
    return warped0 * mask + warped1 * (1 - mask)
