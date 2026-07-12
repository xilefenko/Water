using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using System.Reflection;

// Till Dawn Phase-2 retained reference instrumentation. It never changes serialized scene values.
[InitializeOnLoad]
public static class OceanParityCapture {
    const string Armed = "TillDawn.OceanParity.Armed";
    static OceanParityCapture() {}

    [MenuItem("Tools/Till Dawn/Capture Ocean Parity")]
    public static void Run() {
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        FFTWater water = UnityEngine.Object.FindObjectOfType<FFTWater>();
        if (water == null) throw new InvalidOperationException("SampleScene FFTWater missing");
        typeof(FFTWater).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(water, null);
        var go = new GameObject("OceanParityDriver");
        go.AddComponent<OceanParityDriver>().CaptureNow();
        UnityEngine.Object.DestroyImmediate(go);
        typeof(FFTWater).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(water, null);
        EditorApplication.Exit(0);
    }

    static void Bootstrap() {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) {
            EditorApplication.delayCall += Bootstrap; return;
        }
        if (!EditorApplication.isPlaying) {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            SessionState.SetBool(Armed, true);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
            return;
        }
        if (SessionState.GetBool(Armed, false) && UnityEngine.Object.FindObjectOfType<OceanParityDriver>() == null) {
            new GameObject("OceanParityDriver").AddComponent<OceanParityDriver>();
        }
    }

    public sealed class OceanParityDriver : MonoBehaviour {
        static readonly float[] Times = { 0f, 1f, 10f, 50f, 100f, 199.9f, 200f, 200.1f };
        static readonly string[] Appearance = { "specular", "fresnel_environment", "scatter", "final" };
        string root;
        FFTWater water;
        Camera camera;

        public void CaptureNow() {
            root = Environment.GetEnvironmentVariable("TILLDAWN_OCEAN_PARITY_ROOT");
            if (String.IsNullOrWhiteSpace(root)) root = Path.Combine(Application.dataPath, "../OceanParityArtifacts");
            root = Path.GetFullPath(root);
            water = UnityEngine.Object.FindObjectOfType<FFTWater>(); camera = Camera.main;
            if (water == null || camera == null) throw new InvalidOperationException("SampleScene FFTWater/Main Camera missing");
            // Approved Godot pose converted to Unity: remove Till Dawn's (32,4,0) ocean datum,
            // retain +Z scene placement, and invert camera pitch for Unity's convention.
            camera.transform.SetPositionAndRotation(new Vector3(2.626f, 9.626f, -18.470f), Quaternion.Euler(10.546f, 5.873f, 0f));
            camera.fieldOfView = 75f; camera.nearClipPlane = .05f; camera.farClipPlane = 1000f;
            FFTWater.ParitySuppressAutomaticUpdate = true;
            float previous = 0f;
            foreach (float time in Times) {
                water.ParityStep(time, Mathf.Max(1f / 60f, time - previous)); previous = time;
                string token = "t" + time.ToString("000.0", System.Globalization.CultureInfo.InvariantCulture);
                CaptureCamera(token); CaptureFields(token); Debug.Log($"OCEAN_PARITY_CAPTURE time={time:0.0}");
            }
            WriteMetadata();
            FFTWater.ParitySuppressAutomaticUpdate = false; FFTWater.ParityFrameTime = null; FFTWater.ParityDeltaTime = null;
        }

        IEnumerator Start() {
            root = Environment.GetEnvironmentVariable("TILLDAWN_OCEAN_PARITY_ROOT");
            if (String.IsNullOrWhiteSpace(root)) root = Path.Combine(Application.dataPath, "../OceanParityArtifacts");
            root = Path.GetFullPath(root);
            water = UnityEngine.Object.FindObjectOfType<FFTWater>();
            camera = Camera.main;
            if (water == null || camera == null) throw new InvalidOperationException("SampleScene FFTWater/Main Camera missing");

            camera.transform.SetPositionAndRotation(new Vector3(2.626f, 9.626f, -18.470f), Quaternion.Euler(10.546f, 5.873f, 0f));
            camera.fieldOfView = 75f; camera.nearClipPlane = .05f; camera.farClipPlane = 1000f;
            FFTWater.ParitySuppressAutomaticUpdate = true;
            yield return null;
            float previous = 0f;
            foreach (float time in Times) {
                water.ParityStep(time, Mathf.Max(1f / 60f, time - previous));
                previous = time;
                yield return new WaitForEndOfFrame();
                string token = "t" + time.ToString("000.0", System.Globalization.CultureInfo.InvariantCulture);
                CaptureCamera(token);
                CaptureFields(token);
                Debug.Log($"OCEAN_PARITY_CAPTURE time={time:0.0}");
            }
            WriteMetadata();
            FFTWater.ParitySuppressAutomaticUpdate = false;
            FFTWater.ParityFrameTime = null; FFTWater.ParityDeltaTime = null;
            SessionState.SetBool(Armed, false);
            yield return null;
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        void CaptureCamera(string token) {
            var rt = new RenderTexture(2592, 1355, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = rt; camera.Render(); camera.targetTexture = null;
            foreach (string view in Appearance) Save2D(rt, Path.Combine(root, "unity", view, token + ".png"));
            rt.Release(); Destroy(rt);
        }

        void CaptureFields(string token) {
            RenderTexture displacement = water.GetDisplacementMap();
            RenderTexture slopes = water.GetSlopeMap();
            for (int layer = 0; layer < 4; layer++) SaveLayer(displacement, layer, Path.Combine(root, "unity", "band_" + layer, token + ".png"));
            foreach (string view in new[]{"displacement","vertical_height","horizontal_displacement","jacobian","raw_foam","final_foam"})
                SaveLayer(displacement, 0, Path.Combine(root, "unity", view, token + ".png"));
            SaveLayer(slopes, 0, Path.Combine(root, "unity", "slopes", token + ".png"));
        }

        static void SaveLayer(RenderTexture source, int layer, string path) {
            var rt = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.CopyTexture(source, layer, 0, rt, 0, 0);
            Save2D(rt, path); rt.Release(); Destroy(rt);
        }

        static void Save2D(RenderTexture source, string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            RenderTexture old = RenderTexture.active; RenderTexture.active = source;
            var tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0,0,source.width,source.height),0,0); tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG()); Destroy(tex); RenderTexture.active = old;
        }

        void WriteMetadata() {
            Directory.CreateDirectory(root);
            string json = "{\n  \"schema_version\": 1,\n  \"engine\": \"Unity " + Application.unityVersion +
                "\",\n  \"graphics_api\": \"" + SystemInfo.graphicsDeviceType + "\",\n  \"graphics_device\": \"" +
                SystemInfo.graphicsDeviceName.Replace("\"","'") + "\",\n  \"color_space\": \"" + QualitySettings.activeColorSpace +
                "\",\n  \"hdr\": " + camera.allowHDR.ToString().ToLowerInvariant() +
                ",\n  \"resolution\": [2592, 1355],\n  \"diagnostic_note\": \"Field PNGs retain packed source channels; appearance component isolation awaits shader-native debug branches.\"\n}\n";
            File.WriteAllText(Path.Combine(root, "unity_capture_metadata.json"), json);
        }
    }
}
