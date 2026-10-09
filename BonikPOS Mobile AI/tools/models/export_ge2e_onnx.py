#!/usr/bin/env python3
"""Export the Resemblyzer GE2E speaker encoder to ONNX and validate the export.

Source model
  PyPI package Resemblyzer 0.1.4 (https://pypi.org/project/Resemblyzer/), file resemblyzer/pretrained.pt.
  License: Apache-2.0 (LICENSE file shipped in the wheel and in github.com/resemble-ai/Resemblyzer).
  Architecture (resemblyzer/voice_encoder.py): 3-layer LSTM (40 -> 256), Linear(256 -> 256), ReLU, L2 norm.
  This is the "d-vector" speaker encoder of "Generalized End-to-End Loss for Speaker Verification"
  (Wan et al., 2018), the same embedding family VoiceFilter / VoiceFilter-Lite condition on.

Exported ONNX contract (validated below and again at runtime by the C# adapter)
  input  "mels"      float32 [batch, frames, 40]   power mel spectrogram (NOT log), 16 kHz,
                                                   n_fft=400 (25 ms), hop=160 (10 ms), Slaney mel, Hann window
  output "embedding" float32 [batch, 256]          L2-normalised, non-negative

Usage
  python export_ge2e_onnx.py --out ../../models/speaker/ge2e-resemblyzer-0.1.4.onnx
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import sys

import numpy as np
import onnx
import onnxruntime as ort
import torch


MODEL_ID = "ge2e-resemblyzer"
MODEL_VERSION = "0.1.4"


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


class Ge2eForExport(torch.nn.Module):
    """Same math as resemblyzer.VoiceEncoder.forward, without device handling."""

    def __init__(self, encoder: torch.nn.Module) -> None:
        super().__init__()
        self.lstm = encoder.lstm
        self.linear = encoder.linear

    def forward(self, mels: torch.Tensor) -> torch.Tensor:
        _, (hidden, _) = self.lstm(mels)
        embeds_raw = torch.relu(self.linear(hidden[-1]))
        return embeds_raw / torch.norm(embeds_raw, dim=1, keepdim=True)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, type=pathlib.Path)
    ap.add_argument("--opset", type=int, default=17)
    args = ap.parse_args()

    import resemblyzer
    from resemblyzer import VoiceEncoder, audio, hparams

    weights = pathlib.Path(resemblyzer.__file__).resolve().parent / "pretrained.pt"
    checkpoint = torch.load(weights, map_location="cpu", weights_only=True)
    encoder = VoiceEncoder(device="cpu", verbose=False).eval()
    model = Ge2eForExport(encoder).eval()

    args.out.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.rand(2, hparams.partials_n_frames, hparams.mel_n_channels)
    torch.onnx.export(
        model,
        (dummy,),
        str(args.out),
        input_names=["mels"],
        output_names=["embedding"],
        dynamic_axes={"mels": {0: "batch", 1: "frames"}, "embedding": {0: "batch"}},
        opset_version=args.opset,
        dynamo=False,
    )

    proto = onnx.load(str(args.out))
    meta = {
        "bonik.model_id": MODEL_ID,
        "bonik.model_version": MODEL_VERSION,
        "bonik.role": "speaker-encoder",
        "bonik.license": "Apache-2.0",
        "bonik.source": f"PyPI Resemblyzer {MODEL_VERSION} resemblyzer/pretrained.pt sha256={sha256(weights)} "
                        f"training_step={checkpoint.get('step')}",
        "bonik.features": "mel-power",
        "bonik.sample_rate": str(hparams.sampling_rate),
        "bonik.n_fft": str(int(hparams.sampling_rate * hparams.mel_window_length / 1000)),
        "bonik.hop_length": str(int(hparams.sampling_rate * hparams.mel_window_step / 1000)),
        "bonik.n_mels": str(hparams.mel_n_channels),
        "bonik.partial_frames": str(hparams.partials_n_frames),
        "bonik.embedding_dim": str(hparams.model_embedding_size),
    }
    del proto.metadata_props[:]
    for k, v in meta.items():
        entry = proto.metadata_props.add()
        entry.key, entry.value = k, v
    proto.producer_name = "bonikpos-voicesearch-export"
    onnx.checker.check_model(proto)
    onnx.save(proto, str(args.out))

    # ---- validation against the original PyTorch model ----------------------------------------
    session = ort.InferenceSession(str(args.out), providers=["CPUExecutionProvider"])
    io = {
        "inputs": [(i.name, i.type, i.shape) for i in session.get_inputs()],
        "outputs": [(o.name, o.type, o.shape) for o in session.get_outputs()],
    }
    rng = np.random.default_rng(1234)
    worst = 0.0
    for batch, frames in [(1, 160), (3, 160), (2, 97), (1, 400)]:
        mels = rng.random((batch, frames, hparams.mel_n_channels), dtype=np.float32) * rng.random() * 5
        with torch.no_grad():
            ref = model(torch.from_numpy(mels)).numpy()
        got = session.run(["embedding"], {"mels": mels})[0]
        worst = max(worst, float(np.abs(ref - got).max()))

    # Full utterance path (partials + averaging) on a synthetic harmonic signal.
    t = np.arange(16000 * 3) / 16000.0
    wav = (0.1 * np.sin(2 * np.pi * 140 * t) * (1 + np.sin(2 * np.pi * 4 * t))).astype(np.float32)
    ref_embed = encoder.embed_utterance(wav)
    _, mel_slices = encoder.compute_partial_slices(len(wav), rate=1.3, min_coverage=0.75)
    mel = audio.wav_to_mel_spectrogram(np.pad(wav, (0, max(0, mel_slices[-1].stop * 160 - len(wav)))))
    partials = np.stack([mel[s] for s in mel_slices]).astype(np.float32)
    onnx_partials = session.run(["embedding"], {"mels": partials})[0]
    onnx_embed = onnx_partials.mean(axis=0)
    onnx_embed /= np.linalg.norm(onnx_embed)
    utterance_cos = float(np.dot(ref_embed, onnx_embed))

    report = {
        "file": str(args.out),
        "sha256": sha256(args.out),
        "bytes": args.out.stat().st_size,
        "opset": args.opset,
        "io": io,
        "max_abs_diff_vs_pytorch": worst,
        "utterance_cosine_vs_resemblyzer": utterance_cos,
        "metadata": meta,
    }
    print(json.dumps(report, indent=2))
    if worst > 1e-4 or utterance_cos < 0.9999:
        print("VALIDATION FAILED", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
