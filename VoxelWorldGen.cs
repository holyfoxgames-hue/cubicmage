using Febucci.UI.Core;
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
    public float mountainHeightBoost = 1.0f;
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
                float edge01 = 1f;
                if (skyIslandMode)
                {
                    dist01 = (islandDistanceSampler01 != null)
                        ? Mathf.Clamp01(islandDistanceSampler01(wp))
                        : 1f;

                    // Robust edge falloff: directly estimate proximity to nearby void.
                    float localEdge01 = EstimateLocalEdge01(wp, biomeSampler01);
                    edge01 = Mathf.Min(dist01, localEdge01);
                }

                if (doStats)
                {
                    distMin = Mathf.Min(distMin, edge01);
                    distMax = Mathf.Max(distMax, edge01);
                }

                int surfaceY = ComputeSurfaceY(wx, wz, w, edge01, autoP);

                if (doStats)
                {
                    surfaceMin = Mathf.Min(surfaceMin, surfaceY);
                    surfaceMax = Mathf.Max(surfaceMax, surfaceY);
                }

                if (skyIslandMode)
                {
                    int bottomY = ComputeSkyIslandBottomY(wx, wz, surfaceY, edge01, autoP);

                    if (doStats)
                    {
                        bottomMin = Mathf.Min(bottomMin, bottomY);
                        bottomMax = Mathf.Max(bottomMax, bottomY);
                        if (bottomY <= 0) bottomLE0++;
                    }

                    FillColumnSkyIsland(chunk, x, z, bottomY, surfaceY, w);
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
                DWarn("[SkyIsland DEBUG] landCols=0 in sampled debug chunk. If island is centered, chunk (0,0) can be all VOID; check a center chunk or disable this warning.");
        }
    }

    private float EstimateLocalEdge01(Vector3 worldPos, Func<Vector3, Vector4> biomeSampler01)
    {
        // Dense radial probe + smooth blend to avoid spoke/banding artifacts.
        const float maxProbe = 72f;
        const float step = 3f;
        const int dirCount = 24;

        float nearestVoid = maxProbe;
        float hitSum = 0f;

        for (int i = 0; i < dirCount; i++)
        {
            float a = (Mathf.PI * 2f * i) / dirCount;
            float dx = Mathf.Cos(a);
            float dz = Mathf.Sin(a);

            float hit = maxProbe;
            for (float r = step; r <= maxProbe; r += step)
            {
                Vector3 p = new Vector3(worldPos.x + dx * r, 0f, worldPos.z + dz * r);
                Vector4 w = biomeSampler01(p);
                float sum = w.x + w.y + w.z + w.w;
                if (sum <= voidThreshold)
                {
                    hit = r;
                    break;
                }
            }

            hitSum += hit;
            if (hit < nearestVoid) nearestVoid = hit;
        }

        float avgHit = hitSum / dirCount;
        float nearest01 = Mathf.Clamp01(nearestVoid / maxProbe);
        float avg01 = Mathf.Clamp01(avgHit / maxProbe);

        // Keep edge responsiveness from nearest hit, but suppress angular signatures.
        return Mathf.Lerp(nearest01, avg01, 0.78f);
    }

    // TOP SURFACE: keep existing biome/noise style with sky-island edge drop
    private int ComputeSurfaceY(int wx, int wz, Vector4 biomeW, float dist01, SkyIslandAutoParams autoP)
    {
        float plains = biomeW.x;
        float hills = biomeW.y;
        float mountains = biomeW.z;
        float plateau = biomeW.w;

        float mountainBoost = Mathf.Max(0.1f, mountainHeightBoost);
        float baseH = 18f * plains + 28f * hills + (46f * mountainBoost) * mountains + 34f * plateau;

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
        float mountainExtra = Mathf.Max(0f, mountainBoost - 1f);
        float mountainBody = Mathf.Pow(mountains, 1.35f) * (8f * mountainExtra);

        float crestNoise = Mathf.PerlinNoise((wx + _seed * 23.4f) * (ns * 1.25f), (wz - _seed * 31.7f) * (ns * 1.25f));
        float mountainCrest = Mathf.Pow(mountains, 2.6f) * (6f * mountainExtra) * (0.70f + 0.60f * crestNoise);
        h += mountainBody + mountainCrest;
        h = Mathf.Lerp(h, baseH, plateau * 0.45f);

        // Break broad contour rings on top surface with subtle domain perturbation.␍␊
        float contourBreak = Mathf.PerlinNoise((wx + _seed * 2.3f) * 0.028f, (wz - _seed * 4.1f) * 0.028f);
        contourBreak = (contourBreak - 0.5f) * 2f;
        h += contourBreak * 0.45f;

        // Stochastic rounding reduces broad contour lines on grass top-surface.
        float hFloor = Mathf.Floor(h);
        float hFrac = h - hFloor;
        float hDither = Mathf.PerlinNoise((wx + _seed * 0.41f) * 0.37f, (wz - _seed * 0.83f) * 0.37f);
        int surfaceY = (int)hFloor + ((hDither < hFrac) ? 1 : 0);
        surfaceY = Mathf.Clamp(surfaceY, 6, _chunkHeight - 2);
        return surfaceY;
    }

    // UNDERSIDE: smoother mass profile with anti-terrace rounding
    private int ComputeSkyIslandBottomY(int wx, int wz, int surfaceY, float dist01, SkyIslandAutoParams autoP)
    {
        float maskCenter01 = Mathf.Clamp01(dist01);

        float cx = _worldSizeX * 0.5f;
        float cz = _worldSizeZ * 0.5f;
        float rx = Mathf.Max(1f, cx - 1f);
        float rz = Mathf.Max(1f, cz - 1f);
        float nx = (wx + 0.5f - cx) / rx;
        float nz = (wz + 0.5f - cz) / rz;
        float radial01 = Mathf.Clamp01(1f - Mathf.Sqrt(nx * nx + nz * nz));

        float edgeToCenter01 = Mathf.Min(maskCenter01, radial01);
        float worldH = _chunkHeight;

        float warpScale = 0.009f;
        float warpX = (Mathf.PerlinNoise((wx + _seed * 19.7f) * warpScale, (wz - _seed * 11.3f) * warpScale) - 0.5f) * 2f;
        float warpZ = (Mathf.PerlinNoise((wx - _seed * 13.1f) * warpScale, (wz + _seed * 23.9f) * warpScale) - 0.5f) * 2f;

        // Break radial rings by perturbing shape-space with low-frequency mass noise.
        float massNoise = Mathf.PerlinNoise((wx + _seed * 5.3f) * 0.0065f, (wz - _seed * 7.1f) * 0.0065f);
        float massSigned = (massNoise - 0.5f) * 2f;
        float warpedCenter01 = Mathf.Clamp01(edgeToCenter01 + (warpX * 0.05f + warpZ * 0.04f) + massSigned * 0.06f);

        // Add broad asymmetric lobes so underside silhouette does not stay perfectly circular.
        float theta = Mathf.Atan2(nz, nx);
        float lobe = Mathf.Sin(theta * 2.0f + _seed * 0.013f) * 0.6f + Mathf.Sin(theta * 3.0f - _seed * 0.009f) * 0.4f;
        float lobeMask = Mathf.SmoothStep(0.28f, 0.92f, warpedCenter01);
        warpedCenter01 = Mathf.Clamp01(warpedCenter01 + lobe * 0.045f * lobeMask);

        float minThicknessCfg = autoTuneSkyIsland ? autoP.minThick : skyIslandMinThickness;
        float maxThicknessCfg = autoTuneSkyIsland ? autoP.maxThick : skyIslandMaxThickness;

        float minThickness = Mathf.Min(minThicknessCfg, 7f);
        float maxThickness = Mathf.Min(maxThicknessCfg, Mathf.Max(minThickness + 16f, surfaceY - 8f));

        // Main body: thicker top shell with smoother center transition.
        float body01 = Mathf.Pow(warpedCenter01, 1.68f);
        float thickness = Mathf.Lerp(minThickness, maxThickness, body01);

        // Add broad macro breakup so the silhouette is less saucer-like.␍␊
        float macroBreak = Mathf.PerlinNoise((wx + _seed * 31.7f) * 0.0042f, (wz - _seed * 17.9f) * 0.0042f);
        macroBreak = (macroBreak - 0.5f) * 2f;
        float macroMask = Mathf.SmoothStep(0.20f, 0.95f, warpedCenter01);
        thickness += macroBreak * (worldH * 0.055f) * macroMask;

        // Keep outer rim thin, but not perfectly radial.
        float rim01 = 1f - warpedCenter01;
        thickness -= Mathf.Pow(rim01, 1.30f) * Mathf.Max(2f, worldH * 0.045f);

        float upperShoulder = Mathf.SmoothStep(0.18f, 0.85f, warpedCenter01);
        thickness += upperShoulder * (worldH * 0.15f);

        // Sharper tail without a hard profile kink.
        float tailMask = Mathf.SmoothStep(0.88f, 1f, warpedCenter01);
        float tailCore = Mathf.Pow(warpedCenter01, 8.4f);
        float tailAmp = (autoTuneSkyIsland ? autoP.spireAmp : skyIslandSpireAmplitude) * 0.14f;
        thickness += tailCore * tailAmp * tailMask;

        float tipCore = Mathf.Pow(warpedCenter01, 13.0f);
        thickness += tipCore * (worldH * 0.042f);

        // Mild breakup to avoid circular contour repetition.
        float s = Mathf.Max(0.0001f, autoTuneSkyIsland ? autoP.undersideScale : skyIslandUndersideNoiseScale);
        float wsx = wx + warpX * 20f;
        float wsz = wz + warpZ * 20f;
        float n0 = Mathf.PerlinNoise((wsx + _seed * 101.3f) * s, (wsz - _seed * 73.1f) * s);
        float n1 = Mathf.PerlinNoise((wsx - _seed * 41.2f) * (s * 1.9f), (wsz + _seed * 29.8f) * (s * 1.9f));
        float ridged = 1f - Mathf.Abs(n1 * 2f - 1f);
        ridged = Mathf.Pow(ridged, 2.0f);

        float nMix = (n0 * 0.58f + ridged * 0.42f);
        float nSigned = (nMix - 0.5f) * 2f;
        float noiseAmp = (autoTuneSkyIsland ? autoP.undersideAmp : (worldH * skyIslandUndersideNoise)) * 0.07f;
        float rockMask = Mathf.SmoothStep(0.22f, 1f, warpedCenter01);
        thickness += nSigned * noiseAmp * rockMask;

        // Anti-terrace stochastic rounding.
        thickness = Mathf.Clamp(thickness, 2f, Mathf.Max(2f, surfaceY - 3f));
        float tFloor = Mathf.Floor(thickness);
        float tFrac = thickness - tFloor;
        float dither = Mathf.PerlinNoise((wx + _seed * 0.77f) * 0.47f, (wz - _seed * 1.17f) * 0.47f);
        float roundedThickness = tFloor + ((dither < tFrac) ? 1f : 0f);

        int bottomY = surfaceY - Mathf.RoundToInt(roundedThickness);
        bottomY = Mathf.Min(bottomY, surfaceY - 3);
        bottomY = Mathf.Clamp(bottomY, 0, _chunkHeight - 2);
        return bottomY;
    }

    private static void FillColumnNormal(VoxelChunk chunk, int x, int z, int surfaceY)
{
    for (int y = 0; y < chunk.height; y++)
        chunk.Set(x, y, z, (y < surfaceY) ? VoxelChunk.SOLID : VoxelChunk.AIR);
}

    private void FillColumnSkyIsland(VoxelChunk chunk, int x, int z, int bottomY, int surfaceY, Vector4 biomeW)
    {
        bottomY = Mathf.Max(0, bottomY);
        surfaceY = Mathf.Clamp(surfaceY, 1, chunk.height);

        int grassDepthCfg = Mathf.Max(1, skyIslandSoilDepth);
        int dirtDepthCfg = Mathf.Max(0, skyIslandDirtDepth);

        // Mountains: no dirt/stone layering, keep full grass/soil shell.
        // Mountains: disable dirt/stone when mountain influence is meaningful (not only dominant case).
        // Dominant-only checks were too strict and still allowed dirt/stone patches in mountain zones.
        float mountainInfluence = Mathf.Clamp01(biomeW.z);
        bool isMountainColumn =
            (mountainInfluence >= 0.30f) &&
            (mountainInfluence >= biomeW.x && mountainInfluence >= biomeW.y && mountainInfluence >= biomeW.w) &&
            (surfaceY > Mathf.RoundToInt(_chunkHeight * 0.50f));

        // Give mountain columns a guaranteed support thickness to avoid underside blank spots.
        if (isMountainColumn)
        {
            int minMountainThickness = Mathf.RoundToInt(Mathf.Lerp(26f, 84f, mountainInfluence));
            bottomY = Mathf.Min(bottomY, surfaceY - minMountainThickness);
            bottomY = Mathf.Max(0, bottomY);
        }

        int columnThickness = Mathf.Max(0, surfaceY - bottomY);

        // Keep material order strict at the rim: grass -> dirt -> stone.
        // If the column is thin, compress layer depths but keep stone starting right below dirt.
        int mountainTopCapDepth = Mathf.Min(columnThickness, Mathf.Max(grassDepthCfg, Mathf.RoundToInt(Mathf.Lerp(8f, 72f, mountainInfluence))));
        int grassDepth = isMountainColumn ? mountainTopCapDepth : Mathf.Min(grassDepthCfg, columnThickness);
        int remainingAfterGrass = Mathf.Max(0, columnThickness - grassDepth);
        int dirtDepth = isMountainColumn ? 0 : Mathf.Min(dirtDepthCfg, remainingAfterGrass);

        if (!isMountainColumn && columnThickness >= 3 && dirtDepth > 0 && (columnThickness - (grassDepth + dirtDepth)) <= 0)
        {
        dirtDepth = Mathf.Max(1, dirtDepth - 1);
    }

    int grassEndDepth = grassDepth;
    int dirtEndDepth = grassEndDepth + dirtDepth;

    for (int y = 0; y < chunk.height; y++)
    {
        if (y < bottomY || y >= surfaceY)
        {
            chunk.Set(x, y, z, VoxelChunk.AIR);
            continue;
        }

        int depthFromSurface = (surfaceY - 1) - y;

        byte id =
            (depthFromSurface == 0) ? VoxelChunk.SOLID :
            (depthFromSurface < grassEndDepth) ? VoxelChunk.SOLID :
            (depthFromSurface < dirtEndDepth) ? VoxelChunk.DIRT :
            VoxelChunk.STONE;

        chunk.Set(x, y, z, id);
    }
}

public void CarveCaveEntrances(VoxelChunk chunk)
    {
        /* keep your existing */
    }
}
