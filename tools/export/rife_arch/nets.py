# Adapted from Practical-RIFE / vs-rife (MIT). Parameter names must match the published checkpoints.
import torch.nn as nn

from .layers import Head, IFBlock


class IFNet46(nn.Module):
    """RIFE 4.6: four stages (scales 8/4/2/1), no feature encoder, mask is accumulated across stages."""

    version = "4.6"
    scales = (8, 4, 2, 1)
    has_encoder = False
    multiple = 32

    def __init__(self):
        super().__init__()
        self.block0 = IFBlock(7, c=192, out_channels=6)
        self.block1 = IFBlock(8 + 4, c=128, out_channels=6)
        self.block2 = IFBlock(8 + 4, c=96, out_channels=6)
        self.block3 = IFBlock(8 + 4, c=64, out_channels=6)

    @property
    def blocks(self):
        return [self.block0, self.block1, self.block2, self.block3]


class IFNet425(nn.Module):
    """RIFE 4.25: five stages (scales 16/8/4/2/1), Head encoder, mask replaced per stage, 8-channel feature chain."""

    version = "4.25"
    scales = (16, 8, 4, 2, 1)
    has_encoder = True
    multiple = 64

    def __init__(self):
        super().__init__()
        self.block0 = IFBlock(7 + 8, c=192, out_channels=13)
        self.block1 = IFBlock(8 + 4 + 8 + 8, c=128, out_channels=13)
        self.block2 = IFBlock(8 + 4 + 8 + 8, c=96, out_channels=13)
        self.block3 = IFBlock(8 + 4 + 8 + 8, c=64, out_channels=13)
        self.block4 = IFBlock(8 + 4 + 8 + 8, c=32, out_channels=13)
        self.encode = Head()

    @property
    def blocks(self):
        return [self.block0, self.block1, self.block2, self.block3, self.block4]


MODELS = {"4.6": IFNet46, "4.25": IFNet425}


def build_net(version: str) -> nn.Module:
    if version not in MODELS:
        raise ValueError(f"unsupported RIFE version {version!r}; available: {sorted(MODELS)}")
    return MODELS[version]()
