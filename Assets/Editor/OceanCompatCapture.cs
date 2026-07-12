using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Till Dawn Phase-2 checkpoints 1-2: renders the compat-backend ocean (and the original
// shader as control) from the parity camera pose. Never saves the scene.
public static class OceanCompatCapture {
    static readonly float[] Times = { 0f, 1f, 10f, 50f, 100f, 199.9f, 200f, 200.1f };
    static readonly string[] Views = { "band_0", "band_1", "band_2", "band_3", "displacement",
        "vertical_height", "horizontal_displacement", "slopes", "jacobian", "raw_foam",
        "final_foam", "specular", "fresnel_environment", "scatter", "final" };
    static readonly int[] ViewModes = { 8, 9, 10, 11, 2, 13, 12, 3, 15, 14, 4, 5, 6, 7, 0 };

    // Foam is a per-frame exponential accumulator (decay exp(-_FoamDecayRate) per dispatch,
    // not per second), so one large ParityStep leaves it at ~2% of its 60 fps steady state.
    // Rebuild steady state (~57-frame time constant) before any capture time that is not a
    // small continuation of the previous one.
    const int WarmupFrames = 300;
    const float WarmupDt = 1f / 60f;

    static readonly Vector3 ParityPosition = new Vector3(2.626f, 9.626f, -18.470f);
    static readonly Vector3 ParityEuler = new Vector3(10.546f, 5.873f, 0f);
    // Diagnostic-only pose looking toward the sun azimuth (~165 deg): the serialized sun
    // sits behind the approved parity camera, so the Beckmann glitter field is out of view
    // there. This extra view exists solely to prove the specular term; it is not a parity pose.
    static readonly Vector3 SunFacingEuler = new Vector3(10.546f, 165f, 0f);

    public static void Run() {
        try {
            RunInner();
            EditorApplication.Exit(0);
        } catch (Exception e) {
            Debug.LogError("OCEAN_COMPAT_CAPTURE_FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    static void RunInner() {
        string root = Environment.GetEnvironmentVariable("TILLDAWN_OCEAN_PARITY_ROOT");
        if (String.IsNullOrWhiteSpace(root)) root = Path.Combine(Application.dataPath, "../OceanParityArtifacts");
        root = Path.GetFullPath(root);

        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        FFTWater water = UnityEngine.Object.FindObjectOfType<FFTWater>();
        Camera camera = Camera.main;
        if (water == null || camera == null) throw new InvalidOperationException("SampleScene FFTWater/Main Camera missing");

        camera.transform.SetPositionAndRotation(ParityPosition, Quaternion.Euler(ParityEuler));
        camera.fieldOfView = 75f; camera.nearClipPlane = .05f; camera.farClipPlane = 1000f;

        // The sample contains buoyancy test meshes. One resolves to Unity's magenta error
        // material on this checkout. They are not part of the ocean reference, so exclude all
        // scene MeshRenderers except the FFTWater surface without saving the scene.
        MeshRenderer waterRenderer = water.GetComponent<MeshRenderer>();
        foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            if (renderer != waterRenderer) renderer.enabled = false;

        // The scene references an environment cubemap (guid 6fa3f446...) that was never
        // committed to the repo, so both backends otherwise sample an unbound texture.
        // Substitute a procedural dawn-sky cubemap built from the scene's own serialized
        // atmosphere values + Screenshot.png sky anchors. Runtime-only, never serialized,
        // and skipped automatically if the real asset ever appears.
        if (water.environmentTexture == null) {
            water.environmentTexture = BuildSubstituteEnvironment(water.atmosphere);
            Debug.Log("OCEAN_COMPAT_CAPTURE substitute environment cubemap bound (canonical asset missing from repo)");
        }
        if (water.atmosphere != null && water.atmosphere.skyboxTex == null)
            water.atmosphere.skyboxTex = water.environmentTexture;

        CaptureBackend(water, camera, root);
        WriteMetadata(root, camera, water);

        FFTWater.CompatBackendOverride = null;
        FFTWater.ParitySuppressAutomaticUpdate = false;
        FFTWater.ParityFrameTime = null; FFTWater.ParityDeltaTime = null;
        Debug.Log("OCEAN_COMPAT_CAPTURE_DONE");
    }

    static void CaptureBackend(FFTWater water, Camera camera, string root) {
        const string backend = "compat";
        FFTWater.CompatBackendOverride = backend;
        // Re-run OnEnable so mesh density and material pick up the backend. Skipping OnDisable
        // leaks the previous run's GPU resources, which is fine for a one-shot batch process.
        typeof(FFTWater).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(water, null);
        FFTWater.ParitySuppressAutomaticUpdate = true;

        Material material = water.GetComponent<MeshRenderer>().sharedMaterial;
        Debug.Log($"OCEAN_COMPAT_CAPTURE backend={backend} shader={material.shader.name} verts={water.GetComponent<MeshFilter>().sharedMesh.vertexCount}");

        bool loggedLighting = false;
        float previous = float.NegativeInfinity;
        foreach (float time in Times) {
            Advance(water, time, ref previous);
            if (!loggedLighting) { LogLightingBindings(water, material, backend); loggedLighting = true; }
            string token = "t" + time.ToString("000.0", CultureInfo.InvariantCulture);
            for (int i = 0; i < Views.Length; ++i) {
                material.SetInt("_CompatDebugMode", ViewModes[i]);
                string path = Path.Combine(root, "unity", Views[i], token + ".png");
                if (Views[i] == "final") CaptureWithAtmosphere(camera, water.atmosphere, path);
                else Capture(camera, path);
            }
            material.SetInt("_CompatDebugMode", 0);
        }
    }

    static void WriteMetadata(string root, Camera camera, FFTWater water) {
        string env = water.environmentTexture != null ? water.environmentTexture.name : "UNBOUND";
        string json = "{\n" +
            "  \"schema_version\": 1,\n  \"captures\": {\n    \"unity\": {\n" +
            $"      \"engine\": \"Unity {Application.unityVersion}\",\n" +
            $"      \"platform\": \"{Application.platform}\",\n" +
            $"      \"graphics_api\": \"{SystemInfo.graphicsDeviceType}\",\n" +
            $"      \"graphics_device\": \"{Escape(SystemInfo.graphicsDeviceName)}\",\n" +
            $"      \"color_space\": \"{QualitySettings.activeColorSpace}\",\n" +
            $"      \"camera_hdr\": {camera.allowHDR.ToString().ToLowerInvariant()},\n" +
            $"      \"camera_msaa\": {camera.allowMSAA.ToString().ToLowerInvariant()},\n" +
            "      \"resolution\": [2592, 1355],\n" +
            $"      \"camera_position\": [{Num(camera.transform.position.x)}, {Num(camera.transform.position.y)}, {Num(camera.transform.position.z)}],\n" +
            $"      \"camera_rotation_degrees\": [{Num(camera.transform.eulerAngles.x)}, {Num(camera.transform.eulerAngles.y)}, {Num(camera.transform.eulerAngles.z)}],\n" +
            $"      \"vertical_fov_degrees\": {Num(camera.fieldOfView)}, \"near\": {Num(camera.nearClipPlane)}, \"far\": {Num(camera.farClipPlane)},\n" +
            "      \"backend\": \"Custom/FFTWaterCompat vertex-fragment Linux compatibility path\",\n" +
            $"      \"environment_cubemap\": \"{Escape(env)}\",\n" +
            "      \"environment_deviation\": \"Original referenced cubemap is absent from the repository; runtime procedural TillDawnSubstituteEnvironment is the accepted substitute\",\n" +
            "      \"atmosphere_capture\": \"final view uses deterministic Atmosphere shader post-process; isolation views retain camera clear color\",\n" +
            "      \"non_ocean_meshes_hidden\": true,\n" +
            "      \"jacobian_view_semantics\": \"raw Jacobian retained by parity-only RHalf diagnostic texture before foam accumulation\"\n" +
            "    }\n  }\n}\n";
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "capture_metadata.json"), json);
    }

    static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Steps the simulation to `time`. Short forward gaps continue at the frozen 1/60 s
    // timestep; one dispatch per timestamp would under-advance the frame-based foam accumulator.
    // Larger gaps rebuild foam steady state with a 60 fps warm-up ending at `time`.
    static void Advance(FFTWater water, float time, ref float previous) {
        float gap = time - previous;
        if (gap > 0f && gap <= 5f) {
            float cursor = previous;
            while (cursor + WarmupDt < time - 0.00001f) {
                cursor += WarmupDt;
                water.ParityStep(cursor, WarmupDt);
            }
            float remainder = time - cursor;
            water.ParityStep(time, remainder > 0.00001f ? remainder : WarmupDt);
        } else {
            for (int i = WarmupFrames; i >= 1; --i) {
                float t = time - i * WarmupDt;
                if (t < 0f) t += 200f; // exact spectrum repeat time, verified in Stage 1
                water.ParityStep(t, WarmupDt);
            }
            water.ParityStep(time, WarmupDt);
        }
        previous = time;
    }

    // Stage 2 evidence: the lighting inputs the shader actually received this run.
    // The env map is read from the FFTWater field (what Update binds every frame):
    // material.GetTexture is unreliable here because _EnvironmentMap has no Properties entry.
    static void LogLightingBindings(FFTWater water, Material material, string backend) {
        Texture env = water.useTextureForFresnel ? water.environmentTexture : null;
        Debug.Log($"OCEAN_COMPAT_LIGHTING backend={backend}" +
            $" sunDir={material.GetVector("_SunDirection")}" +
            $" sunColor={material.GetVector("_SunColor")}" +
            $" sunIrradiance={material.GetVector("_SunIrradiance")}" +
            $" envMap={(env != null ? env.name + "/" + env.dimension : "UNBOUND")}" +
            $" useEnvMap={material.GetInt("_UseEnvironmentMap")}" +
            $" envStrength={material.GetFloat("_EnvironmentLightStrength")}" +
            $" scatter={material.GetVector("_ScatterColor")}" +
            $" foamColor={material.GetVector("_FoamColor")}");
    }

    // Procedural stand-in for the missing environment cubemap. Gradient anchors are the
    // Screenshot.png sky (sampled sRGB, stored linear); the sun glow uses the serialized
    // Atmosphere sunDirection/sunColor. Approximate by design: exact sky matching is Stage 3.
    static Cubemap BuildSubstituteEnvironment(Atmosphere atmosphere) {
        const int size = 128;
        Color zenith = new Color(0.552f, 0.578f, 0.615f);   // sRGB (0.77, 0.78, 0.81)
        Color horizon = new Color(0.642f, 0.585f, 0.475f);  // sRGB (0.82, 0.79, 0.72)
        Color sea = new Color(0.055f, 0.070f, 0.075f);
        Vector3 toSun = Vector3.up;
        Color sunColor = Color.white;
        if (atmosphere != null) {
            toSun = -atmosphere.GetSunDirection().normalized;
            sunColor = atmosphere.GetSunColor();
        }

        var cube = new Cubemap(size, TextureFormat.RGBAHalf, false);
        cube.name = "TillDawnSubstituteEnvironment";
        var faces = new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY,
                            CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ };
        var pixels = new Color[size * size];
        foreach (CubemapFace face in faces) {
            for (int y = 0; y < size; ++y) {
                for (int x = 0; x < size; ++x) {
                    Vector3 dir = CubeDirection(face, x, y, size).normalized;
                    Color c = dir.y >= 0f
                        ? Color.Lerp(horizon, zenith, Mathf.Pow(dir.y, 0.75f))
                        : Color.Lerp(horizon, sea, Mathf.Clamp01(-dir.y * 3f));
                    float d = Mathf.Clamp01(Vector3.Dot(dir, toSun));
                    // Soft overcast glow, no hard disc, matching the reference sky.
                    c += sunColor * (0.35f * Mathf.Pow(d, 40f) + 0.08f * Mathf.Pow(d, 8f));
                    pixels[y * size + x] = c;
                }
            }
            cube.SetPixels(pixels, face);
        }
        cube.Apply(false, true);
        return cube;
    }

    static Vector3 CubeDirection(CubemapFace face, int x, int y, int size) {
        float u = (x + 0.5f) / size * 2f - 1f;
        float v = (y + 0.5f) / size * 2f - 1f;
        switch (face) {
            case CubemapFace.PositiveX: return new Vector3(1f, -v, -u);
            case CubemapFace.NegativeX: return new Vector3(-1f, -v, u);
            case CubemapFace.PositiveY: return new Vector3(u, 1f, v);
            case CubemapFace.NegativeY: return new Vector3(u, -1f, -v);
            case CubemapFace.PositiveZ: return new Vector3(u, -v, 1f);
            default: return new Vector3(-u, -v, -1f);
        }
    }

    static void Capture(Camera camera, string path) {
        var rt = new RenderTexture(2592, 1355, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        camera.targetTexture = rt;
        camera.Render();
        camera.targetTexture = null;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        RenderTexture old = RenderTexture.active; RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = old;
        UnityEngine.Object.DestroyImmediate(tex);
        rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
    }

    // Reproduces Atmosphere.OnRenderImage without entering Play mode. The legacy component only
    // creates its private targets from OnEnable, so editor Camera.Render otherwise captures the
    // clear color. This path uses the same shader, serialized parameters, depth buffer, and cubemap.
    static void CaptureWithAtmosphere(Camera camera, Atmosphere atmosphere, string path) {
        if (atmosphere == null || atmosphere.atmosphereShader == null) {
            Capture(camera, path); return;
        }
        var color = new RenderTexture(2592, 1355, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var depth = new RenderTexture(2592, 1355, 32, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear);
        var output = new RenderTexture(2592, 1355, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        color.Create(); depth.Create(); output.Create();
        camera.SetTargetBuffers(color.colorBuffer, depth.depthBuffer);
        camera.Render();
        camera.targetTexture = null;

        var material = new Material(atmosphere.atmosphereShader);
        Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
        material.SetMatrix("_CameraInvViewProjection", (projection * camera.worldToCameraMatrix).inverse);
        material.SetTexture("_DepthTexture", depth);
        material.SetVector("_FogColor", atmosphere.fogColor);
        material.SetVector("_SunColor", atmosphere.sunColor);
        material.SetVector("_SunDirection", atmosphere.sunDirection);
        material.SetVector("_SkyboxDirection", atmosphere.skyboxDirection);
        material.SetFloat("_FogDensity", atmosphere.fogDensity);
        material.SetFloat("_FogOffset", atmosphere.fogOffset);
        material.SetFloat("_FogHeight", atmosphere.fogHeight);
        material.SetFloat("_FogAttenuation", atmosphere.fogAttenuation);
        material.SetFloat("_SkyboxSpeed", atmosphere.skyboxSpeed);
        material.SetTexture("_SkyboxTex", atmosphere.skyboxTex);
        Graphics.Blit(color, output, material);
        SaveRenderTexture(output, path);

        UnityEngine.Object.DestroyImmediate(material);
        color.Release(); depth.Release(); output.Release();
        UnityEngine.Object.DestroyImmediate(color); UnityEngine.Object.DestroyImmediate(depth); UnityEngine.Object.DestroyImmediate(output);
    }

    static void SaveRenderTexture(RenderTexture rt, string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        RenderTexture old = RenderTexture.active; RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(tex);
    }
}
