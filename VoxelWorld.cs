using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

public class VoxelWorld : MonoBehaviour
{
    [Header("World Size (in chunks)")]
    public int chunksX = 16;
    public int chunksZ = 16;

    [Header("Chunk")]
    public int chunkSize = 32;
    public int chunkHeight = 96;
    public GameObject chunkPrefab;

    [Header("Biome Weight Mask (RGBA) - Optional")]
    public Texture2D biomeMaskRGBA;
    public Vector2 maskWorldSize = new Vector2(512, 512);
    public Vector2 maskWorldOffset = Vector2.zero;

    [Header("Biome Mask Filtering (Optional)")]
    public bool blurBiomeMask = true;
    [Range(0, 2)] public int biomeMaskBlurRadius = 1;
    [Range(0f, 1f)] public float biomeMaskBlurStrength = 1f;

    // =========================
    // Sky Island
    // =========================
    [Header("Sky Island (Optional)")]
    [Tooltip("When enabled, (0,0,0,0) in BiomeMask becomes VOID (no terrain). World outside the mask becomes VOID as well.")]
    public bool generateAsSkyIsland = false;

    [Range(0f, 0.25f)] public float skyIslandVoidThreshold = 0.01f;

    [Tooltip("Auto set maskWorldSize to (WorldSizeX, WorldSizeZ) and offset to (0,0) on GenerateWorld.")]
    public bool skyIslandAutoFitMaskToWorld = true;

    [Header("Sky Island Underside Shape")]
    [Min(0)] public int skyIslandSoilDepth = 4;
    public float skyIslandLift = 32f;
    [Min(1)] public int skyIslandMinThickness = 10;
    [Min(1)] public int skyIslandMaxThickness = 84;
    [Range(0.5f, 6f)] public float skyIslandThicknessPower = 2.2f;
    [Range(0f, 1f)] public float skyIslandUndersideNoise = 0.18f;
    public float skyIslandUndersideNoiseScale = 0.035f;

    [Header("Sky Island Cliff / Spires")]
    public float skyIslandCliffDrop = 16f;
    public float skyIslandCliffPower = 2.4f;
    public float skyIslandSpireAmplitude = 90f;
    public float skyIslandSpireScale = 0.018f;
    public float skyIslandSpirePower = 3.2f;

    // ✅ stone gradient/noise
    [Header("Sky Island Stone Look")]
    public Gradient skyIslandStoneGradient; // darker->lighter
    [Range(0f, 1f)] public float skyIslandStoneGradientStrength = 0.55f;
    [Range(0f, 0.35f)] public float skyIslandStoneNoiseAmount = 0.10f;
    public float skyIslandStoneNoiseScale = 0.045f;

    [Header("Sky Island Stone Tint (fallback)")]
    public Color skyIslandStoneTintColor = new Color(0.62f, 0.66f, 0.70f, 1f);
    [Range(0f, 1f)] public float skyIslandStoneTintStrength = 0.65f;
    [Range(0f, 1f)] public float skyIslandStoneStartHeight01 = 0.55f;

    // Dirt tint
    [Header("Dirt Tint")]
    public Color dirtTintColor = new Color(0.45f, 0.34f, 0.23f, 1f);
    [Range(0f, 1f)] public float dirtTintStrength = 0.35f;

    // =========================
    // Feature mask (Road/River)
    // =========================
    [Header("Feature Mask (Road/River) - Optional")]
    public bool useFeatureMaskRoadRiver = false;
    public Texture2D featureMaskRG;
    public Vector2 featureMaskWorldSize = new Vector2(512, 512);
    public Vector2 featureMaskWorldOffset = Vector2.zero;

    // =========================
    // Generator
    // =========================
    [Header("Generator")]
    public int seed = 0;
    public bool randomizeSeedEachGenerate = true;

    [Header("Color (CubeWorld vibe) - Biome Gradients")]
    public Gradient plainsGradient;
    public Gradient hillsGradient;
    public Gradient mountainsGradient;
    public Gradient plateauGradient;

    [Range(0f, 0.25f)] public float noiseColorAmount = 0.08f;
    public float noiseColorScale = 0.035f;

    [Header("Block Color Overrides (Optional)")]
    public bool applyBlockTypeTint = true;

    [Header("Road Tint")]
    public Color roadTintColor = new Color(0.882f, 0.812f, 0.659f, 1f);
    [Range(0f, 1f)] public float roadTintStrength = 0.22f;

    [Header("River Bed Tint")]
    public Color riverBedTintColor = new Color(0.435f, 0.498f, 0.541f, 1f);
    [Range(0f, 1f)] public float riverBedTintStrength = 0.24f;

    [Header("River Water (if you render water via vertex color)")]
    public Color riverWaterTintColor = new Color(0.10f, 0.55f, 0.70f, 1f);
    [Range(0f, 1f)] public float riverWaterTintStrength = 0.0f;

    [Header("Materials")]
    public Material solidMaterial;

    // =========================
    // Post Process Tuning
    // =========================
    [Header("Post Process Tuning (Used by VoxelPostProcess)")]
    public float roadWidth = 6f;
    public float roadFade = 8f;
    public float riverWidth = 8f;
    public float riverFade = 10f;

    [Header("Feature Mask Thresholds (Used by VoxelPostProcess)")]
    [Range(0f, 1f)] public float roadThreshold = 0.35f;
    [Range(0f, 1f)] public float riverThreshold = 0.35f;

    [Header("Road Shaping (Used by VoxelPostProcess)")]
    [Range(0f, 1f)] public float roadFlattenStrength = 0.85f;

    [Header("River Shaping (Used by VoxelPostProcess)")]
    [Min(0f)] public float riverBedDepth = 6f;
    [Min(0f)] public float riverBankWidth = 6f;
    [Range(0f, 2f)] public float riverBankStrength = 1.0f;

    [Header("Debug")]
    public bool verbose = false;

    [Header("Sky Island Debug")]
    public bool debugSkyIsland = false;
    public bool debugLog = true;

    private readonly Dictionary<Vector2Int, VoxelChunk> _chunks = new();

    private float[] _skyIslandDist01;
    private int _skyW, _skyH;
    private float _skyMaxDist;

    public int WorldSizeX => chunksX * chunkSize;
    public int WorldSizeZ => chunksZ * chunkSize;

    private void Reset()
    {
        ApplyDefaultGradientsIfNeeded(force: true);
        ApplyDefaultStoneGradientIfNeeded(force: true);
    }

    private void OnValidate()
    {
        ApplyDefaultGradientsIfNeeded(force: false);
        ApplyDefaultStoneGradientIfNeeded(force: false);
    }

    private void ApplyDefaultStoneGradientIfNeeded(bool force)
    {
        if (!force && skyIslandStoneGradient != null && skyIslandStoneGradient.colorKeys != null && skyIslandStoneGradient.colorKeys.Length > 0)
            return;

        skyIslandStoneGradient = MakeGradient(
            (new Color(0.34f, 0.36f, 0.38f, 1f), 0.00f),
            (new Color(0.55f, 0.58f, 0.60f, 1f), 0.55f),
            (new Color(0.78f, 0.81f, 0.84f, 1f), 1.00f)
        );
    }

    private void ApplyDefaultGradientsIfNeeded(bool force)
    {
        if (force || IsUnsetOrWhite(plainsGradient)) plainsGradient = DefaultPlains();
        if (force || IsUnsetOrWhite(hillsGradient)) hillsGradient = DefaultHills();
        if (force || IsUnsetOrWhite(mountainsGradient)) mountainsGradient = DefaultMountains();
        if (force || IsUnsetOrWhite(plateauGradient)) plateauGradient = DefaultPlateau();
    }

    private static bool IsUnsetOrWhite(Gradient g)
    {
        if (g == null) return true;
        var keys = g.colorKeys;
        if (keys == null || keys.Length == 0) return true;

        for (int i = 0; i < keys.Length; i++)
        {
            var c = keys[i].color;
            if (c.r < 0.98f || c.g < 0.98f || c.b < 0.98f) return false;
        }
        return true;
    }

    private void DLog(string msg)
    {
        if (!debugSkyIsland || !debugLog) return;
        Debug.Log(msg);
    }

    private void DWarn(string msg)
    {
        if (!debugSkyIsland || !debugLog) return;
        Debug.LogWarning(msg);
    }

    [ContextMenu("Generate World")]
    public void GenerateWorld()
    {
        if (randomizeSeedEachGenerate)
            seed = UnityEngine.Random.Range(int.MinValue / 2, int.MaxValue / 2);

        ApplyDefaultGradientsIfNeeded(force: false);
        ApplyDefaultStoneGradientIfNeeded(force: false);

        ClearWorld();

        if (generateAsSkyIsland)
        {
            if (skyIslandAutoFitMaskToWorld)
            {
                maskWorldOffset = Vector2.zero;
                maskWorldSize = new Vector2(WorldSizeX, WorldSizeZ);
            }

            BuildSkyIslandDistanceField();

            // ✅ FIX: string escape 없이 안전하게
            string texStr = biomeMaskRGBA ? $"{biomeMaskRGBA.width}x{biomeMaskRGBA.height}" : "null";
            DLog($"[SkyIsland DEBUG] GenerateWorld() seed={seed} maskSize=({maskWorldSize.x:0.00}, {maskWorldSize.y:0.00}) maskOffset=({maskWorldOffset.x:0.00}, {maskWorldOffset.y:0.00}) tex={texStr}");
            DebugSkyIslandSampler();
        }

        var gen = new VoxelWorldGen(seed, chunkSize, chunkHeight, WorldSizeX, WorldSizeZ)
        {
            skyIslandMode = generateAsSkyIsland,
            voidThreshold = skyIslandVoidThreshold,
            skyIslandLift = skyIslandLift,
            skyIslandMinThickness = skyIslandMinThickness,
            skyIslandMaxThickness = skyIslandMaxThickness,
            skyIslandSoilDepth = skyIslandSoilDepth,
            skyIslandThicknessPower = skyIslandThicknessPower,
            skyIslandUndersideNoise = skyIslandUndersideNoise,
            skyIslandUndersideNoiseScale = skyIslandUndersideNoiseScale,
            skyIslandCliffDrop = skyIslandCliffDrop,
            skyIslandCliffPower = skyIslandCliffPower,
            skyIslandSpireAmplitude = skyIslandSpireAmplitude,
            skyIslandSpireScale = skyIslandSpireScale,
            skyIslandSpirePower = skyIslandSpirePower,
            islandDistanceSampler01 = generateAsSkyIsland ? SampleSkyIslandDistance01 : null,

            // debug pass-through
            debugSkyIsland = debugSkyIsland,
            debugLog = debugLog
        };

        for (int cz = 0; cz < chunksZ; cz++)
        {
            for (int cx = 0; cx < chunksX; cx++)
            {
                var key = new Vector2Int(cx, cz);
                var chunkGO = Instantiate(chunkPrefab, transform);
                chunkGO.name = $"Chunk_{cx}_{cz}";
                chunkGO.transform.position = new Vector3(cx * chunkSize, 0, cz * chunkSize);

                var chunk = chunkGO.GetComponent<VoxelChunk>();
                if (!chunk) chunk = chunkGO.AddComponent<VoxelChunk>();

                chunk.Init(this, cx, cz, chunkSize, chunkHeight, solidMaterial);
                gen.FillChunkVoxels(chunk, SampleBiomeWeights01);

                _chunks.Add(key, chunk);
            }
        }

        foreach (var kv in _chunks)
            gen.CarveCaveEntrances(kv.Value);

        if (useFeatureMaskRoadRiver && featureMaskRG)
            VoxelPostProcess.ApplyFeatureMaskRoadRiver(this);

        RebuildAllMeshes();
    }

    private void DebugSkyIslandSampler()
    {
        if (!debugSkyIsland) return;

        float a = SampleSkyIslandDistance01(new Vector3(1, 0, 1));
        float b = SampleSkyIslandDistance01(new Vector3(WorldSizeX - 1, 0, 1));
        float c = SampleSkyIslandDistance01(new Vector3(1, 0, WorldSizeZ - 1));
        float d = SampleSkyIslandDistance01(new Vector3(WorldSizeX - 1, 0, WorldSizeZ - 1));
        float e = SampleSkyIslandDistance01(new Vector3(WorldSizeX * 0.5f, 0, WorldSizeZ * 0.5f));

        DLog($"[SkyIsland DEBUG] dist01 samples:  (1,1)={a:0.000}  ({WorldSizeX - 1},1)={b:0.000}  (1,{WorldSizeZ - 1})={c:0.000}  ({WorldSizeX - 1},{WorldSizeZ - 1})={d:0.000}  ({WorldSizeX / 2},{WorldSizeZ / 2})={e:0.000}");
        DLog($"[SkyIsland DEBUG] distField: tex={_skyW}x{_skyH} maxDist={_skyMaxDist:0.000} distArr={(_skyIslandDist01 != null ? _skyIslandDist01.Length : 0)}");
    }

    [ContextMenu("Clear World")]
    public void ClearWorld()
    {
        foreach (var kv in _chunks)
        {
            if (kv.Value) DestroyImmediate(kv.Value.gameObject);
        }
        _chunks.Clear();
    }

    [ContextMenu("Rebuild All Meshes")]
    public void RebuildAllMeshes()
    {
        foreach (var kv in _chunks)
            kv.Value.BuildMesh();
    }

    public bool TryGetChunk(int cx, int cz, out VoxelChunk chunk)
        => _chunks.TryGetValue(new Vector2Int(cx, cz), out chunk);

    public void SetWorld(int wx, int y, int wz, byte v)
    {
        if (wx < 0 || wz < 0 || wx >= WorldSizeX || wz >= WorldSizeZ) return;
        if (y < 0 || y >= chunkHeight) return;

        int cx = wx / chunkSize;
        int cz = wz / chunkSize;

        if (!TryGetChunk(cx, cz, out var ch) || ch == null) return;

        int lx = wx - cx * chunkSize;
        int lz = wz - cz * chunkSize;

        ch.Set(lx, y, lz, v);
    }

    // =========================================================
    // Sampling
    // =========================================================

    // ✅ NEW: "RAW(노블러)" 샘플. VOID 판정은 반드시 이 값으로만 한다.
    private Vector4 SampleBiomeMaskRaw_NoBlur(float u01, float v01)
    {
        if (biomeMaskRGBA == null) return Vector4.zero;

        if (generateAsSkyIsland && (u01 < 0f || u01 > 1f || v01 < 0f || v01 > 1f))
            return Vector4.zero;

        var c = biomeMaskRGBA.GetPixelBilinear(Mathf.Clamp01(u01), Mathf.Clamp01(v01));
        return new Vector4(c.r, c.g, c.b, c.a);
    }

    public Vector4 SampleBiomeWeights01(Vector3 worldPos)
    {
        if (biomeMaskRGBA)
        {
            float u = (worldPos.x - maskWorldOffset.x) / Mathf.Max(1e-3f, maskWorldSize.x);
            float v = (worldPos.z - maskWorldOffset.y) / Mathf.Max(1e-3f, maskWorldSize.y);

            if (generateAsSkyIsland)
            {
                if (u < 0f || u > 1f || v < 0f || v > 1f)
                    return Vector4.zero;
            }
            else
            {
                u = Mathf.Clamp01(u);
                v = Mathf.Clamp01(v);
            }

            // ✅ 1) VOID 판정은 RAW(노블러)로만
            Vector4 raw = SampleBiomeMaskRaw_NoBlur(u, v);
            float rawSum = raw.x + raw.y + raw.z + raw.w;

            if (generateAsSkyIsland && rawSum <= skyIslandVoidThreshold)
                return Vector4.zero;

            // ✅ 2) 가중치는 기존 SampleBiomeMaskRaw(블러 가능) 사용
            Vector4 w = SampleBiomeMaskRaw(u, v);
            float sum = w.x + w.y + w.z + w.w;

            // ✅ 3) 블러로 sum이 너무 작아지면 RAW로 fallback
            if (sum <= 1e-6f)
            {
                w = raw;
                sum = rawSum;
            }

            if (sum <= 1e-6f)
                return generateAsSkyIsland ? Vector4.zero : new Vector4(0.6f, 0.25f, 0.15f, 0f);

            return w / sum;
        }

        // no mask fallback (기존 유지)
        float s1 = 0.0025f;
        float s2 = 0.0060f;

        float n1 = Mathf.PerlinNoise((worldPos.x + seed * 17.1f) * s1, (worldPos.z - seed * 9.3f) * s1);
        float n2 = Mathf.PerlinNoise((worldPos.x - seed * 3.7f) * s2, (worldPos.z + seed * 5.9f) * s2);

        float plains = Mathf.Clamp01(1.0f - Mathf.SmoothStep(0.35f, 0.70f, n1));
        float mountains = Mathf.Clamp01(Mathf.SmoothStep(0.45f, 0.85f, n1));

        float hills = Mathf.Clamp01(Mathf.SmoothStep(0.35f, 0.70f, n2) * (1f - mountains));
        float plateau = Mathf.Clamp01(Mathf.SmoothStep(0.65f, 0.90f, n2) * (1f - plains) * 0.6f);

        float sum2 = plains + hills + mountains + plateau;
        if (sum2 < 1e-5f) return new Vector4(0.6f, 0.25f, 0.15f, 0f);

        return new Vector4(plains, hills, mountains, plateau) / sum2;
    }

    // ✅ blur에서 voidThreshold 이하를 "완전 0" 취급해서 번짐 방지 (네 코드 유지)
    private Vector4 SampleBiomeMaskRaw(float u01, float v01)
    {
        if (biomeMaskRGBA == null) return Vector4.zero;

        if (blurBiomeMask && biomeMaskBlurRadius > 0)
        {
            int r = Mathf.Clamp(biomeMaskBlurRadius, 1, 2);
            float texelU = 1f / Mathf.Max(1, biomeMaskRGBA.width);
            float texelV = 1f / Mathf.Max(1, biomeMaskRGBA.height);

            Vector4 acc = Vector4.zero;
            int count = 0;

            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    float su = u01 + dx * texelU;
                    float sv = v01 + dz * texelV;

                    if (generateAsSkyIsland && (su < 0f || su > 1f || sv < 0f || sv > 1f))
                    {
                        count++;
                        continue;
                    }

                    var c = biomeMaskRGBA.GetPixelBilinear(Mathf.Clamp01(su), Mathf.Clamp01(sv));
                    float ssum = c.r + c.g + c.b + c.a;

                    if (!(generateAsSkyIsland && ssum <= skyIslandVoidThreshold))
                        acc += new Vector4(c.r, c.g, c.b, c.a);

                    count++;
                }
            }

            Vector4 blurred = (count > 0) ? (acc / count) : Vector4.zero;

            Vector4 raw;
            if (generateAsSkyIsland && (u01 < 0f || u01 > 1f || v01 < 0f || v01 > 1f))
                raw = Vector4.zero;
            else
            {
                var c0 = biomeMaskRGBA.GetPixelBilinear(Mathf.Clamp01(u01), Mathf.Clamp01(v01));
                float sum0 = c0.r + c0.g + c0.b + c0.a;

                if (generateAsSkyIsland && sum0 <= skyIslandVoidThreshold)
                    raw = Vector4.zero;
                else
                    raw = new Vector4(c0.r, c0.g, c0.b, c0.a);
            }

            return Vector4.Lerp(raw, blurred, Mathf.Clamp01(biomeMaskBlurStrength));
        }

        if (generateAsSkyIsland && (u01 < 0f || u01 > 1f || v01 < 0f || v01 > 1f))
            return Vector4.zero;

        var c1 = biomeMaskRGBA.GetPixelBilinear(Mathf.Clamp01(u01), Mathf.Clamp01(v01));
        float sum1 = c1.r + c1.g + c1.b + c1.a;

        if (generateAsSkyIsland && sum1 <= skyIslandVoidThreshold)
            return Vector4.zero;

        return new Vector4(c1.r, c1.g, c1.b, c1.a);
    }

    public Vector2 SampleFeatureWeights01(Vector3 worldPos)
    {
        if (!featureMaskRG) return Vector2.zero;

        float u = (worldPos.x - featureMaskWorldOffset.x) / Mathf.Max(1e-3f, featureMaskWorldSize.x);
        float v = (worldPos.z - featureMaskWorldOffset.y) / Mathf.Max(1e-3f, featureMaskWorldSize.y);

        u = Mathf.Clamp01(u);
        v = Mathf.Clamp01(v);

        var c = featureMaskRG.GetPixelBilinear(u, v);
        return new Vector2(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g));
    }

    // =========================================================
    // Sky island distance field
    // =========================================================
    private void BuildSkyIslandDistanceField()
    {
        _skyIslandDist01 = null;
        _skyW = _skyH = 0;
        _skyMaxDist = 0f;

        if (!generateAsSkyIsland || biomeMaskRGBA == null) return;

        _skyW = biomeMaskRGBA.width;
        _skyH = biomeMaskRGBA.height;
        if (_skyW <= 0 || _skyH <= 0) return;

        Color[] px = biomeMaskRGBA.GetPixels();
        int n = px.Length;

        var land = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var c = px[i];
            float sum = c.r + c.g + c.b + c.a;
            land[i] = sum > skyIslandVoidThreshold;
        }

        const float INF = 1e9f;
        var dist = new float[n];

        for (int y = 0; y < _skyH; y++)
        {
            int row = y * _skyW;
            bool yBorder = (y == 0 || y == _skyH - 1);

            for (int x = 0; x < _skyW; x++)
            {
                int idx = row + x;
                bool xBorder = (x == 0 || x == _skyW - 1);

                if (!land[idx]) dist[idx] = 0f;
                else dist[idx] = (xBorder || yBorder) ? 0f : INF;
            }
        }

        for (int y = 0; y < _skyH; y++)
        {
            int row = y * _skyW;
            for (int x = 0; x < _skyW; x++)
            {
                int idx = row + x;
                float d = dist[idx];
                if (d <= 0f) continue;

                if (x > 0) d = Mathf.Min(d, dist[idx - 1] + 1f);
                if (y > 0) d = Mathf.Min(d, dist[idx - _skyW] + 1f);
                if (x > 0 && y > 0) d = Mathf.Min(d, dist[idx - _skyW - 1] + 1.4142f);
                if (x < _skyW - 1 && y > 0) d = Mathf.Min(d, dist[idx - _skyW + 1] + 1.4142f);

                dist[idx] = d;
            }
        }

        for (int y = _skyH - 1; y >= 0; y--)
        {
            int row = y * _skyW;
            for (int x = _skyW - 1; x >= 0; x--)
            {
                int idx = row + x;
                float d = dist[idx];
                if (d <= 0f) continue;

                if (x < _skyW - 1) d = Mathf.Min(d, dist[idx + 1] + 1f);
                if (y < _skyH - 1) d = Mathf.Min(d, dist[idx + _skyW] + 1f);
                if (x < _skyW - 1 && y < _skyH - 1) d = Mathf.Min(d, dist[idx + _skyW + 1] + 1.4142f);
                if (x > 0 && y < _skyH - 1) d = Mathf.Min(d, dist[idx + _skyW - 1] + 1.4142f);

                dist[idx] = d;
            }
        }

        _skyMaxDist = 0f;
        for (int i = 0; i < n; i++)
        {
            if (!land[i]) continue;
            if (dist[i] < INF * 0.5f)
                _skyMaxDist = Mathf.Max(_skyMaxDist, dist[i]);
        }
        if (_skyMaxDist <= 1e-6f) _skyMaxDist = 1f;

        _skyIslandDist01 = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (!land[i]) { _skyIslandDist01[i] = 0f; continue; }

            float d = dist[i];
            if (d > INF * 0.5f) d = 0f;

            _skyIslandDist01[i] = Mathf.Clamp01(d / _skyMaxDist);
        }

        DLog($"[SkyIsland DEBUG] BuildSkyIslandDistanceField done. landTex={_skyW}x{_skyH} maxDist={_skyMaxDist:0.###}");
    }

    public float SampleSkyIslandDistance01(Vector3 worldPos)
    {
        if (!generateAsSkyIsland || _skyIslandDist01 == null || _skyW <= 0 || _skyH <= 0)
            return 0f;

        float u = (worldPos.x - maskWorldOffset.x) / Mathf.Max(1e-3f, maskWorldSize.x);
        float v = (worldPos.z - maskWorldOffset.y) / Mathf.Max(1e-3f, maskWorldSize.y);
        if (u < 0f || u > 1f || v < 0f || v > 1f) return 0f;

        float fx = u * (_skyW - 1);
        float fy = v * (_skyH - 1);

        int x0 = Mathf.Clamp((int)fx, 0, _skyW - 1);
        int y0 = Mathf.Clamp((int)fy, 0, _skyH - 1);
        int x1 = Mathf.Min(x0 + 1, _skyW - 1);
        int y1 = Mathf.Min(y0 + 1, _skyH - 1);

        float tx = fx - x0;
        float ty = fy - y0;

        float A = _skyIslandDist01[y0 * _skyW + x0];
        float B = _skyIslandDist01[y0 * _skyW + x1];
        float C = _skyIslandDist01[y1 * _skyW + x0];
        float D = _skyIslandDist01[y1 * _skyW + x1];

        float ab = Mathf.Lerp(A, B, tx);
        float cd = Mathf.Lerp(C, D, tx);
        return Mathf.Lerp(ab, cd, ty);
    }

    // =========================================================
    // Vertex color
    // =========================================================
    public Color ComputeVertexColor(Vector3 worldPos, Vector4 biomeW)
    {
        float h01 = Mathf.InverseLerp(0f, chunkHeight - 1, worldPos.y);

        Color cp = plainsGradient.Evaluate(h01);
        Color ch = hillsGradient.Evaluate(h01);
        Color cm = mountainsGradient.Evaluate(h01);
        Color ct = plateauGradient.Evaluate(h01);

        Color col = cp * biomeW.x + ch * biomeW.y + cm * biomeW.z + ct * biomeW.w;
        col.a = 1f;

        float x = worldPos.x;
        float z = worldPos.z;
        float s = Mathf.Max(0.0001f, noiseColorScale);

        float n1 = Mathf.PerlinNoise(x * (s * 0.35f) + 11.7f, z * (s * 0.35f) + 3.1f);
        float n2 = Mathf.PerlinNoise(x * (s * 1.00f) + 71.3f, z * (s * 1.00f) + 9.9f);
        float n3 = Mathf.PerlinNoise(x * (s * 2.60f) + 19.2f, z * (s * 2.60f) + 41.6f);

        float n = (n1 * 0.55f + n2 * 0.30f + n3 * 0.15f);
        float noise = (n - 0.5f) * 2f;
        float k = 1f + noise * noiseColorAmount;

        col.r = Mathf.Clamp01(col.r * k);
        col.g = Mathf.Clamp01(col.g * k);
        col.b = Mathf.Clamp01(col.b * k);

        return col;
    }

    public Color ApplyBlockTint(Color baseColor, byte blockId, Vector3 worldPos)
    {
        if (!applyBlockTypeTint) return baseColor;

        if (blockId == VoxelChunk.ROAD)
            return Color.Lerp(baseColor, roadTintColor, Mathf.Clamp01(roadTintStrength));

        if (blockId == VoxelChunk.RIVERBED)
            return Color.Lerp(baseColor, riverBedTintColor, Mathf.Clamp01(riverBedTintStrength));

        if (blockId == VoxelChunk.DIRT)
            return Color.Lerp(baseColor, dirtTintColor, Mathf.Clamp01(dirtTintStrength));

        if (blockId == VoxelChunk.STONE)
        {
            float h01 = Mathf.InverseLerp(0f, chunkHeight - 1, worldPos.y);

            Color gcol = (skyIslandStoneGradient != null) ? skyIslandStoneGradient.Evaluate(h01) : skyIslandStoneTintColor;
            float gs = Mathf.Clamp01(skyIslandStoneGradientStrength);

            Color col = Color.Lerp(skyIslandStoneTintColor, gcol, gs);

            float s = Mathf.Max(0.0001f, skyIslandStoneNoiseScale);
            float n = Mathf.PerlinNoise((worldPos.x + seed * 13.7f) * s, (worldPos.z - seed * 9.9f) * s);
            float noise = (n - 0.5f) * 2f;
            float k = 1f + noise * Mathf.Clamp01(skyIslandStoneNoiseAmount);

            col.r = Mathf.Clamp01(col.r * k);
            col.g = Mathf.Clamp01(col.g * k);
            col.b = Mathf.Clamp01(col.b * k);
            col.a = 1f;

            if (generateAsSkyIsland)
            {
                float start = Mathf.Clamp01(skyIslandStoneStartHeight01);
                if (h01 < start)
                {
                    float a = Mathf.InverseLerp(start, 0f, h01);
                    a = a * a * (3f - 2f * a);
                    float t = a * Mathf.Clamp01(skyIslandStoneTintStrength);
                    col = Color.Lerp(col, skyIslandStoneTintColor, t);
                }
            }

            return col;
        }

        return baseColor;
    }

    // =========================================================
    // Save / Load (기존 유지)
    // =========================================================
    public void SaveWorldBinary(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(0x434D5657);
        bw.Write(1);
        bw.Write(seed);
        bw.Write(chunksX);
        bw.Write(chunksZ);
        bw.Write(chunkSize);
        bw.Write(chunkHeight);

        foreach (var kv in _chunks)
        {
            var ch = kv.Value;
            bw.Write(ch.cx);
            bw.Write(ch.cz);
            ch.WriteRLE(bw);
        }

        if (verbose) Debug.Log($"[VoxelWorld] Saved: {path}");
    }

    public void LoadWorldBinary(string path)
    {
        if (!File.Exists(path)) { Debug.LogError($"Missing file: {path}"); return; }

        ApplyDefaultGradientsIfNeeded(force: false);
        ApplyDefaultStoneGradientIfNeeded(force: false);

        ClearWorld();

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        int magic = br.ReadInt32();
        if (magic != 0x434D5657) { Debug.LogError("Invalid file"); return; }
        int version = br.ReadInt32();
        if (version != 1) { Debug.LogError($"Unsupported version: {version}"); return; }

        seed = br.ReadInt32();
        chunksX = br.ReadInt32();
        chunksZ = br.ReadInt32();
        chunkSize = br.ReadInt32();
        chunkHeight = br.ReadInt32();

        int total = chunksX * chunksZ;

        for (int i = 0; i < total; i++)
        {
            int cx = br.ReadInt32();
            int cz = br.ReadInt32();

            var chunkGO = Instantiate(chunkPrefab, transform);
            chunkGO.name = $"Chunk_{cx}_{cz}";
            chunkGO.transform.position = new Vector3(cx * chunkSize, 0, cz * chunkSize);

            var chunk = chunkGO.GetComponent<VoxelChunk>();
            if (!chunk) chunk = chunkGO.AddComponent<VoxelChunk>();

            chunk.Init(this, cx, cz, chunkSize, chunkHeight, solidMaterial);
            chunk.ReadRLE(br);

            _chunks.Add(new Vector2Int(cx, cz), chunk);
        }

        RebuildAllMeshes();

        if (verbose) Debug.Log($"[VoxelWorld] Loaded: {path}");
    }

    public void ClearChunkRegistry_EditorSafe() { }

    // =========================================================
    // Default gradients
    // =========================================================
    private static Gradient MakeGradient(params (Color c, float t)[] keys)
    {
        var g = new Gradient();
        var ck = new GradientColorKey[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            ck[i] = new GradientColorKey(keys[i].c, keys[i].t);

        g.SetKeys(ck, new[]
        {
            new GradientAlphaKey(1, 0),
            new GradientAlphaKey(1, 1),
        });

        return g;
    }

    private static Color Hex(string hex)
    {
        if (!hex.StartsWith("#")) hex = "#" + hex;
        ColorUtility.TryParseHtmlString(hex, out var c);
        return QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
    }

    private static Gradient DefaultPlains() => MakeGradient(
        (Hex("#1FAE57"), 0.00f),
        (Hex("#42D86A"), 0.35f),
        (Hex("#7BEE66"), 0.70f),
        (Hex("#C9FF7A"), 1.00f)
    );

    private static Gradient DefaultHills() => MakeGradient(
        (Hex("#168A4B"), 0.00f),
        (Hex("#2FBE5C"), 0.35f),
        (Hex("#63D86B"), 0.70f),
        (Hex("#A7F07E"), 1.00f)
    );

    private static Gradient DefaultMountains() => MakeGradient(
        (Hex("#7E8A8C"), 0.00f),
        (Hex("#9AA5A7"), 0.45f),
        (Hex("#C7D0D3"), 0.78f),
        (Hex("#F5FAFF"), 1.00f)
    );

    private static Gradient DefaultPlateau() => MakeGradient(
        (Hex("#6FAE83"), 0.00f),
        (Hex("#86BE92"), 0.40f),
        (Hex("#A9C6B4"), 0.80f),
        (Hex("#DCE6E2"), 1.00f)
    );
}
