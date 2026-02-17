using System;
using UnityEngine;

public class VoxelWorldGen
{
    private readonly int _seed;
    private readonly int _chunkSize;
    private readonly int _chunkHeight;
    private readonly int _worldSizeX;
    private readonly int _worldSizeZ;

    // =========================
    // Sky island (configured by VoxelWorld)
    // =========================
    public bool skyIslandMode = false;
    public bool autoTuneSkyIsland = true;

    public float voidThreshold = 0.01f;
    public float skyIslandLift = 24f;

    public int skyIslandMinThickness = 10;
    public int skyIslandMaxThickness = 84;

    public int skyIslandSoilDepth = 4; // grass
    public int skyIslandDirtDepth = 6; // dirt thickness after grass

    public float skyIslandThicknessPower = 2.2f;
    public float skyIslandUndersideNoise = 0.18f;
    public float skyIslandUndersideNoiseScale = 0.035f;

    public float skyIslandCliffDrop = 14f;
    public float skyIslandCliffPower = 2.4f;

    public float skyIslandSpireAmplitude = 26f;
    public float skyIslandSpireScale = 0.022f;
    public float skyIslandSpirePower = 2.4f;

    public Func<Vector3, float> islandDistanceSampler01;

    // =========================
    // Debug (VoxelWorld에서 설정)
    // =========================
    public bool debugSkyIsland = false;
    public bool debugLog = true;

    // =========================
    // Height tuning
    // =========================
    private const float BASE_NOISE_SCALE = 0.022f;
    private const float BASE_NOISE_AMPL = 6.0f;

    public VoxelWorldGen(int seed, int chunkSize, int chunkHeight, int worldSizeX, int worldSizeZ)
    {
        _seed = seed;
        _chunkSize = chunkSize;
        _chunkHeight = chunkHeight;
        _worldSizeX = worldSizeX;
        _worldSizeZ = worldSizeZ;
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

    private struct SkyIslandAutoParams
    {
        public float lift;
        public float minThick;
        public float maxThick;
        public float thickPower;

        public float cliffDrop;
        public float cliffPower;

        public float undersideAmp;
        public float undersideScale;

        public float spireAmp;
        public float spireScale;
        public float spirePower;

        public float edgeCapMaxExtraThick;
        public float edgeCapRange;

        public float topBulgeAmp;
        public float topBulgeRange;
    }

    private SkyIslandAutoParams AutoTuneSkyIsland()
    {
        float H = _chunkHeight;

        var p = new SkyIslandAutoParams();

        p.lift = Mathf.Round(H * 0.56f);

        p.minThick = Mathf.Clamp(H * 0.12f, 8f, 18f);
        p.maxThick = Mathf.Clamp(H * 0.62f, 45f, 90f);
        p.thickPower = 2.25f;

        p.cliffDrop = Mathf.Clamp(H * 0.14f, 10f, 22f);
        p.cliffPower = 2.2f;

        p.undersideAmp = H * 0.18f;
        p.undersideScale = 0.012f;

        p.spireAmp = H * 0.42f;
        p.spireScale = 0.020f;
        p.spirePower = 2.9f;

        p.edgeCapMaxExtraThick = H * 0.10f;
        p.edgeCapRange = 0.22f;

        p.topBulgeAmp = H * 0.10f;
        p.topBulgeRange = 0.32f;

        return p;
    }

    public void FillChunkVoxels(VoxelChunk chunk, Func<Vector3, Vector4> biomeSampler01)
    {
        if (chunk == null) return;
        if (biomeSampler01 == null) biomeSampler01 = _ => new Vector4(1, 0, 0, 0);

        SkyIslandAutoParams autoP = default;
        if (skyIslandMode && autoTuneSkyIsland)
            autoP = AutoTuneSkyIsland();

        int baseX = chunk.cx * _chunkSize;
        int baseZ = chunk.cz * _chunkSize;

        // debug stats (first chunk only)
        bool doStats = debugSkyIsland && chunk.cx == 0 && chunk.cz == 0;

        int landCols = 0, voidCols = 0;
        float distMin = 999f, distMax = -999f;
        int surfaceMin = int.MaxValue, surfaceMax = int.MinValue;
        int bottomMin = int.MaxValue, bottomMax = int.MinValue;
        int bottomLE0 = 0;

        for (int z = 0; z < chunk.size; z++)
        {
            for (int x = 0; x < chunk.size; x++)
            {
                int wx = baseX + x;
                int wz = baseZ + z;
                var wp = new Vector3(wx + 0.5f, 0f, wz + 0.5f);

                Vector4 w = biomeSampler01(wp);
                float sum = w.x + w.y + w.z + w.w;

                if (skyIslandMode)
                {
                    if (sum <= voidThreshold)
                    {
                        voidCols++;
                        for (int y = 0; y < chunk.height; y++)
                            chunk.Set(x, y, z, VoxelChunk.AIR);
                        continue;
                    }
                }

                landCols++;

                if (sum > 1e-6f) w /= sum;
                else w = new Vector4(1, 0, 0, 0);

                float dist01 = 1f;
                if (skyIslandMode)
                {
                    dist01 = (islandDistanceSampler01 != null)
                        ? Mathf.Clamp01(islandDistanceSampler01(wp))
                        : 1f;
                }

                if (doStats)
                {
                    distMin = Mathf.Min(distMin, dist01);
                    distMax = Mathf.Max(distMax, dist01);
                }

                int surfaceY = ComputeSurfaceY(wx, wz, w, dist01, autoP);

                if (doStats)
                {
                    surfaceMin = Mathf.Min(surfaceMin, surfaceY);
                    surfaceMax = Mathf.Max(surfaceMax, surfaceY);
                }

                if (skyIslandMode)
                {
                    int bottomY = ComputeSkyIslandBottomY(wx, wz, surfaceY, dist01, autoP);

                    if (doStats)
                    {
                        bottomMin = Mathf.Min(bottomMin, bottomY);
                        bottomMax = Mathf.Max(bottomMax, bottomY);
                        if (bottomY <= 0) bottomLE0++;
                    }

                    FillColumnSkyIsland(chunk, x, z, bottomY, surfaceY);
                }
                else
                {
                    FillColumnNormal(chunk, x, z, surfaceY);
                }
            }
        }

        if (doStats)
        {
            DLog(
                $"[SkyIsland DEBUG] world=({_worldSizeX}x{_worldSizeZ}) chunk=({_chunkSize}x{_chunkSize}x{_chunkHeight}) seed={_seed}\n" +
                $"  landCols={landCols} voidCols={voidCols}\n" +
                $"  dist01: min={distMin:0.000} max={distMax:0.000}\n" +
                $"  surfaceY: min={surfaceMin} max={surfaceMax}\n" +
                $"  bottomY : min={bottomMin} max={bottomMax}  bottom<=0 count={bottomLE0}"
            );

            if (landCols == 0)
                DWarn("[SkyIsland DEBUG] landCols=0 => BiomeMask가 전부 VOID로 판정됨. (mask size/offset/threshold/blur 문제)");
        }
    }

    // TOP SURFACE: keep existing biome/noise style with sky-island edge drop
    private int ComputeSurfaceY(int wx, int wz, Vector4 biomeW, float dist01, SkyIslandAutoParams autoP)
    {
        float plains = biomeW.x;
        float hills = biomeW.y;
        float mountains = biomeW.z;
        float plateau = biomeW.w;

        float baseH = 18f * plains + 28f * hills + 46f * mountains + 34f * plateau;

        float ns = BASE_NOISE_SCALE;
        float nA = Mathf.PerlinNoise((wx + _seed * 17.1f) * ns, (wz - _seed * 9.3f) * ns);
        float nB = Mathf.PerlinNoise((wx - _seed * 3.7f) * (ns * 2.2f), (wz + _seed * 5.9f) * (ns * 2.2f));
        float noise = (nA * 0.65f + nB * 0.35f);
        noise = (noise - 0.5f) * 2f;

        float ampl = BASE_NOISE_AMPL * Mathf.Lerp(0.6f, 1.35f, mountains);
        float h = baseH + noise * ampl;

        if (skyIslandMode)
        {
            float edge01 = Mathf.Clamp01(dist01);
            float edgeNoiseMul = Mathf.Lerp(0.55f, 1.0f, edge01);
            h = baseH + noise * (ampl * edgeNoiseMul);

            float cliffDrop = autoTuneSkyIsland ? autoP.cliffDrop : skyIslandCliffDrop;
            float cliffPower = Mathf.Max(0.01f, autoTuneSkyIsland ? autoP.cliffPower : skyIslandCliffPower);
            h -= cliffDrop * Mathf.Pow(1f - edge01, cliffPower);

            h += autoTuneSkyIsland ? autoP.lift : skyIslandLift;
        }

        h = Mathf.Lerp(h, baseH, plateau * 0.45f);

        int surfaceY = Mathf.RoundToInt(h);
        surfaceY = Mathf.Clamp(surfaceY, 6, _chunkHeight - 2);
        return surfaceY;
    }

    // UNDERSIDE: suspended funnel profile (thin rim -> broad body -> centered tip)
    private int ComputeSkyIslandBottomY(int wx, int wz, int surfaceY, float dist01, SkyIslandAutoParams autoP)
    {
        float edgeToCenter01 = Mathf.Clamp01(dist01);
        float worldH = _chunkHeight;

        float minThickness = autoTuneSkyIsland ? autoP.minThick : skyIslandMinThickness;
        float maxThickness = autoTuneSkyIsland ? autoP.maxThick : skyIslandMaxThickness;
        float thicknessPower = Mathf.Max(0.01f, autoTuneSkyIsland ? autoP.thickPower : skyIslandThicknessPower);

        // Keep the island suspended in air to avoid bottom-floor cylinder artifacts.
        int minBottomY = Mathf.Clamp(Mathf.RoundToInt(worldH * 0.10f), 6, Mathf.Max(6, _chunkHeight - 8));

        float core01 = Mathf.Pow(edgeToCenter01, thicknessPower);
        float thickness = Mathf.Lerp(minThickness, maxThickness, core01);

        float edge01 = 1f - edgeToCenter01;
        float rimSlim = Mathf.Pow(edge01, 1.4f);
        thickness -= rimSlim * (worldH * 0.12f);

        float body01 = Mathf.Pow(edgeToCenter01, 2.1f);
        thickness += body01 * (worldH * 0.16f);

        float tip01 = Mathf.Pow(edgeToCenter01, Mathf.Max(1.15f, autoTuneSkyIsland ? autoP.spirePower : skyIslandSpirePower));
        float tipAmplitude = autoTuneSkyIsland ? autoP.spireAmp : skyIslandSpireAmplitude;
        thickness += tip01 * tipAmplitude;

        float undersideNoiseScale = Mathf.Max(0.0001f, autoTuneSkyIsland ? autoP.undersideScale : skyIslandUndersideNoiseScale);
        float undersideNoise = Mathf.PerlinNoise((wx + _seed * 133.7f) * undersideNoiseScale, (wz - _seed * 211.9f) * undersideNoiseScale);
        float undersideNoiseSigned = (undersideNoise - 0.5f) * 2f;
        float undersideNoiseAmp = autoTuneSkyIsland ? autoP.undersideAmp : (worldH * skyIslandUndersideNoise);
        thickness += undersideNoiseSigned * undersideNoiseAmp * Mathf.SmoothStep(0.18f, 1f, edgeToCenter01) * 0.16f;

        float maxSuspendedThickness = Mathf.Max(3f, surfaceY - minBottomY);
        thickness = Mathf.Clamp(thickness, 3f, maxSuspendedThickness);

        int bottomY = surfaceY - Mathf.RoundToInt(thickness);
        bottomY = Mathf.Min(bottomY, surfaceY - 3);
        bottomY = Mathf.Clamp(bottomY, minBottomY, _chunkHeight - 2);
        return bottomY;
    }

    private static void FillColumnNormal(VoxelChunk chunk, int x, int z, int surfaceY)
    {
        for (int y = 0; y < chunk.height; y++)
            chunk.Set(x, y, z, (y < surfaceY) ? VoxelChunk.SOLID : VoxelChunk.AIR);
    }

    private void FillColumnSkyIsland(VoxelChunk chunk, int x, int z, int bottomY, int surfaceY)
    {
        bottomY = Mathf.Max(0, bottomY);
        surfaceY = Mathf.Clamp(surfaceY, 1, chunk.height);

        int grassDepth = Mathf.Max(0, skyIslandSoilDepth);
        int dirtDepth = Mathf.Max(0, skyIslandDirtDepth);

        for (int y = 0; y < chunk.height; y++)
        {
            if (y < bottomY || y >= surfaceY)
            {
                chunk.Set(x, y, z, VoxelChunk.AIR);
                continue;
            }

            int depthFromSurface = (surfaceY - 1) - y;

            byte id =
                (depthFromSurface <= grassDepth) ? VoxelChunk.SOLID :
                (depthFromSurface <= grassDepth + dirtDepth) ? VoxelChunk.DIRT :
                VoxelChunk.STONE;

            chunk.Set(x, y, z, id);
        }
    }

    public void CarveCaveEntrances(VoxelChunk chunk)
    {
        /* keep your existing */
    }
}
