## How does Gaussian Splatting integrate with the rest of rendering?

### Which Unity render pipelines are supported?

"All of them" is a simple answer. URP and HDRP require Unity 6 or later. More details:
- I am mostly developing and testing with the built-in render pipeline (BiRP). This one does not need any extra setup;
  just have `GaussianSplatRenderer` components.
- URP: add `GaussianSplatURPFeature` to the URP renderer settings.
  - Render Graph "Compatibility Mode" in URP settings must be turned off!
  - For single-pass XR, select **Stochastic** transparency, **None** for Temporal Filter, and **Splats** for Render Mode on the Gaussian Splat Render Options component. The URP sample exposes these settings on the GaussianSplats object.
  - This configuration draws directly into the camera's color and depth targets. The URP asset controls MSAA. With multisampling enabled, each splat writes a stochastic coverage mask.
  - With **Sorted Blended**, the renderer keeps the current depth order while a static scene is viewed with a translating camera. **Sort Angle Degrees** sets how far the viewing direction can turn before a new sort. The default of zero re-sorts for any change of depth axis. A positive value can reduce sorting during small head rotations at the cost of possible blending changes where splats overlap. The projected splats still follow the current eye pose on every render.
  - Debug Points can also draw directly into stereo and MSAA camera targets.
  - Use OpenXR Multi-Pass with MSAA disabled for sorted blending and temporal reconstruction.
- HDRP: add CustomPass volume object and a `GaussianSplatHDRPPass` entry to it. It can be setup to render before transparencies,
  or after postprocess. Doing it after postprocess often produces better results, since before transparencies does not play well
  with HDRP auto-exposure thingamabobs.

Note that the project requires DX12 or Vulkan on Windows, i.e. **DX11 will not work**. Go to player settings graphics APIs
section and change Windows to use DX12.

### How do gaussians interact with regular rendering?

GaussianSplatRenderer objects are rendered after all opaque objects and skybox is rendered, and are tested against Z buffer.
This means you _can_ have opaque objects inside the "gaussian scene", and the splats will be occluded properly.

However gaussians will not interact quite properly with regular semitransparent 3D objects in the scene.

### Are gaussians affected by lighting?

No. No lights, shadows, reflection probes, lightmaps, skybox, any of that stuff.

### Known issues where things do not work

- MSAA support is limited to the URP direct stochastic path described above.
