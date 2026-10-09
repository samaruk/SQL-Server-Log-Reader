"""Personalised GTCRN (pGTCRN): a small causal STFT-domain model for owner-voice target-speaker extraction.

Derived from GTCRN by Rong Xiaobin (https://github.com/Xiaobin-Rong/gtcrn, commit 502ebfa, files gtcrn.py,
stream/gtcrn_stream.py and stream/modules/convolution.py), MIT License, Copyright (c) 2024 Rong Xiaobin; streaming
convolution technique by Xiaohuai Le and Xiaobin Rong. The licence text is in LICENSE-GTCRN next to this file.

Changes from upstream (this file is a modified re-implementation, not a copy):
  * channel width is a parameter (upstream: 16) so that capacity can be raised for speaker extraction;
  * optional FiLM conditioning on a speaker embedding after the encoder (initialised to the identity, so with
    emb_dim=0 or zero FiLM weights the network computes exactly GTCRN);
  * SFE written with padding/stack instead of nn.Unfold, and the streaming model passes caches as separate slices
    concatenated with Concat (no in-place ScatterND), which keeps the ONNX graph to common mobile operators.

STATUS: architecture only. No trained pGTCRN weights exist yet. A randomly initialised pGTCRN does NOT extract a
speaker; it is used here for tensor-contract, streaming-parity and compute-cost measurements. See
docs/TSE_MODEL_AUDIT.md for what is needed to train it.
"""
from __future__ import annotations

import numpy as np
import torch
import torch.nn as nn

N_FFT = 512
HOP = 256
FREQS = N_FFT // 2 + 1          # 257
ERB_LOW = 65                    # bins kept linear
ERB_BANDS = 64                  # ERB bands for the rest -> 129 features
WIDTH = 33                      # 129 -> 65 -> 33 after two stride-2 convolutions
DILATIONS = (1, 2, 5)           # temporal dilations of the three GT-conv blocks
CACHE_FRAMES = sum(2 * d for d in DILATIONS)  # 16


def sqrt_hann(n: int = N_FFT) -> torch.Tensor:
    """Periodic Hann window, square-rooted (as torch.hann_window(512).pow(0.5) in upstream)."""
    return torch.hann_window(n, periodic=True).pow(0.5)


class ERB(nn.Module):
    def __init__(self, low: int = ERB_LOW, bands: int = ERB_BANDS, nfft: int = N_FFT, high_lim: float = 8000, fs: int = 16000):
        super().__init__()
        filters = self._filters(low, bands, nfft, high_lim, fs)
        self.erb_subband_1 = low
        self.erb_fc = nn.Linear(nfft // 2 + 1 - low, bands, bias=False)
        self.ierb_fc = nn.Linear(bands, nfft // 2 + 1 - low, bias=False)
        self.erb_fc.weight = nn.Parameter(filters, requires_grad=False)
        self.ierb_fc.weight = nn.Parameter(filters.T.contiguous(), requires_grad=False)

    @staticmethod
    def _filters(low, bands, nfft, high_lim, fs):
        hz2erb = lambda f: 21.4 * np.log10(0.00437 * f + 1)
        erb2hz = lambda e: (10 ** (e / 21.4) - 1) / 0.00437
        points = np.linspace(hz2erb(low / nfft * fs), hz2erb(high_lim), bands)
        bins = np.round(erb2hz(points) / fs * nfft).astype(np.int32)
        f = np.zeros([bands, nfft // 2 + 1], dtype=np.float32)
        f[0, bins[0]:bins[1]] = (bins[1] - np.arange(bins[0], bins[1]) + 1e-12) / (bins[1] - bins[0] + 1e-12)
        for i in range(bands - 2):
            f[i + 1, bins[i]:bins[i + 1]] = (np.arange(bins[i], bins[i + 1]) - bins[i] + 1e-12) / (bins[i + 1] - bins[i] + 1e-12)
            f[i + 1, bins[i + 1]:bins[i + 2]] = (bins[i + 2] - np.arange(bins[i + 1], bins[i + 2]) + 1e-12) / (bins[i + 2] - bins[i + 1] + 1e-12)
        f[-1, bins[-2]:bins[-1] + 1] = 1 - f[-2, bins[-2]:bins[-1] + 1]
        return torch.from_numpy(np.abs(f[:, low:]))

    def bm(self, x):
        return torch.cat([x[..., :self.erb_subband_1], self.erb_fc(x[..., self.erb_subband_1:])], dim=-1)

    def bs(self, x):
        return torch.cat([x[..., :self.erb_subband_1], self.ierb_fc(x[..., self.erb_subband_1:])], dim=-1)


class SFE(nn.Module):
    """Subband feature extraction: each frequency bin with its two neighbours (equals nn.Unfold((1,3), padding (0,1)))."""

    def forward(self, x):  # (B,C,T,F) -> (B,3C,T,F)
        f = x.shape[-1]
        p = nn.functional.pad(x, [1, 1])
        xs = torch.stack([p[..., 0:f], p[..., 1:f + 1], p[..., 2:f + 2]], dim=2)  # (B,C,3,T,F): channel-major like Unfold
        return xs.reshape(x.shape[0], x.shape[1] * 3, x.shape[2], f)


class TRA(nn.Module):
    """Temporal recurrent attention (state = GRU hidden)."""

    def __init__(self, channels):
        super().__init__()
        self.att_gru = nn.GRU(channels, channels * 2, 1, batch_first=True)
        self.att_fc = nn.Linear(channels * 2, channels)
        self.att_act = nn.Sigmoid()

    def forward(self, x, h=None):
        zt = torch.mean(x.pow(2), dim=-1)                      # (B,C,T)
        at, h = self.att_gru(zt.transpose(1, 2), h)
        at = self.att_act(self.att_fc(at).transpose(1, 2))     # (B,C,T)
        return x * at[..., None], h


class ConvBlock(nn.Module):
    def __init__(self, cin, cout, kernel, stride, padding, groups=1, use_deconv=False, is_last=False):
        super().__init__()
        conv = nn.ConvTranspose2d if use_deconv else nn.Conv2d
        self.conv = conv(cin, cout, kernel, stride, padding, groups=groups)
        self.bn = nn.BatchNorm2d(cout)
        self.act = nn.Tanh() if is_last else nn.PReLU()

    def forward(self, x):
        return self.act(self.bn(self.conv(x)))


class GTConvBlock(nn.Module):
    """Group temporal convolution block. Offline form (causal padding in time)."""

    def __init__(self, cin, hidden, dilation, use_deconv=False):
        super().__init__()
        self.use_deconv = use_deconv
        self.dilation = dilation
        self.pad_size = 2 * dilation
        conv = nn.ConvTranspose2d if use_deconv else nn.Conv2d
        self.sfe = SFE()
        self.point_conv1 = conv(cin // 2 * 3, hidden, 1)
        self.point_bn1 = nn.BatchNorm2d(hidden)
        self.point_act = nn.PReLU()
        padding = (2 * dilation, 1) if use_deconv else (0, 1)
        self.depth_conv = conv(hidden, hidden, (3, 3), stride=(1, 1), padding=padding, dilation=(dilation, 1), groups=hidden)
        self.depth_bn = nn.BatchNorm2d(hidden)
        self.depth_act = nn.PReLU()
        self.point_conv2 = conv(hidden, cin // 2, 1)
        self.point_bn2 = nn.BatchNorm2d(cin // 2)
        self.tra = TRA(cin // 2)

    @staticmethod
    def shuffle(x1, x2):
        x = torch.stack([x1, x2], dim=1).transpose(1, 2).contiguous()  # (B,C,2,T,F)
        return x.reshape(x.shape[0], -1, x.shape[3], x.shape[4])

    def front(self, x):
        c = x.shape[1] // 2
        x1, x2 = x[:, :c], x[:, c:]
        h1 = self.point_act(self.point_bn1(self.point_conv1(self.sfe(x1))))
        return h1, x2

    def back(self, h1, x2, tra_h=None):
        h1 = self.point_bn2(self.point_conv2(self.depth_act(self.depth_bn(h1))))
        h1, tra_h = self.tra(h1, tra_h)
        return self.shuffle(h1, x2), tra_h

    def forward(self, x):
        h1, x2 = self.front(x)
        h1 = self.depth_conv(nn.functional.pad(h1, [0, 0, self.pad_size, 0]))
        y, _ = self.back(h1, x2)
        return y


class GRNN(nn.Module):
    def __init__(self, input_size, hidden_size, bidirectional=False):
        super().__init__()
        self.hidden_size = hidden_size
        self.bidirectional = bidirectional
        self.rnn1 = nn.GRU(input_size // 2, hidden_size // 2, 1, batch_first=True, bidirectional=bidirectional)
        self.rnn2 = nn.GRU(input_size // 2, hidden_size // 2, 1, batch_first=True, bidirectional=bidirectional)

    def forward(self, x, h=None):
        if h is None:
            h = torch.zeros(2 if self.bidirectional else 1, x.shape[0], self.hidden_size, device=x.device)
        x1, x2 = torch.chunk(x, 2, dim=-1)
        h1, h2 = torch.chunk(h, 2, dim=-1)
        y1, h1 = self.rnn1(x1, h1.contiguous())
        y2, h2 = self.rnn2(x2, h2.contiguous())
        return torch.cat([y1, y2], dim=-1), torch.cat([h1, h2], dim=-1)


class DPGRNN(nn.Module):
    """Grouped dual-path RNN: bidirectional within a frame (frequency), causal across frames (time)."""

    def __init__(self, channels, width):
        super().__init__()
        self.width = width
        self.hidden_size = channels
        self.intra_rnn = GRNN(channels, channels // 2, bidirectional=True)
        self.intra_fc = nn.Linear(channels, channels)
        self.intra_ln = nn.LayerNorm((width, channels), eps=1e-8)
        self.inter_rnn = GRNN(channels, channels, bidirectional=False)
        self.inter_fc = nn.Linear(channels, channels)
        self.inter_ln = nn.LayerNorm((width, channels), eps=1e-8)

    def forward(self, x, inter_h=None):
        x = x.permute(0, 2, 3, 1)                                            # (B,T,F,C)
        b, t = x.shape[0], x.shape[1]
        intra = self.intra_rnn(x.reshape(b * t, self.width, self.hidden_size))[0]
        intra = self.intra_ln(self.intra_fc(intra).reshape(b, t, self.width, self.hidden_size))
        intra_out = x + intra
        y = intra_out.permute(0, 2, 1, 3).reshape(b * self.width, t, self.hidden_size)  # (B*F,T,C)
        inter, inter_h = self.inter_rnn(y, inter_h)
        inter = self.inter_fc(inter).reshape(b, self.width, t, self.hidden_size).permute(0, 2, 1, 3)
        out = intra_out + self.inter_ln(inter)
        return out.permute(0, 3, 1, 2), inter_h                             # (B,C,T,F)


class FiLM(nn.Module):
    """Speaker conditioning: feature * (1 + gamma(e)) + beta(e). Zero-initialised, i.e. the identity at start."""

    def __init__(self, emb_dim, channels):
        super().__init__()
        self.proj = nn.Linear(emb_dim, 2 * channels)
        nn.init.zeros_(self.proj.weight)
        nn.init.zeros_(self.proj.bias)

    def forward(self, x, emb):  # x (B,C,T,F), emb (B,D)
        gamma, beta = torch.chunk(self.proj(emb), 2, dim=-1)
        return x * (1 + gamma[:, :, None, None]) + beta[:, :, None, None]


def _mask(m, spec):  # complex ratio mask; m, spec: (B,2,T,F)
    real = spec[:, 0] * m[:, 0] - spec[:, 1] * m[:, 1]
    imag = spec[:, 1] * m[:, 0] + spec[:, 0] * m[:, 1]
    return torch.stack([real, imag], dim=1)


def _features(spec, erb, sfe):  # spec (B,F,T,2) -> (B,9,T,129)
    real = spec[..., 0].permute(0, 2, 1)
    imag = spec[..., 1].permute(0, 2, 1)
    mag = torch.sqrt(real ** 2 + imag ** 2 + 1e-12)
    return sfe(erb.bm(torch.stack([mag, real, imag], dim=1)))


class PGTCRN(nn.Module):
    """Offline (whole-utterance) model: spec (B,257,T,2) [+ emb (B,D)] -> enhanced spec (B,257,T,2)."""

    def __init__(self, channels: int = 16, emb_dim: int = 256):
        super().__init__()
        if channels % 4:
            raise ValueError("channels must be a multiple of 4")
        c = channels
        self.channels, self.emb_dim = c, emb_dim
        self.erb = ERB()
        self.sfe = SFE()
        self.encoder = nn.Module()
        self.encoder.en_convs = nn.ModuleList([
            ConvBlock(9, c, (1, 5), (1, 2), (0, 2)),
            ConvBlock(c, c, (1, 5), (1, 2), (0, 2), groups=2),
            *[GTConvBlock(c, c, d) for d in DILATIONS],
        ])
        self.film = FiLM(emb_dim, c) if emb_dim > 0 else None
        self.dpgrnn1 = DPGRNN(c, WIDTH)
        self.dpgrnn2 = DPGRNN(c, WIDTH)
        self.decoder = nn.Module()
        self.decoder.de_convs = nn.ModuleList([
            *[GTConvBlock(c, c, d, use_deconv=True) for d in reversed(DILATIONS)],
            ConvBlock(c, c, (1, 5), (1, 2), (0, 2), groups=2, use_deconv=True),
            ConvBlock(c, 2, (1, 5), (1, 2), (0, 2), use_deconv=True, is_last=True),
        ])

    def forward(self, spec, emb=None):
        x = _features(spec, self.erb, self.sfe)
        outs = []
        for conv in self.encoder.en_convs:
            x = conv(x)
            outs.append(x)
        if self.film is not None:
            x = self.film(x, emb)
        x, _ = self.dpgrnn1(x)
        x, _ = self.dpgrnn2(x)
        for i, conv in enumerate(self.decoder.de_convs):
            x = conv(x + outs[4 - i])
        m = self.erb.bs(x)
        return _mask(m, spec.permute(0, 3, 2, 1)).permute(0, 3, 2, 1)


class _StreamDepthConv(nn.Module):
    """One-frame form of a GT-conv depthwise convolution with a time cache of 2*dilation frames."""

    def __init__(self, offline: nn.Module, dilation: int, deconv: bool):
        super().__init__()
        hidden = offline.weight.shape[0]
        self.conv = nn.Conv2d(hidden, hidden, (3, 3), padding=(0, 1 if not deconv else 0), dilation=(dilation, 1), groups=hidden)
        w = offline.weight.detach().clone()
        if deconv:
            # ConvTranspose2d with padding (2d, 1) on causally padded input == Conv2d with the kernel flipped in time and
            # frequency (depthwise: same weight shape) and frequency padding (k-1)-1 = 1 applied explicitly.
            w = torch.flip(w, dims=[-2, -1])
        self.conv.weight = nn.Parameter(w)
        self.conv.bias = nn.Parameter(offline.bias.detach().clone())
        self.deconv = deconv

    def forward(self, x, cache):  # x (B,C,1,F), cache (B,C,2d,F)
        inp = torch.cat([cache, x], dim=2)
        y = self.conv(nn.functional.pad(inp, [1, 1]) if self.deconv else inp)
        return y, inp[:, :, 1:]


class StreamPGTCRN(nn.Module):
    """
    Frame-by-frame form of a trained PGTCRN (weights shared). One STFT frame per call:
      inputs : mix (1,257,1,2), [spk_emb (1,D)], conv_cache (2,1,C,16,33), tra_cache (2,3,1,1,C), inter_cache (2,1,33,C)
      outputs: enh (1,257,1,2), conv_cache_out, tra_cache_out, inter_cache_out
    """

    def __init__(self, offline: PGTCRN):
        super().__init__()
        self.m = offline
        self.enc_depth = nn.ModuleList([_StreamDepthConv(b.depth_conv, d, False) for b, d in zip(offline.encoder.en_convs[2:], DILATIONS)])
        self.dec_depth = nn.ModuleList([_StreamDepthConv(b.depth_conv, d, True) for b, d in zip(offline.decoder.de_convs[:3], reversed(DILATIONS))])
        self.slices = []  # cache frame ranges per block, encoder order: d=1 -> [0,2), d=2 -> [2,6), d=5 -> [6,16)
        start = 0
        for d in DILATIONS:
            self.slices.append((start, start + 2 * d))
            start += 2 * d

    def forward(self, mix, *rest):
        if self.m.emb_dim > 0:
            emb, conv_cache, tra_cache, inter_cache = rest
        else:
            emb = None
            conv_cache, tra_cache, inter_cache = rest
        m = self.m
        x = _features(mix, m.erb, m.sfe)
        outs = []
        enc_conv, enc_tra = [], []
        for i, block in enumerate(m.encoder.en_convs):
            if i < 2:
                x = block(x)
            else:
                k = i - 2
                a, b = self.slices[k]
                h1, x2 = block.front(x)
                h1, new_cache = self.enc_depth[k](h1, conv_cache[0, :, :, a:b])
                x, h = block.back(h1, x2, tra_cache[0, k])
                enc_conv.append(new_cache)
                enc_tra.append(h)
            outs.append(x)
        if m.film is not None:
            x = m.film(x, emb)
        x, i1 = m.dpgrnn1(x, inter_cache[0])
        x, i2 = m.dpgrnn2(x, inter_cache[1])
        dec_conv, dec_tra = [None, None, None], []
        for i, block in enumerate(m.decoder.de_convs):
            inp = x + outs[4 - i]
            if i < 3:
                d_index = 2 - i  # decoder block 0 uses dilation 5 (cache slice 2)
                a, b = self.slices[d_index]
                h1, x2 = block.front(inp)
                h1, new_cache = self.dec_depth[i](h1, conv_cache[1, :, :, a:b])
                x, h = block.back(h1, x2, tra_cache[1, i])
                dec_conv[d_index] = new_cache
                dec_tra.append(h)
            else:
                x = block(inp)
        mm = m.erb.bs(x)
        enh = _mask(mm, mix.permute(0, 3, 2, 1)).permute(0, 3, 2, 1)
        conv_out = torch.stack([torch.cat(enc_conv, dim=2), torch.cat(dec_conv, dim=2)], dim=0)
        tra_out = torch.stack([torch.stack(enc_tra, dim=0), torch.stack(dec_tra, dim=0)], dim=0)
        inter_out = torch.stack([i1, i2], dim=0)
        return enh, conv_out, tra_out, inter_out

    def zero_state(self, batch: int = 1):
        c = self.m.channels
        return (torch.zeros(2, batch, c, CACHE_FRAMES, WIDTH), torch.zeros(2, 3, 1, batch, c), torch.zeros(2, 1, WIDTH * batch, c))


def stream_stft(audio: np.ndarray) -> np.ndarray:
    """Streaming analysis exactly as the C# adapter does it: zero history, hop 256, periodic sqrt-Hann, frames end at
    every hop boundary. Returns (frames, 257) complex."""
    win = sqrt_hann().numpy().astype(np.float64)
    buf = np.zeros(N_FFT, dtype=np.float64)
    frames = []
    for start in range(0, len(audio) - HOP + 1, HOP):
        buf = np.concatenate([buf[HOP:], audio[start:start + HOP].astype(np.float64)])
        frames.append(np.fft.rfft(buf * win))
    return np.array(frames)


def stream_istft(spectra: np.ndarray) -> np.ndarray:
    """Weighted overlap-add matching the C# adapter: emits HOP samples per frame (delay N_FFT - HOP)."""
    win = sqrt_hann().numpy().astype(np.float64)
    ola = np.zeros(N_FFT, dtype=np.float64)
    out = []
    for spec in spectra:
        ola += np.fft.irfft(spec, N_FFT) * win
        out.append(ola[:HOP].copy())
        ola = np.concatenate([ola[HOP:], np.zeros(HOP)])
    return np.concatenate(out) if out else np.zeros(0)
