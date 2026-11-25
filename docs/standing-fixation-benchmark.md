# Desktop stereo splat benchmark with standing fixation

Unity 6000.3.18f1, Vulkan, RTX 4090, 1,157,708 degree-3 splats from a local trained PLY. Each eye renders at 1024 × 1024. The two eye positions are 64 mm apart, with parallel views and a square perspective projection. The reference is sorted blending with exact depth-axis invalidation and 1× sampling. All paths update both projections every frame. Stochastic uses 4× MSAA with temporal filtering disabled.

## Motion

The quiet segment replays the six curves in Figure 2 of [Aytekin and Rucci (2012)](https://aplab.bcs.rochester.edu/assets/download/PDFs/articles/AytekinRucci12.pdf). The authors recorded a standing observer looking at a target one metre away. The CSV reconstructs approximately 0.15–12.15 seconds of the plotted trial. The six vector paths were interpolated along the horizontal plot axis, calibrated from the axis labels, zeroed at the first pose, and sampled at 30 Hz. The benchmark interpolates these digitized positions and angles to 90 Hz. Plot calibration, choice of orientation convention and treating the measured head displacement as a head-centre displacement make this an approximate replay. The curves describe one observer and smoothing in the figure limits recoverable high-frequency motion.

The 12-second replay spans about 37 mm fore/aft, 8 mm laterally, 2 mm vertically and 4.7° of pitch. The separate two-second synthetic glance adds a 30° yaw out and back to motion from the same trace. Clips sample both eyes at 45 fps from seconds 5–7 of the quiet segment and from the entire glance. Each panel shows the central 512 × 512 pixels at native sampling density.

## Measurements

Each row gives a stereo pair. Wall time includes command recording, submission, blits and waiting for a one-pixel GPU readback. Sorted uses two URP camera renders. Stochastic directly submits the package's stereo preparation and instanced draw into a two-layer multisampled render texture. This exercises the actual shaders and shared stereo data on the desktop, while the two paths incur different pipeline overhead. GPU stage times come from Unity's GPU recorders. Medians exclude five poses at each phase boundary for delayed recorder results. GPU sort medians can be zero when most frames reuse a previous order.

| Phase | Path | Median wall ms | p95 wall ms | GPU prepare ms | GPU sort ms | GPU draw ms | Sort dispatches |
|---|---|---:|---:|---:|---:|---:|---:|
| Quiet, 1080 poses | Sorted 0° | 3.191 | 3.469 | 0.253 | 0.134 | 1.577 | 1079 |
| Quiet | Sorted 1° | 3.024 | 3.356 | 0.236 | 0 | 1.576 | 5 |
| Quiet | Stochastic 4× | 1.922 | 1.974 | 0.153 | 0 | 1.511 | 0 |
| Glance, 180 poses | Sorted 0° | 3.207 | 3.448 | 0.253 | 0.134 | 1.584 | 179 |
| Glance | Sorted 1° | 3.040 | 3.363 | 0.236 | 0 | 1.584 | 47 |
| Glance | Stochastic 4× | 1.916 | 1.971 | 0.153 | 0 | 1.509 | 0 |

Stability was measured before video compression, across both eyes. The table uses mean absolute RGB channel differences on a 0–255 scale. Only pixels whose sorted reference has at least one channel above 8 enter the image comparison. The temporal measurement uses pixels passing that test in consecutive frames and measures the change in each method's error relative to the moving sorted reference. This subtracts ordinary reference-image motion and still includes differences due to coverage, compositing and sampling. It is an image metric rather than a headset comfort measurement.

| Phase | Path | Image difference | Frame-to-frame change in difference |
|---|---|---:|---:|
| Quiet | Sorted 1° | 0.031 | 0.041 |
| Quiet | Stochastic 4× | 20.544 | 23.104 |
| Glance | Sorted 1° | 0.029 | 0.055 |
| Glance | Stochastic 4× | 20.646 | 27.094 |

## Raster experiment

A trial shrank the stochastic bounding quad using each splat's opacity and the existing Gaussian alpha discard threshold. It retained a one-pixel margin at multisampled edges. Quiet median GPU draw time went from 1.511 to 1.506 ms and stereo wall time went from 1.922 to 1.930 ms. Glance GPU draw went from 1.509 to 1.507 ms. These single-run differences do not justify the added shader work. The production shader retains the existing bounds.

## Sorted single-pass follow-up

The new direct sorted draw shares a depth order between the two parallel eyes. The following measurements come from one four-way rerun on the same RTX 4090 capture. Both direct modes use identical two-layer 4× MSAA targets and the same submission harness. The legacy sorted path uses two full URP camera renders at 1×.

| Path | Quiet median wall ms | Glance median wall ms | Quiet GPU draw ms | Quiet sorts / 1080 | Quiet RGB error | Quiet temporal error |
|---|---:|---:|---:|---:|---:|---:|
| Legacy sorted, exact | 3.288 | 3.389 | 1.577 | 1079 | reference | reference |
| Legacy sorted, 1° | 3.098 | 3.203 | 1.577 | 5 | 0.031 | 0.041 |
| Direct sorted, 1°, 4× | 2.606 | 2.665 | 2.173 | 5 | 11.960 | 2.420 |
| Direct stochastic, 4× | 1.908 | 1.940 | 1.503 | 0 | 20.544 | 23.101 |

During the glance both 1° modes sorted 47 times in 180 poses. Direct sorted temporal error rose to 8.426, compared with 27.116 for stochastic. The direct sorted output was brighter than the reference in the first left-eye fixation crop. Direct rendering changes the color blending space and target precision. These captures do not isolate the contribution of each difference.

A separate run through the URP camera and Render Graph measured 2.19 ms for mono sorted 4× and 1.75 ms for mono sorted 1×. That check exercised actual camera pass selection. The two-layer replay exercised instanced drawing with synthetic eye matrices. Headset timing, OpenXR multiview, and canted-eye quality still need device validation.

## Reproduce

1. Launch the URP sample with `-batchmode -force-vulkan -gsFixationStereo -gsBenchmarkSource "<absolute capture path>" -executeMethod GaussianDirectValidation.Run`. The batch Editor exits on completion. Allow approximately 1.6 GB for raw eye clips.
2. Run `python tools/analyze-fixation-stereo.py projects/GaussianExample-URP/Library/GSFixationStereo projects/GaussianExample-URP/Library/GSFixationResults` from the repository root with NumPy, Pillow and FFmpeg installed.
3. Inspect `fixation.mp4`, `glance.mp4` and `measurements.json` in that results directory. Frame CSVs alongside the raw captures include the actual sort count, GPU stage times and GPU draw sample count.
