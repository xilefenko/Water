using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Till Dawn Phase-2 compute-simulation verification. Reads back every simulation field,
// checks finiteness/ranges/animation/periodicity, and writes stats JSON plus preview PNGs.
// Never changes serialized scene values.
public static class OceanFieldDiagnostics {
    static readonly float[] Times = { 0f, 1f, 10f, 50f, 100f, 199.9f, 200f, 200.1f };

    class LayerStats {
        public string texture; public int layer; public float time;
        public int nanCount, infCount;
        public double[] min = new double[4], max = new double[4], mean = new double[4], rms = new double[4];
        public double nonZeroFraction;
    }

    public static void Run() {
        try {
            RunInner();
            EditorApplication.Exit(0);
        } catch (Exception e) {
            Debug.LogError("OCEAN_FIELD_DIAGNOSTICS_FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    static void RunInner() {
        string root = Environment.GetEnvironmentVariable("TILLDAWN_OCEAN_PARITY_ROOT");
        if (String.IsNullOrWhiteSpace(root)) root = Path.Combine(Application.dataPath, "../OceanParityArtifacts");
        root = Path.GetFullPath(Path.Combine(root, "diagnostics"));
        Directory.CreateDirectory(root);

        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        FFTWater water = UnityEngine.Object.FindObjectOfType<FFTWater>();
        if (water == null) throw new InvalidOperationException("SampleScene FFTWater missing");
        typeof(FFTWater).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(water, null);

        var stats = new List<LayerStats>();
        var checks = new List<string>();
        // Height channel per band retained across times for animation/periodicity checks.
        var heightBaselines = new Dictionary<string, float[][]>();

        FFTWater.ParitySuppressAutomaticUpdate = true;

        // Initial spectrum (4 layers) is computed once in OnEnable; capture before stepping.
        RenderTexture initial = water.GetInitialSpectrum();
        for (int layer = 0; layer < 4; layer++) {
            float[] data = ReadLayer(initial, layer, TextureFormat.RGBAFloat);
            stats.Add(Analyze(data, "initial_spectrum", layer, -1f));
            SavePreview(data, initial.width, 0, Path.Combine(root, $"initial_spectrum_band{layer}.png"));
        }

        float previous = 0f;
        foreach (float time in Times) {
            water.ParityStep(time, Mathf.Max(1f / 60f, time - previous));
            previous = time;
            string token = "t" + time.ToString("000.0", CultureInfo.InvariantCulture);

            RenderTexture displacement = water.GetDisplacementMap();
            RenderTexture slopes = water.GetSlopeMap();

            var bandHeights = new float[4][];
            for (int layer = 0; layer < 4; layer++) {
                float[] disp = ReadLayer(displacement, layer, TextureFormat.RGBAFloat);
                stats.Add(Analyze(disp, "displacement", layer, time));
                bandHeights[layer] = ExtractChannel(disp, 1);
                SavePreview(disp, displacement.width, 1, Path.Combine(root, $"height_band{layer}_{token}.png"));
                if (layer == 0) SavePreview(disp, displacement.width, 3, Path.Combine(root, $"foam_band0_{token}.png"));

                float[] slope = ReadLayer(slopes, layer, TextureFormat.RGBAFloat);
                stats.Add(Analyze(slope, "slopes", layer, time));
                if (time == 50f) SavePreview(slope, slopes.width, 0, Path.Combine(root, $"slope_x_band{layer}_{token}.png"));
            }
            heightBaselines[token] = bandHeights;

            if (time == 0f || time == 100f) {
                RenderTexture spectrum = water.GetDisplacementSpectrum();
                for (int layer = 0; layer < 8; layer++)
                    stats.Add(Analyze(ReadLayer(spectrum, layer, TextureFormat.RGBAFloat), "spectrum", layer, time));
            }

            float[] buoyancy = ReadLayer(water.GetBuoyancyData(), 0, TextureFormat.RGBAFloat);
            stats.Add(Analyze(buoyancy, "buoyancy", 0, time));
            Debug.Log($"OCEAN_FIELD_DIAGNOSTICS time={time:0.0} captured");
        }

        FFTWater.ParitySuppressAutomaticUpdate = false;
        FFTWater.ParityFrameTime = null; FFTWater.ParityDeltaTime = null;

        // ---- Cross-time verdicts ----
        bool allFinite = true;
        foreach (var s in stats) if (s.nanCount > 0 || s.infCount > 0) allFinite = false;
        checks.Add(Verdict("finite_all_fields", allFinite, "no NaN/Inf in any captured field"));

        bool allBandsAlive = true;
        var bandRms = new double[4];
        for (int band = 0; band < 4; band++) {
            bandRms[band] = Rms(heightBaselines["t050.0"][band]);
            if (bandRms[band] < 1e-6) allBandsAlive = false;
        }
        checks.Add(Verdict("all_bands_written", allBandsAlive,
            $"height RMS per band at t=50: [{bandRms[0]:E3}, {bandRms[1]:E3}, {bandRms[2]:E3}, {bandRms[3]:E3}]"));

        double animRel = RelativeRmsDiff(heightBaselines["t000.0"], heightBaselines["t001.0"]);
        checks.Add(Verdict("waves_animate", animRel > 0.05, $"relative height change t=0 vs t=1: {animRel:F4}"));

        double periodicRel = RelativeRmsDiff(heightBaselines["t000.0"], heightBaselines["t200.0"]);
        checks.Add(Verdict("periodic_200s", periodicRel < 0.05, $"relative height difference t=0 vs t=200: {periodicRel:F5}"));

        double continuityRel = RelativeRmsDiff(heightBaselines["t199.9"], heightBaselines["t200.1"]);
        checks.Add(Verdict("continuity_around_200s", continuityRel < animRel,
            $"relative height difference t=199.9 vs t=200.1: {continuityRel:F4} (vs 1s change {animRel:F4})"));

        double totalRms = 0;
        for (int band = 0; band < 4; band++) totalRms += bandRms[band] * bandRms[band];
        totalRms = Math.Sqrt(totalRms);
        checks.Add(Verdict("plausible_amplitude", totalRms > 0.01 && totalRms < 50.0,
            $"combined height RMS: {totalRms:F4} m"));

        WriteJson(root, stats, checks);
        foreach (string line in checks) Debug.Log(line);
        bool pass = allFinite && allBandsAlive && animRel > 0.05 && periodicRel < 0.05;
        Debug.Log(pass ? "OCEAN_FIELD_DIAGNOSTICS_PASS" : "OCEAN_FIELD_DIAGNOSTICS_FAIL");
        if (!pass) throw new InvalidOperationException("Field diagnostics failed; see checks in JSON");
    }

    static string Verdict(string name, bool pass, string detail) {
        return $"CHECK {(pass ? "PASS" : "FAIL")} {name}: {detail}";
    }

    static float[] ReadLayer(RenderTexture source, int layer, TextureFormat format) {
        AsyncGPUReadbackRequest request = source.dimension == TextureDimension.Tex2DArray
            ? AsyncGPUReadback.Request(source, 0, 0, source.width, 0, source.height, layer, 1, format)
            : AsyncGPUReadback.Request(source, 0, format);
        request.WaitForCompletion();
        if (request.hasError) throw new InvalidOperationException($"GPU readback failed for {source.name} layer {layer}");
        NativeArray<float> native = request.GetData<float>();
        float[] managed = new float[native.Length];
        native.CopyTo(managed);
        return managed;
    }

    static LayerStats Analyze(float[] data, string texture, int layer, float time) {
        var s = new LayerStats { texture = texture, layer = layer, time = time };
        int channels = 4;
        long nonZero = 0;
        for (int c = 0; c < channels; c++) { s.min[c] = double.MaxValue; s.max[c] = double.MinValue; }
        for (int i = 0; i < data.Length; i++) {
            float v = data[i];
            if (float.IsNaN(v)) { s.nanCount++; continue; }
            if (float.IsInfinity(v)) { s.infCount++; continue; }
            int c = i & 3;
            if (v < s.min[c]) s.min[c] = v;
            if (v > s.max[c]) s.max[c] = v;
            s.mean[c] += v;
            s.rms[c] += (double)v * v;
            if (v != 0f) nonZero++;
        }
        long perChannel = data.Length / channels;
        for (int c = 0; c < channels; c++) {
            s.mean[c] /= perChannel;
            s.rms[c] = Math.Sqrt(s.rms[c] / perChannel);
        }
        s.nonZeroFraction = nonZero / (double)data.Length;
        return s;
    }

    static float[] ExtractChannel(float[] rgba, int channel) {
        float[] outData = new float[rgba.Length / 4];
        for (int i = 0; i < outData.Length; i++) outData[i] = rgba[i * 4 + channel];
        return outData;
    }

    static double Rms(float[] data) {
        double sum = 0;
        for (int i = 0; i < data.Length; i++) sum += (double)data[i] * data[i];
        return Math.Sqrt(sum / data.Length);
    }

    static double RelativeRmsDiff(float[][] a, float[][] b) {
        double diff = 0, reference = 0;
        for (int band = 0; band < a.Length; band++)
            for (int i = 0; i < a[band].Length; i++) {
                double d = a[band][i] - b[band][i];
                diff += d * d;
                reference += (double)a[band][i] * a[band][i];
            }
        return reference > 0 ? Math.Sqrt(diff / reference) : double.PositiveInfinity;
    }

    static void SavePreview(float[] rgba, int width, int channel, string path) {
        int height = rgba.Length / 4 / width;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = channel; i < rgba.Length; i += 4) {
            float v = rgba[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        float range = Mathf.Max(1e-9f, max - min);
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false, true);
        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++) {
            float v = rgba[i * 4 + channel];
            byte g = (float.IsNaN(v) || float.IsInfinity(v)) ? (byte)255 : (byte)Mathf.RoundToInt(Mathf.Clamp01((v - min) / range) * 255f);
            pixels[i] = (float.IsNaN(v) || float.IsInfinity(v)) ? new Color32(255, 0, 255, 255) : new Color32(g, g, g, 255);
        }
        tex.SetPixels32(pixels); tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
    }

    static void WriteJson(string root, List<LayerStats> stats, List<string> checks) {
        var sb = new StringBuilder();
        sb.Append("{\n  \"schema_version\": 1,\n  \"graphics_api\": \"").Append(SystemInfo.graphicsDeviceType)
          .Append("\",\n  \"graphics_device\": \"").Append(SystemInfo.graphicsDeviceName.Replace("\"", "'"))
          .Append("\",\n  \"checks\": [\n");
        for (int i = 0; i < checks.Count; i++)
            sb.Append("    \"").Append(checks[i].Replace("\"", "'")).Append(i < checks.Count - 1 ? "\",\n" : "\"\n");
        sb.Append("  ],\n  \"layers\": [\n");
        for (int i = 0; i < stats.Count; i++) {
            LayerStats s = stats[i];
            sb.Append("    {\"texture\": \"").Append(s.texture).Append("\", \"layer\": ").Append(s.layer)
              .Append(", \"time\": ").Append(s.time.ToString("0.0##", CultureInfo.InvariantCulture))
              .Append(", \"nan\": ").Append(s.nanCount).Append(", \"inf\": ").Append(s.infCount)
              .Append(", \"non_zero_fraction\": ").Append(s.nonZeroFraction.ToString("0.####", CultureInfo.InvariantCulture));
            for (int c = 0; c < 4; c++)
                sb.Append(", \"ch").Append(c).Append("\": [")
                  .Append(Num(s.min[c])).Append(", ").Append(Num(s.max[c])).Append(", ")
                  .Append(Num(s.mean[c])).Append(", ").Append(Num(s.rms[c])).Append(']');
            sb.Append('}').Append(i < stats.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(Path.Combine(root, "field_diagnostics.json"), sb.ToString());
    }

    static string Num(double v) {
        if (double.IsNaN(v) || double.IsInfinity(v) || v == double.MaxValue || v == double.MinValue) return "null";
        return v.ToString("0.######E+0", CultureInfo.InvariantCulture);
    }
}
