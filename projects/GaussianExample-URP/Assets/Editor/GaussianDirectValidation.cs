// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

// Run via -batchmode -executeMethod GaussianDirectValidation.Run.
// The normal editor update loop advances the package's asynchronous asset loader.
public static partial class GaussianDirectValidation
{
    const int SplatCount = 250000;
    const int Size = 1024;
    const int WarmupSeconds = 3;
    const int PoseRate = 90;
    const int MeasuredFrames = 4 * PoseRate;
    static GaussianSplatRenderer s_Renderer;
    static GaussianSplatRenderOptions s_Options;
    static Camera s_Camera;
    static DateTime s_Deadline;
    static Color32[] s_Reference;
    static bool s_UseCapture;
    static Vector3 s_CameraOrigin;

    public static void Run()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("Run this benchmark in a batch Editor.");
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            string source = Path.GetFullPath("Library/gs-direct-benchmark.ply");
            string[] args = Environment.GetCommandLineArgs();
            int pathIndex = Array.IndexOf(args, "-gsBenchmarkSource");
            s_UseCapture = pathIndex >= 0;
            if (s_UseCapture) source = Path.GetFullPath(args[pathIndex + 1]);
            else WriteFixture(source);
            s_Options = new GameObject("Render options").AddComponent<GaussianSplatRenderOptions>();
            s_Options.m_TemporalFilter = StochasticTemporalFilter.None;
            s_Renderer = new GameObject("Benchmark splats").AddComponent<GaussianSplatRenderer>();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gsWideSplats") >= 0)
                s_Renderer.m_SplatScale = 3;
            s_Renderer.m_SourcePath = source;
            s_Renderer.Reload();
            s_Camera = new GameObject("Benchmark camera").AddComponent<Camera>();
            s_Camera.enabled = false;
            s_Camera.transform.position = new Vector3(0, 0, -3);
            s_Camera.clearFlags = CameraClearFlags.SolidColor;
            s_Camera.backgroundColor = Color.black;
            s_Camera.allowMSAA = true;
            s_Deadline = DateTime.UtcNow.AddMinutes(1);
            EditorApplication.update += WhenLoaded;
        }
        catch (Exception e) { Fail(e); }
    }

    static void WhenLoaded()
    {
        if (s_Renderer.isLoading && DateTime.UtcNow < s_Deadline)
            return;
        EditorApplication.update -= WhenLoaded;
        try
        {
            if (s_Renderer.splatCount == 0 || (!s_UseCapture && s_Renderer.splatCount != SplatCount))
                throw new IOException($"Invalid benchmark load: {s_Renderer.splatCount} splats. {s_Renderer.loadError}");
            if (s_UseCapture)
            {
                Bounds bounds = s_Renderer.localBounds;
                float radius = Mathf.Max(bounds.extents.magnitude, .01f);
                s_Camera.transform.position = bounds.center - Vector3.forward * (2.2f * radius);
                s_Camera.farClipPlane = Mathf.Max(1000, radius * 5);
                s_Camera.nearClipPlane = Mathf.Min(.1f, radius * .01f);
            }
            s_CameraOrigin = s_Camera.transform.position;
            Debug.Log($"[GSMotionBench] splats={s_Renderer.splatCount} SH degree={s_Renderer.sourceSHDegree}");
            s_Run = (Array.IndexOf(Environment.GetCommandLineArgs(), "-gsFixationStereo") >= 0 ? FixationStereo() : Benchmark()).GetEnumerator();
            EditorApplication.update += Step;
        }
        catch (Exception e) { Fail(e); }
    }

    static IEnumerator<int> s_Run;

    static void Step()
    {
        try
        {
            if (s_Run.MoveNext()) return;
            EditorApplication.update -= Step;
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            EditorApplication.update -= Step;
            s_Run.Dispose();
            Fail(e);
        }
    }

    static IEnumerable<int> Benchmark()
    {
        var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
        int previousSamples = pipeline.msaaSampleCount;
        bool asyncCompilation = ShaderUtil.allowAsyncCompilation;
        try
        {
            ShaderUtil.allowAsyncCompilation = false;
            var settings = new[] {
                (TransparencyMode.SortedBlended, 1), (TransparencyMode.SortedBlended, 4), (TransparencyMode.Stochastic, 1), (TransparencyMode.Stochastic, 4),
                (TransparencyMode.Stochastic, 1), (TransparencyMode.Stochastic, 4), (TransparencyMode.SortedBlended, 1)
            };
            foreach (var (mode, samples) in settings)
                foreach (int frame in Measure(pipeline, mode, samples))
                    yield return frame;
            foreach (int frame in Measure(pipeline, TransparencyMode.SortedBlended, 1, forceSort: true))
                yield return frame;
            s_Options.m_SortAngleDegrees = 1;
            foreach (int frame in Measure(pipeline, TransparencyMode.SortedBlended, 1))
                yield return frame;
        }
        finally
        {
            pipeline.msaaSampleCount = previousSamples;
            ShaderUtil.allowAsyncCompilation = asyncCompilation;
        }
    }

    static void Fail(Exception e)
    {
        Debug.LogError($"[GSDirectBench] {e}");
        EditorApplication.Exit(1);
    }

    // A deterministic synthetic path in metres and degrees, sampled at 90 poses/s.
    // The second half adds a glance and return on top of the same standing sway.
    static void SetStandingPose(int sample)
    {
        float t = (sample % MeasuredFrames) / (float)PoseRate;
        float phase = 2 * Mathf.PI * t;
        float glance = t >= 2 ? Mathf.Pow(Mathf.Sin(Mathf.PI * (t - 2) / 2), 4) : 0;
        var rotation = Quaternion.Euler(
            2 * Mathf.Sin(.75f * phase) + 6 * glance,
            3 * Mathf.Sin(.5f * phase) + 30 * glance,
            Mathf.Sin(1.25f * phase) - 2 * glance);
        var sway = new Vector3(.035f * Mathf.Sin(.5f * phase), .012f * Mathf.Sin(.75f * phase),
            .025f * Mathf.Sin(.25f * phase));
        var eyeFromNeck = new Vector3(0, .12f, .08f);
        s_Camera.transform.SetPositionAndRotation(s_CameraOrigin + sway + rotation * eyeFromNeck - eyeFromNeck, rotation);
    }

    static IEnumerable<int> Measure(UniversalRenderPipelineAsset pipeline, TransparencyMode mode, int samples, bool forceSort = false)
    {
        s_Options.m_Transparency = mode;
        pipeline.msaaSampleCount = samples;
        var target = new RenderTexture(Size, Size, 24) { antiAliasing = samples };
        var resolved = RenderTexture.GetTemporary(Size, Size);
        try
        {
            if (!target.Create() || target.antiAliasing != samples)
                throw new InvalidOperationException($"Cannot create {samples}x render target.");
            s_Camera.targetTexture = target;
            var times = new double[MeasuredFrames];
            var watch = new Stopwatch();
            var warmup = Stopwatch.StartNew();
            int frame = 0;
            int sampleIndex = 0;
            bool measuring = false;
            while (frame < MeasuredFrames)
            {
                if (!measuring && warmup.Elapsed.TotalSeconds >= WarmupSeconds)
                {
                    measuring = true;
                    sampleIndex = 0;
                }
                SetStandingPose(sampleIndex++);
                if (forceSort) s_Renderer.InvalidateTemporalHistory();
                watch.Restart();
                s_Camera.Render();
                Graphics.Blit(target, resolved);
                var pixel = AsyncGPUReadback.Request(resolved, 0, Size / 2, 1, Size / 2, 1, 0, 1);
                pixel.WaitForCompletion();
                if (pixel.hasError) throw new IOException("Camera readback failed.");
                if (measuring)
                    times[frame++] = watch.Elapsed.TotalMilliseconds;
                // Return to the player loop so Unity can retire completed GPU readback requests.
                yield return frame;
            }
            // Keep full-frame readback and image encoding outside the timing window.
            var read = AsyncGPUReadback.Request(resolved, 0, TextureFormat.RGBA32);
            read.WaitForCompletion();
            if (read.hasError) throw new IOException("Image capture failed.");
            var image = read.GetData<Color32>().ToArray();
            var png = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            try
            {
                png.SetPixels32(image);
                png.Apply();
                var suffix = forceSort ? "-forced" : s_Options.m_SortAngleDegrees > 0 ? "-reuse" : "";
                File.WriteAllBytes($"Library/gs-benchmark-{mode}-{samples}x{suffix}.png", png.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(png); }

            if (mode == TransparencyMode.SortedBlended && samples == 1)
                s_Reference = image;
            else
            {
                double difference = 0;
                for (int i = 0; i < image.Length; i++)
                {
                    Color32 a = image[i], b = s_Reference[i];
                    difference += Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);
                }
                double mae = difference / (3 * image.Length);
                Debug.Log($"[GSDirectBench] {mode} {samples}x RGB mean absolute difference from sorted 1x = {mae:F2} / 255");
            }
            var sway = times[..(MeasuredFrames / 2)];
            var turning = times[(MeasuredFrames / 2)..];
            Array.Sort(sway);
            Array.Sort(turning);
            Debug.Log($"[GSMotionBench] {mode} {samples}x sortAngle={s_Options.m_SortAngleDegrees} forceSort={forceSort} sway={sway[sway.Length / 2]:F3} ms glance={turning[turning.Length / 2]:F3} ms");
            Array.Sort(times);
            double median = (times[(MeasuredFrames - 1) / 2] + times[MeasuredFrames / 2]) * .5;
            double p95 = times[(int)Math.Ceiling(MeasuredFrames * .95) - 1];
            Debug.Log($"[GSDirectBench] {SystemInfo.graphicsDeviceName} {SystemInfo.graphicsDeviceType} {mode} {samples}x median={median:F2} ms p95={p95:F2} ms");
        }
        finally
        {
            s_Camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(resolved);
            Object.DestroyImmediate(target);
        }
    }

    static void WriteFixture(string path)
    {
        string[] properties = "x y z scale_0 scale_1 scale_2 rot_1 rot_2 rot_3 rot_0 f_dc_0 f_dc_1 f_dc_2 opacity".Split(' ');
        var header = new StringBuilder($"ply\nformat binary_little_endian 1.0\nelement vertex {SplatCount}\n");
        foreach (string property in properties) header.Append($"property float {property}\n");
        header.Append("end_header\n");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes(header.ToString()));
        var random = new System.Random(719);
        float Signed() => (float)(random.NextDouble() * 2 - 1);
        for (int i = 0; i < SplatCount; ++i)
        {
            writer.Write(2 * Signed());
            writer.Write(1.5f * Signed());
            writer.Write(Signed());
            writer.Write(-5f);
            writer.Write(-5.5f);
            writer.Write(-5f);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(1f);
            writer.Write(Signed());
            writer.Write(Signed());
            writer.Write(Signed());
            writer.Write(1f);
        }
    }
}
