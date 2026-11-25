// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static partial class GaussianDirectValidation
{
    // Desktop simulation of the package's two-eye preparation and instanced draw.
    // Bind the package entry points once; the sample is outside its internal assembly.
    delegate void PrepareViews(Camera camera, CommandBuffer cmd, Matrix4x4 projection, Matrix4x4 view,
        int width, int height, Matrix4x4? rightProjection, Matrix4x4? rightView);

    static IEnumerable<int> FixationStereo()
    {
        s_Camera.aspect = 1; // Both the square eye targets and the layered target use the same projection.
        var poses = File.ReadLines("Assets/Editor/StandingFixationFigure2.csv").Skip(1)
            .Select(line => line.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        var type = typeof(GaussianSplatRenderer).Assembly.GetType("GaussianSplatting.Runtime.GaussianSplatRenderSystem");
        var system = type.GetProperty("instance").GetValue(null);
        const BindingFlags methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        T Bind<T>(string name) where T : Delegate => (T)type.GetMethod(name, methods).CreateDelegate(typeof(T), system);
        var gather = Bind<Func<Camera,int,bool>>("GatherSplatsForCamera");
        var prepare = Bind<PrepareViews>("PrepareDirectViews");
        var setup = Bind<Action<CommandBuffer,int,int,int>>("PrepareSplatDraw");
        var draw = Bind<Action<RasterCommandBuffer,int>>("DrawDirect");
        var sorts = Bind<Func<int>>("get_SortDispatchCount");
        var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
        int oldSamples = pipeline.msaaSampleCount;
        string output = Path.GetFullPath("Library/GSFixationStereo");
        Directory.CreateDirectory(output);
        var clock = new Stopwatch();
        string[] stages = {"GaussianSplat.CalcView", "GaussianSplat.Sort", "GaussianSplat.Draw", "GaussianSplat.Compose"};
        var recorders = stages.Select(Recorder.Get).ToArray();
        foreach (var recorder in recorders) recorder.enabled = true;
        try
        {
            foreach (string mode in new[]{"sorted0","sorted1","sortedStereo4","stochastic4"})
            {
                bool direct = mode == "stochastic4" || mode == "sortedStereo4";
                s_Options.m_Transparency = mode == "stochastic4" ? TransparencyMode.Stochastic : TransparencyMode.SortedBlended;
                s_Options.m_SortAngleDegrees = (mode == "sorted1" || mode == "sortedStereo4") ? 1 : 0;
                pipeline.msaaSampleCount = direct ? 4 : 1;
                var desc = new RenderTextureDescriptor(Size, Size, RenderTextureFormat.ARGB32, 24)
                {
                    dimension = direct ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                    volumeDepth = direct ? 2 : 1, msaaSamples = direct ? 4 : 1
                };
                var target = new RenderTexture(desc);
                if (!target.Create() || target.antiAliasing != desc.msaaSamples) throw new IOException("Unsupported benchmark target");
                var eyes = new[]{RenderTexture.GetTemporary(Size,Size),RenderTexture.GetTemporary(Size,Size)};
                using var cmd = new CommandBuffer();
                using var csv = new StreamWriter(Path.Combine(output, mode+".csv"));
                csv.WriteLine("frame,phase,stereo_ms,sorts,prepare_gpu_ns,sort_gpu_ns,draw_gpu_ns,compose_gpu_ns,draw_gpu_blocks");
                try
                {
                    foreach(bool glance in new[]{false,true})
                    {
                        string phase = glance ? "glance" : "fixation";
                        using var left = File.Create(Path.Combine(output,$"{mode}-{phase}-L.rgba"));
                        using var right = File.Create(Path.Combine(output,$"{mode}-{phase}-R.rgba"));
                        int frames = glance ? 180 : 1080;
                        for(int i=-30;i<frames;i++)
                        {
                            float t = Mathf.Max(i,0)/90f;
                            float pos = (glance ? 5+t : t)*30;
                            int k = Mathf.FloorToInt(pos);
                            float Axis(int col) => Mathf.Lerp(poses[k][col], poses[k+1][col], pos-k);
                            float yaw = glance ? 30*Mathf.Pow(Mathf.Sin(Mathf.PI*t/2),4) : 0;
                            var head = s_CameraOrigin + new Vector3(Axis(1),Axis(2),Axis(3));
                            var rot = Quaternion.Euler(Axis(4),Axis(5)+yaw,Axis(6));
                            void SetEye(int eye) => s_Camera.transform.SetPositionAndRotation(head+rot*Vector3.right*(eye==0?-.032f:.032f),rot);
                            int startSorts=sorts();
                            clock.Restart();
                            if(direct)
                            {
                                SetEye(1);
                                var viewR=s_Camera.worldToCameraMatrix;
                                SetEye(0);
                                var proj=GL.GetGPUProjectionMatrix(s_Camera.projectionMatrix,true);
                                if(!gather(s_Camera,2)) throw new IOException("No active splats");
                                cmd.Clear();
                                prepare(s_Camera,cmd,proj,s_Camera.worldToCameraMatrix,Size,Size,proj,viewR);
                                setup(cmd,Size,Size,4);
                                cmd.SetRenderTarget(target,0,CubemapFace.Unknown,-1);
                                cmd.ClearRenderTarget(true,true,Color.black);
                                cmd.SetViewport(new Rect(0,0,Size,Size));
                                cmd.EnableShaderKeyword("STEREO_INSTANCING_ON");
                                cmd.SetInstanceMultiplier(2);
                                draw(CommandBufferHelpers.GetRasterCommandBuffer(cmd),4);
                                cmd.SetInstanceMultiplier(1);
                                cmd.DisableShaderKeyword("STEREO_INSTANCING_ON");
                                Graphics.ExecuteCommandBuffer(cmd);
                                for(int eye=0;eye<2;eye++) Graphics.Blit(target,eyes[eye],eye,0);
                            }
                            else
                            {
                                s_Camera.targetTexture=target;
                                for(int eye=0;eye<2;eye++)
                                {
                                    SetEye(eye);
                                    s_Camera.Render();
                                    Graphics.Blit(target,eyes[eye]);
                                }
                            }
                            var fence=AsyncGPUReadback.Request(eyes[1],0,Size/2,1,Size/2,1,0,1);
                            fence.WaitForCompletion();
                            if(fence.hasError) throw new IOException("Stereo readback failed");
                            double ms=clock.Elapsed.TotalMilliseconds;
                            if(i>=0)
                            {
                                csv.WriteLine(FormattableString.Invariant($"{i},{phase},{ms:F5},{sorts()-startSorts},{recorders[0].gpuElapsedNanoseconds},{recorders[1].gpuElapsedNanoseconds},{recorders[2].gpuElapsedNanoseconds},{recorders[3].gpuElapsedNanoseconds},{recorders[2].gpuSampleBlockCount}"));
                                // Two seconds at 45 fps. Crop at native pixel size for visible grain.
                                if(i%2==0 && (glance || (i>=450 && i<630)))
                                    for(int eye=0;eye<2;eye++)
                                    {
                                        var read=AsyncGPUReadback.Request(eyes[eye],0,(Size-512)/2,512,(Size-512)/2,512,0,1,TextureFormat.RGBA32);
                                        read.WaitForCompletion();
                                        if(read.hasError) throw new IOException("Stereo capture failed");
                                        var bytes=read.GetData<byte>().ToArray();
                                        (eye==0?left:right).Write(bytes,0,bytes.Length);
                                    }
                            }
                            yield return i;
                        }
                        csv.Flush();
                        Debug.Log($"[GSFixationStereo] {mode} {phase} complete");
                    }
                }
                finally
                {
                    s_Camera.targetTexture=null;
                    foreach(var eye in eyes) RenderTexture.ReleaseTemporary(eye);
                    Object.DestroyImmediate(target);
                }
            }
        }
        finally
        {
            foreach(var recorder in recorders) recorder.enabled=false;
            pipeline.msaaSampleCount=oldSamples;
        }
    }
}
