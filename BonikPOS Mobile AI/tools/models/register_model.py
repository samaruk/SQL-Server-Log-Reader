#!/usr/bin/env python3
"""Add or update an entry in a BonikPOS model manifest (models/models.json) with SHA-256 checksums.

The app refuses to load a file whose SHA-256 differs from the manifest, so register every model you install.

Examples
  # Whisper export (files written by export_whisper_onnx.py)
  python register_model.py --manifest ../../models/models.json --id whisper-small --version openai-v20231117 \
      --role SpeechRecognition --format bonikpos-whisper-onnx/1 --license MIT \
      --source "OpenAI Whisper small exported with export_whisper_onnx.py" \
      --file config=asr/whisper-small-config.json --file encoder=asr/whisper-small-encoder.onnx \
      --file decoder=asr/whisper-small-decoder.onnx --file tokens=asr/whisper-small-tokens.txt

  # A streaming target-speaker extraction model (see docs/MODELS.md for the option keys)
  python register_model.py --manifest ../../models/models.json --id my-tse --version 1 \
      --role TargetSpeakerExtraction --format streaming-tse-onnx/1 --license "<verified licence>" \
      --source "<where it came from>" --file model=tse/my-tse.onnx \
      --options '{"chunk_samples": 320, "embedding_dim": 256, "required_encoder_id": "ge2e-resemblyzer",
                  "states": [{"input": "h_in", "output": "h_out"}]}'
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import sys

ROLES = ["VoiceActivity", "SpeakerEncoder", "TargetSpeakerExtraction", "SpeechRecognition"]


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def register(manifest: pathlib.Path, entry: dict) -> None:
    root = manifest.parent
    data = json.loads(manifest.read_text(encoding="utf-8")) if manifest.exists() else {"format": "bonikpos-models/1", "models": []}
    if data.get("format") != "bonikpos-models/1":
        raise SystemExit(f"{manifest} is not a bonikpos-models/1 manifest")
    entry["sha256"] = {}
    for logical, rel in entry["files"].items():
        path = root / rel
        if not path.is_file():
            raise SystemExit(f"File for '{logical}' not found: {path}")
        entry["sha256"][rel] = sha256(path)
    models = [m for m in data["models"] if m["id"] != entry["id"]]
    models.append(entry)
    order = {r: i for i, r in enumerate(ROLES)}
    models.sort(key=lambda m: (order.get(m["role"], 99), m["id"]))
    data["models"] = models
    manifest.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", required=True, type=pathlib.Path)
    ap.add_argument("--id", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--role", required=True, choices=ROLES)
    ap.add_argument("--format", required=True)
    ap.add_argument("--license", required=True, help="the licence you verified for the weights")
    ap.add_argument("--source", required=True)
    ap.add_argument("--file", action="append", required=True, help="logical=relative/path (relative to the manifest)")
    ap.add_argument("--options", default=None, help="adapter options as JSON")
    ap.add_argument("--test-fixture", action="store_true", help="mark as a test fixture (never loaded by the app)")
    args = ap.parse_args()
    files = dict(f.split("=", 1) for f in args.file)
    entry = {"id": args.id, "version": args.version, "role": args.role, "format": args.format, "files": files,
             "license": args.license, "source": args.source}
    if args.test_fixture:
        entry["isTestFixture"] = True
    if args.options:
        entry["options"] = json.loads(args.options)
    register(args.manifest, entry)
    print(f"Registered {args.id} in {args.manifest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
