"""Analyze the Unity fixation benchmark. Requires numpy, Pillow and ffmpeg on PATH."""
import argparse
import csv
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

MODES = ("sorted0", "sorted1", "stochastic4")
LABELS = ("Sorted 0 degrees", "Sorted 1 degree", "Stochastic 4x")
EYES = ("L", "R")
SIZE = 512


def timing(root, mode, phase):
    with (root / f"{mode}.csv").open() as f:
        rows = [r for r in csv.DictReader(f) if r["phase"] == phase]
    values = lambda key: np.array([float(r[key]) for r in rows])
    # GPU recorder results arrive after submission. Remove phase boundaries.
    gpu = lambda key: round(float(np.median(values(key)[5:-5])) / 1e6, 4)
    return dict(frames=len(rows), sorts=int(values("sorts").sum()),
                median_ms=round(float(np.median(values("stereo_ms"))), 4),
                p95_ms=round(float(np.percentile(values("stereo_ms"), 95)), 4),
                prepare_gpu_ms=gpu("prepare_gpu_ns"), sort_gpu_ms=gpu("sort_gpu_ns"),
                draw_gpu_ms=gpu("draw_gpu_ns"), compose_gpu_ms=gpu("compose_gpu_ns"))


def sequence(root, output, phase):
    streams = {(m, e): np.memmap(root / f"{m}-{phase}-{e}.rgba", dtype=np.uint8, mode="r")
               .reshape(-1, SIZE, SIZE, 4) for m in MODES for e in EYES}
    assert all(len(s) == 90 for s in streams.values())
    header = 28
    canvas = Image.new("RGB", (SIZE * 3, (SIZE + header) * 2))
    labels = ImageDraw.Draw(canvas)
    for col, label in enumerate(LABELS):
        for row, eye in enumerate(EYES):
            labels.text((col * SIZE + 8, row * (SIZE + header) + 6), f"{label} | {eye} | {phase}", fill="white")
    ffmpeg = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "rgb24",
                               "-video_size", f"{canvas.width}x{canvas.height}", "-framerate", "45",
                               "-i", "-", "-an", "-c:v", "libx264", "-crf", "12", "-pix_fmt", "yuv420p",
                               "-movflags", "+faststart", str(output / f"{phase}.mp4")], stdin=subprocess.PIPE)
    totals = {m: np.zeros(4) for m in MODES[1:]}
    previous = {}
    for i in range(90):
        for row, eye in enumerate(EYES):
            ref = streams["sorted0", eye][i, :, :, :3].astype(np.int16)
            mask = ref.max(axis=2) > 8
            for col, mode in enumerate(MODES):
                rgb = streams[mode, eye][i, :, :, :3]
                canvas.paste(Image.fromarray(rgb[::-1]), (col * SIZE, row * (SIZE + header) + header))
                if col:
                    residual = rgb.astype(np.int16) - ref
                    acc = totals[mode]
                    acc[0] += np.abs(residual[mask]).sum()
                    acc[1] += 3 * mask.sum()
                    if i:
                        old, old_mask = previous[mode, eye]
                        active = mask & old_mask
                        acc[2] += np.abs((residual-old)[active]).sum()
                        acc[3] += 3 * active.sum()
                    previous[mode, eye] = residual, mask
        ffmpeg.stdin.write(canvas.tobytes())
    ffmpeg.stdin.close()
    if ffmpeg.wait(): raise RuntimeError("ffmpeg failed")
    return {m: dict(rgb_mae=round(a/b, 3), temporal_residual_mae=round(c/d, 3)) for m,(a,b,c,d) in totals.items()}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("capture_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    report = {phase: dict(timing={m: timing(args.capture_dir, m, phase) for m in MODES},
                          image=sequence(args.capture_dir, args.output_dir, phase)) for phase in ("fixation", "glance")}
    (args.output_dir / "measurements.json").write_text(json.dumps(report, indent=2))
