# Stochastic rendering: single-pass XR and MSAA

Implementation status, 2026-11-25. The URP sample now selects the direct stochastic path with temporal filtering disabled. The renderer prepares both eyes in a single compute dispatch per object and draws the visible splats in one raster submission to camera color and depth. Debug Points also supports layered rendering. The optional single-pass temporal reconstruction work is pending the headset performance and quality decision in section 11.

## Desktop prototype results

Unity 6000.3.18f1, URP 17.3.0, RTX 4090. A simulated two-eye procedural draw on both desktop backends wrote a visible splat to both array slices. Changing the right view moved its center about 39 pixels relative to the left at a width of 256 pixels. Unity compiled the direct vertex and fragment variants for desktop single-pass instancing and Android Vulkan multiview. Offscreen camera renders at 1×, 2× and 4× showed intermediate sample coverage at 2× and 4×. An opaque foreground mesh blocked the splat at every sample count tested.

The reproducible benchmark generates 250,000 SH0 splats, renders a stationary 1024 × 1024 mono camera, and measures elapsed time for Camera.Render, a blit and synchronized one-pixel readback. Each configuration warms up for three seconds and records 120 frames. The two numbers in each cell are separate runs, in milliseconds. These measurements include CPU and synchronization cost.

| Desktop backend and statistic | Sorted 1× | Stochastic 1× | Stochastic 4× |
|---|---:|---:|---:|
| Vulkan median | 1.14 / 1.16 | 0.90 / 0.88 | 1.07 / 1.15 |
| Vulkan p95 | 1.25 / 1.29 | 1.00 / 0.99 | 1.18 / 1.21 |
| DX12 median | 1.50 / 1.68 | 1.31 / 1.52 | 1.74 / 1.75 |
| DX12 p95 | 1.67 / 1.84 | 1.47 / 1.74 | 1.89 / 1.92 |

Mean absolute RGB difference from the sorted 1× reference was about 35.7 / 255 for stochastic 1× and 13.6 / 255 at 4×. This measures single-frame image difference, including sampling noise and differences in compositing. Stereo comfort, head-motion quality, mobile GPU timings, peak device memory and the performance advantage at comparable quality still require headset measurements.

The installed Meta XR Simulator runtime advertised Vulkan and D3D12 through OpenXR. The sample's temporary OpenXR loader dependency failed to resolve in Unity Package Manager with `The "path" argument must be of type string. Received undefined.` The simulator session and end-to-end XR camera test therefore remain unverified. The sample's original package configuration was restored and the Vulkan benchmark ran with that configuration.

The current benchmark exercises simultaneous standing sway and three-axis head rotation, followed by a 30-degree glance and return and logs separate medians under `[GSMotionBench]`. The table above records the earlier stationary-camera version. Add `-gsWideSplats` to increase overlap or `-gsBenchmarkSource "<absolute file path>"` to run a compatible local capture. To run the desktop benchmark, open the URP sample with Unity CLI and pass `-batchmode -executeMethod GaussianDirectValidation.Run` and either `-force-vulkan` or `-force-d3d12` in `--args`. The benchmark exits on completion, logs median and p95 under `[GSDirectBench]`, and saves the three comparison frames under `Library/gs-benchmark-*.png` in the sample project. Running the sample in OpenXR also requires an enabled OpenXR loader for the build target.

## Objective

Support genuine single-pass stereo and MSAA in the URP stochastic renderer while reducing total frame time at acceptable visual quality. Removing sorting is useful only if the replacement costs less after rasterization, resolves, and any temporal filtering.

Build a minimal direct-rendering path first, measure it on the target headset, and add temporal reconstruction only where its quality benefit justifies its cost.

## 1. Supported configuration and acceptance criteria

Target URP Render Graph with OpenXR, supporting Single Pass Instanced on desktop and the corresponding multiview path on Vulkan devices. Verify these separately: desktop success does not establish Android multiview support.

Support 1Ã— samples (MSAA disabled), 2Ã— and 4Ã— initially; include 8Ã— where the device supports it. Confirm the target headset, graphics API, refresh rate, render resolution and foveation settings before benchmarking.

Completion requires:

1. Correct images and depth interaction in both eyes.
2. One stereo splat raster submission, without falling back to rendering each eye separately.
3. No sorting dispatches or meaningful sort-buffer allocations in stochastic mode.
4. MSAA that improves splat coverage alongside normal mesh antialiasing.
5. Lower measured GPU cost than sorted rendering at comparable quality in representative workloads.

A slower result fails the performance target even if it renders correctly. Establish numerical frame-time and visual-quality targets from the baseline and intended headset workload rather than inventing a universal speedup threshold.

## 2. Establish a measurable baseline

Use representative scenes spanning low and high splat counts, substantial overlapping splats, and mixed mesh/splat content. Include stationary views, continuous head movement and close inspection.

| Measurement | Purpose |
|---|---|
| View preparation GPU time | Cost of projecting and shading splats |
| Sorting GPU time | Work stochastic rendering removes |
| Splat raster GPU time | Overdraw and coverage cost |
| Resolve, reconstruction and composite GPU time | Replacement overhead |
| Total GPU frame time and CPU submission time | Actual application benefit |
| GPU allocations and peak memory | Stereo/MSAA affordability |
| Noise, ghosting and edge quality | Whether timings compare useful output |

Keep headset resolution, scene, graphics API, refresh rate, MSAA and foveation fixed across each comparison. Report absolute milliseconds and median/high-percentile frame times. Use repeatable camera motion and sustained runs sufficient to identify thermal or clock variation on standalone devices.

The current sorted path also lacks single-pass support. Until there is a correct sorted single-pass comparator, label comparisons against sorted multipass explicitly: they mix stereo submission improvements with the benefit of eliminating sorting. Isolate sorting savings from other changes in profiling results.

## 3. Replace the current per-eye URP assumptions

Primary file: `package/Runtime/GaussianSplatURPFeature.cs`.

The current integration forces scratch textures to `Tex2D`, selects one target slice and supplies one eye's matrices.

1. Obtain the active XR view count, matrices, viewport and texture layout from URP.
2. Preserve the camera's array layout and sample count where attachments require them.
3. Bind stereo attachments for layered rendering.
4. Let URP manage XR rendering state rather than manually emulating two eye passes.
5. Use declared Render Graph raster attachments and resource dependencies where possible, replacing the current broadly permissive unsafe pass.
6. Handle projection orientation, reversed depth, viewport size and render scaling using the installed URP APIs and actual attachment conventions.

Check procedural instance multiplication carefully. The installed XR implementation already applies an instance multiplier for instanced stereo; doubling the draw count again could produce incorrect indexing or duplicate work.

Follow the installed URP version when implementing the integration. References:

1. [Unity single-pass instanced rendering and custom shaders](https://docs.unity.com/en-us/engine/6000.0/manual/xr/graphics/stereo-rendering/single-pass-instancing).
2. [Unity Render Graph render-pass workflow](https://docs.unity.com/en-us/engine/6000.0/manual/render-pipelines/universal-render-pipeline/customizing-urp/render-graph/write-render-pass).

## 4. Prepare stereo splat data efficiently

Primary files: `package/Runtime/GaussianSplatRenderer.cs` and `package/Shaders/Resources/GaussianSplatUtilities.compute`.

The renderer currently produces one 28-byte view record per splat for one view. Start with a combined stereo preparation kernel:

1. Load splat geometry and calculate view-independent covariance once.
2. Project it separately for each eye, including each eye's ellipse and visibility.
3. Load shared color and spherical-harmonic data once where practical.
4. Evaluate eye-dependent shading correctly.
5. Write view records into eye-indexed regions.

Two 28-byte records require 56 bytes per splat for stereo view data, excluding source data. One million splats therefore require approximately 53.4 MiB for these records. Include this explicitly in the memory budget, buffer-size checks and allocation lifecycle.

Preserve splats visible in either eye. Do not use left-eye culling for both eyes.

Shared center-eye shading, compressed stereo records and vertex-stage projection are later experiments driven by measured bottlenecks. Avoid complicating the first correct implementation with them. Verify non-XR and existing multipass rendering while changing shared buffer management.

## 5. Make the splat draw genuinely stereo

Primary file: `package/Shaders/Resources/GaussianSplats.shader`.

1. Initialize Unity's instance and stereo state.
2. Select the correct eye's view record.
3. Route output to that eye's texture slice.
4. Separate the logical splat index from the raw hardware instance ID. The existing shader uses `SV_InstanceID` directly as the splat index.
5. Keep stochastic rendering independent of `_OrderBuffer` and the sorter.
6. Apply equivalent eye handling to debug rendering so it remains useful for stereo diagnosis.

Verify near objects, asymmetric eye projections and splats crossing one eye's frustum boundary before adding MSAA.

## 6. Build the fastest path: direct stochastic rendering

With temporal filtering disabled, render accepted stochastic samples directly into URP's active color/depth attachments at the existing point before ordinary transparencies.

This path avoids:

1. A separate splat color target.
2. A reconstruction depth target.
3. Temporal history textures.
4. A fullscreen splat composite.
5. A separate splat-color resolve.

Use depth writes and replacement color for accepted samples. Rejected samples leave the existing scene untouched. Preserve opaque mesh occlusion and verify subsequent transparent rendering; document any remaining transparency limitations.

Audit color conversion. The current implementation converts color in the composite shader, so removing that pass requires moving the appropriate conversion into the direct path. Check direct and filtered output against reference images, including the difference between filtering encoded color and filtering linear radiance. Do not assume those operations produce identical averages.

This path establishes whether stochastic rendering has a useful performance floor on the target hardware. Attempt to keep camera attachments in URP's existing render-pass flow and inspect actual attachment loads/stores rather than assuming tile-memory savings.

## 7. Implement efficient stochastic MSAA coverage

Matching attachment sample counts fixes compatibility, but the existing whole-pixel random discard gives all covered samples the same acceptance decision. That leaves much of MSAA's potential noise reduction unused.

Prototype a pixel-frequency shader that generates a stochastic sample mask:

1. Evaluate Gaussian alpha once per pixel.
2. Generate acceptance decisions for the available MSAA samples.
3. Include splat identity, pixel position, sample identity and frame seed in the sampling design.
4. Output the mask so hardware combines it with geometric coverage and depth/stencil testing.
5. Write opaque color/depth only to accepted samples.

Evaluate output `SV_Coverage` support on both required backends. Keep expected sample coverage equal to alpha. Avoid correlated masks across overlapping splats that would bias transparency. Evaluate inter-eye noise correlation for binocular comfort, and use a coherent frame seed for both eyes rather than advancing the sequence independently for each eye.

Do not make sample-frequency fragment shading the default. Reading `SV_SampleIndex` forces sample-frequency invocation and can multiply fragment work. See [Microsoft's HLSL interpolation reference](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-struct) and [system-value semantics reference](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics).

Measure whether shader-generated coverage harms early depth rejection or foveated shading on the target GPU. Compare practical coverage alternatives only if backend support, quality or profiling warrants it. Do not silently substitute a different transparency estimator and call it equivalent.

## 8. Treat temporal filtering as a measured quality option

Primary files: `package/Runtime/GaussianSplatTemporalAccumulator.cs` and `package/Shaders/Resources/GaussianComposite.shader`.

MSAA must work with filtering disabled. For filtering, support stereo texture arrays and per-eye matrices/history validity. Keep histories single-sampled; do not multiply persistent history memory by the MSAA sample count.

The filtered path needs:

1. Matching multisampled splat attachments during visibility rendering.
2. A deliberate color/coverage resolve before reconstruction.
3. A reconstruction depth policy that selects a meaningful surface depth rather than averaging unrelated depths.
4. Correct current-frame opaque occlusion when historical splat color is reused.
5. History invalidation on resolution, XR layout, projection, mode and relevant scene changes.
6. No cross-eye history sampling or incorrectly shared eye transforms.

Occlusion after reconstruction is a specific correctness risk: history must not paint splats over newly revealed foreground meshes. Prototype and validate this before committing to the final filtered attachment layout. Preserve the opaque visibility information needed by the selected design; a resolved color and one representative depth alone must not be assumed sufficient at mixed-coverage boundaries.

Measure added attachment storage, resolves and fullscreen bandwidth against the direct path. If filtering consumes the sorting savings, report that result and use the direct path where its quality is sufficient. Keep filtering selection explicit rather than silently overriding the user's setting.

## 9. Optimize the measured bottleneck

| Dominant cost | Next investigation |
|---|---|
| View preparation | Shared stereo geometry work, record bandwidth, SH evaluation |
| Fragment work | Quad bounds, overlap, coverage generation, depth rejection |
| Attachment bandwidth | Direct rendering, formats, render-pass merging and stores |
| Reconstruction | Resolution, history traffic, neighborhood sampling |
| CPU submission | Dispatch/draw count and per-frame allocations |

Do not add a depth prepass, extra sorting scheme, spatial filter or complicated caching without evidence that it reduces total cost. An unsorted stochastic renderer can still be expensive in heavily overlapping scenes.

## 10. Implementation sequence and validation

Deliver focused changes in this order:

1. Profiling baseline and reference captures.
2. Stereo preparation and single-pass drawing at 1Ã— samples.
3. Direct rendering and MSAA attachment compatibility.
4. Stochastic sample coverage and backend validation.
5. Optional stereo temporal reconstruction.
6. Sample setup and documentation.

The validation matrix covers:

1. Desktop and target-headset graphics APIs, with desktop instancing and device multiview verified separately.
2. Both eyes, including near objects and asymmetric visibility.
3. 1Ã—, 2Ã— and 4Ã— samples; 8Ã— where supported.
4. Filtering disabled, Simple Accumulation and Reprojected where implemented.
5. Opaque occluders, mixed-coverage mesh edges, later transparencies and background pixels.
6. Stationary views, sustained head rotation/translation, disocclusion and scene changes.
7. Resolution changes, mode changes, resource cleanup and absence of recurring per-frame allocations.
8. Non-XR and existing multipass regression checks.
9. Foveation compatibility and performance when it is part of the intended deployment.

Use narrow static/compile checks during development and focused GPU captures and headset checks for integration. Distinguish shader compilation, correct desktop output, correct headset output and measured performance in reports.

Keep implementation commits separate from temporary sample/environment changes. Preserve the sample project's existing modified settings. Do not commit temporary local dependency references. No push or pull request is implied by this plan.

## 11. Early decision gate and outlook

The first decision gate is direct single-pass stochastic rendering with 4Ã— MSAA on the target headset. Its measured speed and visual quality determine how much reconstruction the renderer can afford.

1. Compatibility confidence is high: stereo view preparation, layered drawing and matching MSAA attachments have no obvious architectural blocker in the inspected code.
2. Confidence in a useful performance win is moderate until measured. Efficient sample coverage, overdraw and mobile attachment bandwidth remain risks.
3. Head-motion quality is the largest uncertainty. Acceptable output with little or no reconstruction gives the strongest performance opportunity. Expensive reconstruction may consume the sorting savings.
4. High splat counts with expensive sorting offer more opportunity. Heavy screen coverage and overlap can remain raster-bound after sorting is removed.

Proceed through the early prototype before investing heavily in reconstruction. Compilation or desktop rendering alone does not establish the performance target.
