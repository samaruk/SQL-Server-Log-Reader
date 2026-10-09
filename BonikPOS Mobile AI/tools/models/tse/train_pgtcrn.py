#!/usr/bin/env python3
"""Training recipe for pGTCRN owner-voice target-speaker extraction (see pgtcrn.py, docs/TSE_MODEL_AUDIT.md).

What it trains: given a mixture (owner + other talkers + noise + room) and a 256-dim GE2E d-vector of the owner taken
from a DIFFERENT utterance (as at enrollment), output the owner's speech only. Mixtures are generated on the fly:

  * target speech from speaker S, enrollment embedding from another utterance of S;
  * 0-2 interfering talkers at signal-to-interference ratio SIR in [-5, 10] dB with random overlap;
  * optional noise at SNR in [0, 25] dB and optional room impulse response;
  * 15 % "target absent" mixtures (owner silent: the output must be silence) and 10 % clean owner speech (the model
    must not damage it) -- the second guards against over-suppression, which hurts recognition.

Loss: SI-SNR on the waveform + power-compressed (0.3) complex spectral MSE (GTCRN-style); for target-absent mixtures
the output energy is penalised instead of SI-SNR.

DATA LICENCES: every manifest line carries a "license" field. Lines whose licence is non-commercial, no-derivatives or
unknown are refused unless --allow-noncommercial-research is given, in which case the checkpoint is marked
"NON-COMMERCIAL RESEARCH ONLY" and export_pgtcrn_onnx.py --register should not be used for the app.

Manifest format (JSON Lines), one per utterance / noise / RIR file:
  {"path": "/data/LibriSpeech/train-clean-100/19/198/19-198-0001.flac", "speaker": "ls-19", "license": "CC-BY-4.0", "corpus": "LibriSpeech"}

Usage
  python train_pgtcrn.py --speech speech.jsonl --noise noise.jsonl [--rir rir.jsonl] --out ckpt_dir --channels 32
  python train_pgtcrn.py --smoke --out /tmp/pgtcrn-smoke     # 20 steps on the bundled SYNTHETIC test clips (code check only)

Requires: torch, numpy, soundfile, scipy, resemblyzer (Apache-2.0; same encoder the app ships as ONNX).
"""
from __future__ import annotations

import os

os.environ.setdefault("ORT_DISABLE_TELEMETRY", "1")

import argparse
import json
import math
import pathlib
import random
import sys
import time

import numpy as np
import soundfile
import torch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from pgtcrn import HOP, N_FFT, PGTCRN, sqrt_hann  # noqa: E402

SR = 16000
COMMERCIAL_OK = {"CC0-1.0", "CC-BY-4.0", "CC-BY-3.0", "PUBLIC-DOMAIN", "APACHE-2.0", "MIT", "OWN-CONSENTED"}


def load_manifest(path: pathlib.Path | None, allow_nc: bool) -> list[dict]:
    if path is None:
        return []
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    bad = [r for r in rows if str(r.get("license", "")).upper() not in COMMERCIAL_OK]
    if bad and not allow_nc:
        lic = sorted({str(r.get("license")) for r in bad})
        raise SystemExit(f"{path}: {len(bad)} entries have licences not cleared for commercial use: {lic}. "
                         "Remove them or pass --allow-noncommercial-research (checkpoint will be marked).")
    return rows


def read_audio(path: str, seconds: float | None = None) -> np.ndarray:
    audio, sr = soundfile.read(path, dtype="float32", always_2d=False)
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    if sr != SR:
        from scipy.signal import resample_poly
        g = math.gcd(sr, SR)
        audio = resample_poly(audio, SR // g, sr // g).astype(np.float32)
    if seconds is not None:
        n = int(seconds * SR)
        if len(audio) >= n:
            start = random.randint(0, len(audio) - n)
            audio = audio[start:start + n]
        else:
            audio = np.pad(audio, (0, n - len(audio)))
    return audio


def rms(x: np.ndarray) -> float:
    return float(np.sqrt(np.mean(x ** 2) + 1e-12))


class Embedder:
    """GE2E d-vectors from Resemblyzer (the encoder the app ships as ge2e-resemblyzer ONNX), cached per file."""

    def __init__(self) -> None:
        from resemblyzer import VoiceEncoder
        self.encoder = VoiceEncoder(device="cpu", verbose=False)
        self.cache: dict[str, np.ndarray] = {}

    def __call__(self, path: str) -> np.ndarray:
        if path not in self.cache:
            self.cache[path] = self.encoder.embed_utterance(read_audio(path)).astype(np.float32)
        return self.cache[path]


class MixtureSampler:
    def __init__(self, speech: list[dict], noise: list[dict], rir: list[dict], seconds: float, embed: Embedder) -> None:
        self.by_speaker: dict[str, list[dict]] = {}
        for r in speech:
            self.by_speaker.setdefault(r["speaker"], []).append(r)
        self.speakers = [s for s, rows in self.by_speaker.items() if len(rows) >= 2]  # need target + enrollment
        if len(self.speakers) < 2:
            raise SystemExit("Need at least two speakers with two or more utterances each.")
        self.noise, self.rir, self.seconds, self.embed = noise, rir, seconds, embed

    def sample(self):
        spk = random.choice(self.speakers)
        target_row, enrol_row = random.sample(self.by_speaker[spk], 2)
        n = int(self.seconds * SR)
        r = random.random()
        absent = r < 0.15
        clean = 0.15 <= r < 0.25
        target = np.zeros(n, np.float32) if absent else read_audio(target_row["path"], self.seconds)
        mix = target.copy()
        if not clean:
            others = [s for s in self.speakers if s != spk]
            for _ in range(random.choice([1, 1, 2])):
                other = read_audio(random.choice(self.by_speaker[random.choice(others)])["path"], self.seconds)
                shift = random.randint(-n // 2, n // 2)
                other = np.roll(other, shift)
                ref = rms(target) if not absent else 0.05
                sir = random.uniform(-5, 10)
                mix += other * (ref / (rms(other) * 10 ** (sir / 20)))
            if self.noise and random.random() < 0.8:
                nz = read_audio(random.choice(self.noise)["path"], self.seconds)
                snr = random.uniform(0, 25)
                mix += nz * (max(rms(mix), 1e-3) / (rms(nz) * 10 ** (snr / 20)))
        if self.rir and random.random() < 0.5:
            from scipy.signal import fftconvolve
            h = read_audio(random.choice(self.rir)["path"])
            h = h / (np.abs(h).max() + 1e-9)
            mix = fftconvolve(mix, h)[:n].astype(np.float32)
            target = fftconvolve(target, h[: int(0.05 * SR)])[:n].astype(np.float32)  # early reflections stay in the target
        peak = max(np.abs(mix).max(), 1e-6)
        gain = random.uniform(0.1, 0.9) / peak
        return (mix * gain).astype(np.float32), (target * gain).astype(np.float32), self.embed(enrol_row["path"]), absent


def stft(x: torch.Tensor) -> torch.Tensor:  # (B,N) -> (B,257,T,2)
    return torch.view_as_real(torch.stft(x, N_FFT, HOP, N_FFT, sqrt_hann().to(x.device), return_complex=True))


def istft(s: torch.Tensor, length: int) -> torch.Tensor:
    return torch.istft(torch.view_as_complex(s.contiguous()), N_FFT, HOP, N_FFT, sqrt_hann().to(s.device), length=length)


def si_snr(est: torch.Tensor, ref: torch.Tensor) -> torch.Tensor:
    est = est - est.mean(-1, keepdim=True)
    ref = ref - ref.mean(-1, keepdim=True)
    proj = (est * ref).sum(-1, keepdim=True) * ref / (ref.pow(2).sum(-1, keepdim=True) + 1e-8)
    noise = est - proj
    return 10 * torch.log10(proj.pow(2).sum(-1) / (noise.pow(2).sum(-1) + 1e-8) + 1e-8)


def loss_fn(est_spec, ref_spec, est_wave, ref_wave, absent):
    def compress(s):
        mag = torch.sqrt(s[..., 0] ** 2 + s[..., 1] ** 2 + 1e-12)
        return s * (mag ** (0.3 - 1))[..., None], mag ** 0.3

    ce, cm = compress(est_spec)
    cr, crm = compress(ref_spec)
    spec_loss = 0.3 * (ce - cr).pow(2).mean(dim=(1, 2, 3)) + 0.7 * (cm - crm).pow(2).mean(dim=(1, 2))
    present = ~absent
    wave_loss = torch.zeros_like(spec_loss)
    if present.any():
        wave_loss[present] = -si_snr(est_wave[present], ref_wave[present]) * 0.01
    if absent.any():
        wave_loss[absent] = 10 * torch.log10(est_wave[absent].pow(2).mean(-1) + 1e-8) * 0.01  # push output energy down
    return (spec_loss + wave_loss).mean()


def smoke_manifests(root: pathlib.Path) -> tuple[list[dict], list[dict]]:
    audio = root / "tests/fixtures/audio"
    speech = []
    for f in sorted(audio.glob("synthetic-voice*.wav")):
        spk = f.name.split("-")[1]  # voiceA / voiceB / voiceC
        speech.append({"path": str(f), "speaker": spk, "license": "SYNTHETIC-TTS-FIXTURE", "corpus": "synthetic-tts-fixture"})
    noise = [{"path": str(audio / "noise-2s.wav"), "license": "SYNTHETIC-TTS-FIXTURE", "corpus": "synthetic-noise"}]
    return speech, noise


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--speech", type=pathlib.Path)
    ap.add_argument("--noise", type=pathlib.Path)
    ap.add_argument("--rir", type=pathlib.Path)
    ap.add_argument("--out", type=pathlib.Path, required=True)
    ap.add_argument("--channels", type=int, default=32)
    ap.add_argument("--seconds", type=float, default=4.0)
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--steps", type=int, default=200000)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--allow-noncommercial-research", action="store_true")
    ap.add_argument("--smoke", action="store_true", help="20 steps on the bundled synthetic clips (checks the code runs)")
    args = ap.parse_args()

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    if args.smoke:
        speech, noise = smoke_manifests(pathlib.Path(__file__).resolve().parents[3])
        rir: list[dict] = []
        args.steps, args.batch, args.seconds = 20, 2, 2.0
    else:
        speech = load_manifest(args.speech, args.allow_noncommercial_research)
        noise = load_manifest(args.noise, args.allow_noncommercial_research)
        rir = load_manifest(args.rir, args.allow_noncommercial_research)
    sampler = MixtureSampler(speech, noise, rir, args.seconds, Embedder())
    model = PGTCRN(channels=args.channels, emb_dim=256).train()
    opt = torch.optim.Adam([p for p in model.parameters() if p.requires_grad], lr=args.lr)
    args.out.mkdir(parents=True, exist_ok=True)
    licences = sorted({str(r.get("license")) for r in speech + noise + rir})
    non_commercial = any(lic.upper() not in COMMERCIAL_OK for lic in licences)
    history = []
    t0 = time.time()
    for step in range(1, args.steps + 1):
        batch = [sampler.sample() for _ in range(args.batch)]
        mix = torch.from_numpy(np.stack([b[0] for b in batch]))
        ref = torch.from_numpy(np.stack([b[1] for b in batch]))
        emb = torch.from_numpy(np.stack([b[2] for b in batch]))
        absent = torch.tensor([b[3] for b in batch])
        mix_spec, ref_spec = stft(mix), stft(ref)
        est_spec = model(mix_spec, emb)
        est = istft(est_spec, mix.shape[-1])
        loss = loss_fn(est_spec, ref_spec, est, ref, absent)
        opt.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        opt.step()
        history.append(float(loss))
        if step % 10 == 0 or step == args.steps:
            print(f"step {step} loss {np.mean(history[-10:]):.4f} ({time.time() - t0:.0f} s)")
        if step % 5000 == 0 or step == args.steps:
            torch.save({
                "model": model.state_dict(),
                "config": {"channels": args.channels, "emb_dim": 256, "encoder": "ge2e-resemblyzer-0.1.4", "n_fft": N_FFT, "hop": HOP},
                "data_licences": licences,
                "usage": "NON-COMMERCIAL RESEARCH ONLY" if non_commercial else "commercial use subject to the listed data licences",
                "smoke_test": args.smoke,
                "step": step,
            }, args.out / f"pgtcrn-c{args.channels}-step{step}.tar")
    summary = {"steps": args.steps, "first_loss": history[0], "last10_mean_loss": float(np.mean(history[-10:])),
               "licences": licences, "smoke": args.smoke}
    (args.out / "train-summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))
    if args.smoke:
        print("SMOKE TEST ONLY: synthetic TTS clips, 20 steps. The checkpoint does not extract speech and must not be registered.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
