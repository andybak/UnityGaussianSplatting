## How does Gaussian Splatting integrate with the rest of rendering?

### Which Unity render pipelines are supported?

"All of them" is a simple answer. URP and HDRP require Unity 6 or later. More details:
- I am mostly developing and testing with the built-in render pipeline (BiRP). This one does not need any extra setup;
  just have `GaussianSplatRenderer` components.
- URP: add `GaussianSplatURPFeature` to the URP renderer settings.
  - Render Graph "Compatibility Mode" in URP settings must be turned off!
  - For stochastic single-pass XR, select **Stochastic** transparency, **None** for Temporal Filter, and **Splats** for Render Mode on the Gaussian Splat Rendering Options component. The URP sample exposes these settings on the GaussianRenderOptions object.
  - This configuration draws directly into the camera's color and depth targets. The URP asset controls MSAA. With multisampling enabled, each splat writes a stochastic coverage mask.
  - With **Sorted Blended**, the renderer keeps the current depth order while a static scene is viewed with a translating camera. **Sort Angle Degrees** sets how far the viewing direction can turn before a new sort. The default of zero re-sorts for any change of depth axis. A positive value can reduce sorting during small head rotations at the cost of possible blending changes where splats overlap. The projected splats still follow the current eye pose on every render.
  - Single-pass XR or MSAA selects direct back-to-front blending for Sorted Blended. It projects both eyes separately and uses left-eye depth order for both. Parallel eyes share the exact depth ordering at zero angular tolerance. Canted displays can show ordering differences. Direct blending converts individual splat colors before blending, which can change overlapping colors relative to the legacy composite.
  - Debug Points can also draw directly into stereo and MSAA camera targets.
  - Use OpenXR Multi-Pass with MSAA disabled for stochastic temporal reconstruction.
- HDRP: add CustomPass volume object and a `GaussianSplatHDRPPass` entry to it. It can be setup to render before transparencies,
  or after postprocess. Doing it after postprocess often produces better results, since before transparencies does not play well
  with HDRP auto-exposure thingamabobs.

Note that the project requires DX12 or Vulkan on Windows, i.e. **DX11 will not work**. Go to player settings graphics APIs
section and change Windows to use DX12.

### URP performance modes

Select the **GaussianRenderOptions** GameObject in the URP sample and open **Gaussian Splat Rendering Options** in the Inspector. Keep **Render Mode** on **Splats** for normal rendering. The sample enables **4× MSAA** in its active URP pipeline asset. In another project, choose the MSAA level in your own URP asset and select single-pass stereo in your XR provider settings.

| Settings | When to choose them |
|---|---|
| **Stochastic**, **Temporal Filter = None** (default) | Fastest measured stereo mode with 4× MSAA. Skips depth sorting and frame history. Grain can shimmer as the view moves. |
| **Sorted Blended**, **Sort Angle Degrees = 1** | Steadier stereo image. Updates each eye every frame and reuses the depth order through small rotations. Overlapping splats can occasionally blend in a different order. Supports single-pass and MSAA. |
| **Sorted Blended**, **Sort Angle Degrees = 0** | Prioritizes depth ordering. A change in view direction triggers a sort. Pure translation can reuse the order. Supports single-pass and MSAA. |
| **Stochastic**, **Simple Accumulation** or **Reprojected** | Uses previous frames to reduce grain. Reprojection accounts for camera motion and adds processing cost. These URP modes require MSAA off and, in XR, multipass. |

On an RTX 4090 with 1.16 million splats, median time per stereo pair during standing sway was **1.91 ms** for default stochastic 4× and **2.61 ms** for sorted 4× at 1°. Both used the same simulated single-pass setup. Stochastic used **26.8% less time**. Sorted reduced measured frame-to-frame image error by **89.5%**. Against legacy exact sorted rendering with two camera renders at 1× (3.29 ms), the default used **42.0% less time**. Headset timings are still unmeasured. [Benchmark methodology and results](standing-fixation-benchmark.md)

### How do gaussians interact with regular rendering?

GaussianSplatRenderer objects are rendered after all opaque objects and skybox is rendered, and are tested against Z buffer.
This means you _can_ have opaque objects inside the "gaussian scene", and the splats will be occluded properly.

However gaussians will not interact quite properly with regular semitransparent 3D objects in the scene.

### Are gaussians affected by lighting?

No. No lights, shadows, reflection probes, lightmaps, skybox, any of that stuff.

### Known issues where things do not work

- MSAA support is limited to the URP direct paths described above.
