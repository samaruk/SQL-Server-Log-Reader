#!/usr/bin/env python3
"""Generate SYNTHETIC test audio for automated tests (flite / espeak-ng text-to-speech).

These clips exist only to exercise the audio and model plumbing (VAD, speaker-embedding extraction,
mixing, resampling). They are NOT consented owner recordings and MUST NOT be used to claim
recognition or speaker-verification accuracy. See docs/EVALUATION_PLAN.md for real evaluation data.

Requires: flite (voices slt, rms, awb) and espeak-ng with the Bengali voice, numpy, scipy, soundfile.
  sudo apt-get install flite espeak-ng
Output: 16 kHz, mono, 16-bit PCM WAV files.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import tempfile

import numpy as np
import soundfile
from scipy.signal import resample_poly

SR = 16000

CLIPS = [
    # file, engine, voice, text
    ("synthetic-en-napa.wav", "flite", "slt", "Napa extra five hundred milligram tablet"),
    ("synthetic-en-seclo.wav", "flite", "slt", "Seclo twenty milligram capsule"),
    ("synthetic-voiceA-1.wav", "flite", "slt", "Please give me two strips of paracetamol"),
    ("synthetic-voiceA-2.wav", "flite", "slt", "I need omeprazole capsules for my father"),
    ("synthetic-voiceA-3.wav", "flite", "slt", "Do you have cough syrup for children"),
    ("synthetic-voiceB-1.wav", "flite", "rms", "Please give me two strips of paracetamol"),
    ("synthetic-voiceB-2.wav", "flite", "rms", "How much does this antibiotic cost today"),
    ("synthetic-voiceC-1.wav", "flite", "awb", "Is the pharmacy open tomorrow morning"),
    ("synthetic-bn-napa.wav", "espeak-ng", "bn", "নাপা এক্সট্রা পাঁচশো মিলিগ্রাম ট্যাবলেট"),
]


def synthesize(engine: str, voice: str, text: str) -> np.ndarray:
    with tempfile.TemporaryDirectory() as tmp:
        out = pathlib.Path(tmp) / "out.wav"
        if engine == "flite":
            subprocess.run(["flite", "-voice", voice, "-t", text, "-o", str(out)], check=True)
        else:
            subprocess.run(["espeak-ng", "-v", voice, "-w", str(out), text], check=True)
        audio, sr = soundfile.read(str(out), dtype="float32")
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    if sr != SR:
        g = np.gcd(sr, SR)
        audio = resample_poly(audio, SR // g, sr // g).astype(np.float32)
    peak = float(np.abs(audio).max()) or 1.0
    return (audio / peak * 0.5).astype(np.float32)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out-dir", required=True, type=pathlib.Path)
    args = ap.parse_args()
    args.out_dir.mkdir(parents=True, exist_ok=True)

    clips = {}
    manifest = {"notice": "SYNTHETIC TTS AUDIO FOR PLUMBING TESTS ONLY - not evaluation data", "clips": []}
    for name, engine, voice, text in CLIPS:
        audio = synthesize(engine, voice, text)
        clips[name] = audio
        soundfile.write(str(args.out_dir / name), audio, SR, subtype="PCM_16")
        manifest["clips"].append({"file": name, "engine": engine, "voice": voice, "text": text,
                                  "seconds": round(audio.size / SR, 3)})

    rng = np.random.default_rng(7)
    a, b = clips["synthetic-voiceA-2.wav"], clips["synthetic-voiceB-2.wav"]
    n = max(a.size, b.size)
    mix = np.zeros(n, np.float32)
    mix[: a.size] += a
    mix[: b.size] += b * 0.7  # second voice about 3 dB quieter
    mix = (mix / np.abs(mix).max() * 0.5).astype(np.float32)
    soundfile.write(str(args.out_dir / "synthetic-overlap-A2-B2.wav"), mix, SR, subtype="PCM_16")
    manifest["clips"].append({"file": "synthetic-overlap-A2-B2.wav", "engine": "mix",
                              "text": "voiceA-2 + 0.7 * voiceB-2 (simultaneous speech)",
                              "seconds": round(n / SR, 3)})

    noise = rng.standard_normal(SR * 2).astype(np.float32)
    noise = np.cumsum(noise) * 0.002  # brown-ish noise
    noise = (noise - noise.mean()) * 0.05 / (np.abs(noise).max() or 1.0)
    soundfile.write(str(args.out_dir / "noise-2s.wav"), noise.astype(np.float32), SR, subtype="PCM_16")
    soundfile.write(str(args.out_dir / "silence-1s.wav"), np.zeros(SR, np.float32), SR, subtype="PCM_16")
    manifest["clips"] += [{"file": "noise-2s.wav", "engine": "numpy", "text": "brown-ish noise"},
                          {"file": "silence-1s.wav", "engine": "numpy", "text": "digital silence"}]

    with open(args.out_dir / "README.json", "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)


if __name__ == "__main__":
    main()
