#!/usr/bin/env python3
"""Self-test for export_whisper_onnx.hf_to_openai().

Builds a small, randomly initialised Hugging Face WhisperForConditionalGeneration, converts it to the
openai-whisper module layout and checks that both produce the same logits. This proves the parameter
mapping used for Hugging Face fine-tunes (for example Bangla fine-tuned Whisper checkpoints) is exact.
It downloads nothing.
"""
from __future__ import annotations

import sys

import numpy as np
import torch
from transformers import WhisperConfig, WhisperForConditionalGeneration

import whisper.model as wm
from export_whisper_onnx import hf_to_openai


def main() -> int:
    wm.MultiHeadAttention.use_sdpa = False
    torch.manual_seed(0)
    config = WhisperConfig(
        vocab_size=51865, num_mel_bins=80, d_model=48, encoder_layers=2, decoder_layers=2,
        encoder_attention_heads=4, decoder_attention_heads=4, encoder_ffn_dim=192, decoder_ffn_dim=192,
        max_source_positions=150, max_target_positions=448, activation_function="gelu")
    hf = WhisperForConditionalGeneration(config).eval()
    with torch.no_grad():  # make LayerNorms non-trivial so a wrong mapping cannot hide
        for name, p in hf.named_parameters():
            if "layer_norm" in name:
                p.normal_(1.0 if name.endswith("weight") else 0.0, 0.1)
    ow = hf_to_openai(hf)

    mel = torch.randn(1, 80, 300)
    tokens = torch.tensor([[50258, 50302, 50359, 50363, 400, 1000, 2000, 51000]])
    with torch.no_grad():
        ref = hf(input_features=mel, decoder_input_ids=tokens).logits
        got = ow(mel, tokens)
    diff = float((ref - got).abs().max())
    scale = float(ref.abs().max())
    print(f"max |hf - converted| = {diff:.3e} (logit scale {scale:.3f})")
    if not np.isfinite(diff) or diff > 1e-3 * max(1.0, scale):
        print("CONVERSION SELF-TEST FAILED", file=sys.stderr)
        return 1
    print("CONVERSION SELF-TEST PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
