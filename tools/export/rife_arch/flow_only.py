# Export wrapper: the RIFE flow network without its final warp + blend.
#
# Interpolate-it composes the picture itself, at window resolution, from the two real frames. The network
# therefore only has to return the two backward flows and the blend-mask logit at inference resolution.
#
# Differences from upstream (validated against reference.py):
#   * resizes use explicit sizes (h // s) instead of scale factors: identical for sizes that are multiples of
#     the pyramid step, and it keeps symbolic shapes exact during export;
#   * identity resizes (scale 1) are skipped;
#   * image and encoder features share one grid_sample per frame per stage;
#   * the final warp/blend and the sigmoid are left out (the shader applies sigmoid after upsampling the logit).
import torch
import torch.nn as nn
import torch.nn.functional as F

from .warp import make_grid, warp


def _resize(x, h, w):
    return F.interpolate(x, size=(h, w), mode="bilinear", align_corners=False)


def _run_block(blk, x, flow, s, h, w):
    """Replicates IFBlock.forward for an integer pyramid scale `s`. Returns the raw 6/13-channel output at h x w."""
    if s != 1:
        x = _resize(x, h // s, w // s)
    if flow is not None:
        f = flow if s == 1 else _resize(flow, h // s, w // s) * (1.0 / s)
        x = torch.cat((x, f), 1)
    tmp = blk.lastconv(blk.convblock(blk.conv0(x)))
    if s != 1:
        tmp = _resize(tmp, h, w)
    return tmp


class FlowOnly(nn.Module):
    """Inputs : img0, img1  [1, 3, H, W] RGB in [0, 1] (I/O precision), H and W multiples of `net.multiple`
                timestep    [1, 1, 1, 1] in (0, 1)
       Outputs: flow        [1, 4, H, W] pixels at inference resolution: (t->0 x, t->0 y, t->1 x, t->1 y)
                mask_logit  [1, 1, H, W] blend weight of frame 0 before sigmoid
    """

    def __init__(self, net, core_dtype=torch.float32):
        super().__init__()
        self.net = net
        self.core_dtype = core_dtype

    def forward(self, img0, img1, timestep):
        net = self.net
        io_dtype = img0.dtype
        img0 = img0.to(self.core_dtype).clamp(0.0, 1.0)
        img1 = img1.to(self.core_dtype).clamp(0.0, 1.0)
        h, w = img0.shape[2], img0.shape[3]
        grid, norm = make_grid(h, w, img0.device)
        t = timestep.to(self.core_dtype).expand(1, 1, h, w)
        blocks, scales = net.blocks, net.scales

        if net.has_encoder:
            src0 = torch.cat((img0, net.encode(img0)), 1)       # 3 + 4 channels, warped together
            src1 = torch.cat((img1, net.encode(img1)), 1)
            tmp = _run_block(blocks[0], torch.cat((img0, img1, src0[:, 3:], src1[:, 3:], t), 1), None, scales[0], h, w)
            flow, mask, feat = tmp[:, :4] * float(scales[0]), tmp[:, 4:5], tmp[:, 5:]
            for i in range(1, len(blocks)):
                w0 = warp(src0, flow[:, :2], grid, norm)
                w1 = warp(src1, flow[:, 2:4], grid, norm)
                x = torch.cat((w0[:, :3], w1[:, :3], w0[:, 3:], w1[:, 3:], t, mask, feat), 1)
                tmp = _run_block(blocks[i], x, flow, scales[i], h, w)
                flow = flow + tmp[:, :4] * float(scales[i])
                mask, feat = tmp[:, 4:5], tmp[:, 5:]
        else:
            tmp = _run_block(blocks[0], torch.cat((img0, img1, t), 1), None, scales[0], h, w)
            flow, mask = tmp[:, :4] * float(scales[0]), tmp[:, 4:5]
            for i in range(1, len(blocks)):
                w0 = warp(img0, flow[:, :2], grid, norm)
                w1 = warp(img1, flow[:, 2:4], grid, norm)
                tmp = _run_block(blocks[i], torch.cat((w0, w1, t, mask), 1), flow, scales[i], h, w)
                flow = flow + tmp[:, :4] * float(scales[i])
                mask = mask + tmp[:, 4:5]

        return flow.to(io_dtype), mask.to(io_dtype)


@torch.no_grad()
def compose(img0, img1, flow, mask_logit):
    """RIFE's final step in float32: warp both frames and blend. Used for validation and golden images."""
    img0, img1 = img0.float(), img1.float()
    flow, mask_logit = flow.float(), mask_logit.float()
    grid, norm = make_grid(img0.shape[2], img0.shape[3], img0.device)
    m = torch.sigmoid(mask_logit)
    return warp(img0, flow[:, :2], grid, norm) * m + warp(img1, flow[:, 2:4], grid, norm) * (1 - m)
