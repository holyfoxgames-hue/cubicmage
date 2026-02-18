using UnityEngine;

public static class VoxelPostProcess
{
    private const int ROAD_MAX_RAISE = 0;

    public static void ApplyFeatureMaskRoadRiver(VoxelWorld world)
    {
        if (world == null || world.featureMaskRG == null) return;
        if (!world.useFeatureMaskRoadRiver) return;

        for (int wz = 0; wz < world.WorldSizeZ; wz++)
            for (int wx = 0; wx < world.WorldSizeX; wx++)
            {
                if (world.generateAsSkyIsland)
                {
                    var bw = world.SampleBiomeWeights01(new Vector3(wx + 0.5f, 0f, wz + 0.5f));
                    if (bw == Vector4.zero) continue;
                }

                Vector2 f = world.SampleFeatureWeights01(new Vector3(wx + 0.5f, 0f, wz + 0.5f));
                float roadW = f.x;
                float riverW = f.y;

                if (riverW >= world.riverThreshold)
                {
                    if (TryWorldToChunk(world, wx, wz, out var chunk, out int lx, out int lz))
                    {
                        int surfaceY = FindSurfaceY(chunk, lx, lz);

                        float depth = world.riverBedDepth * Mathf.Clamp01(riverW);
                        CarveRiverAt(world,
                            new Vector3(wx + 0.5f, surfaceY, wz + 0.5f),
                            world.riverWidth,
                            depth,
                            world.riverBankWidth,
                            world.riverBankStrength,
                            surfaceY);
                    }
                }

                if (roadW >= world.roadThreshold)
                {
                    int targetY = ComputeAvgSurfaceY_3x3(world, wx, wz, fallback: 1);
                    float flatten = world.roadFlattenStrength * Mathf.Clamp01(roadW);

                    CarveRoadAt(world,
                        new Vector3(wx + 0.5f, targetY, wz + 0.5f),
                        world.roadWidth,
                        world.roadFade,
                        flatten);
                }
            }
    }

    private static void CarveRoadAt(VoxelWorld world, Vector3 p, float width, float fade, float flattenStrength)
    {
        int r = Mathf.CeilToInt(width + fade);
        int x0 = Mathf.FloorToInt(p.x) - r;
        int z0 = Mathf.FloorToInt(p.z) - r;
        int x1 = Mathf.FloorToInt(p.x) + r;
        int z1 = Mathf.FloorToInt(p.z) + r;

        for (int wz = z0; wz <= z1; wz++)
            for (int wx = x0; wx <= x1; wx++)
            {
                float dist = Vector2.Distance(new Vector2(wx + 0.5f, wz + 0.5f), new Vector2(p.x, p.z));
                if (dist > width + fade) continue;

                float core01 = Mathf.InverseLerp(width + fade, width, dist);
                float strength = Mathf.Clamp01(core01) * flattenStrength;

                if (!TryWorldToChunk(world, wx, wz, out var chunk, out int lx, out int lz))
                    continue;

                int surfaceY = FindSurfaceY(chunk, lx, lz);
                int targetY = ComputeAvgSurfaceY_3x3(world, wx, wz, surfaceY);

                int newY = Mathf.RoundToInt(Mathf.Lerp(surfaceY, targetY, strength));
                int maxY = surfaceY + ROAD_MAX_RAISE;
                if (newY > maxY) newY = maxY;

                newY = Mathf.Clamp(newY, 2, chunk.height - 2);

                if (newY < surfaceY)
                {
                    for (int y = newY; y <= surfaceY; y++)
                        chunk.Set(lx, y, lz, VoxelChunk.AIR);
                }
                else if (newY > surfaceY)
                {
                    for (int y = surfaceY; y < newY; y++)
                        chunk.Set(lx, y, lz, VoxelChunk.SOLID);
                }

                chunk.Set(lx, newY - 1, lz, VoxelChunk.ROAD);
            }
    }

    private static int ComputeAvgSurfaceY_3x3(VoxelWorld world, int wx, int wz, int fallback)
    {
        int sum = 0;
        int cnt = 0;

        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = wx + dx;
                int nz = wz + dz;
                if (!TryWorldToChunk(world, nx, nz, out var c2, out int lx2, out int lz2)) continue;
                sum += FindSurfaceY(c2, lx2, lz2);
                cnt++;
            }

        if (cnt <= 0) return fallback;
        return Mathf.RoundToInt(sum / (float)cnt);
    }

    private static void CarveRiverAt(VoxelWorld world, Vector3 p, float width, float bedDepth, float bankWidth, float bankStrength, float waterHeight)
    {
        int r = Mathf.CeilToInt(width + bankWidth);
        int x0 = Mathf.FloorToInt(p.x) - r;
        int z0 = Mathf.FloorToInt(p.z) - r;
        int x1 = Mathf.FloorToInt(p.x) + r;
        int z1 = Mathf.FloorToInt(p.z) + r;

        for (int wz = z0; wz <= z1; wz++)
            for (int wx = x0; wx <= x1; wx++)
            {
                float dist = Vector2.Distance(new Vector2(wx + 0.5f, wz + 0.5f), new Vector2(p.x, p.z));
                if (dist > width + bankWidth) continue;

                float core01 = Mathf.InverseLerp(width + bankWidth, width, dist);
                float bed01 = Mathf.Clamp01(core01);

                if (!TryWorldToChunk(world, wx, wz, out var chunk, out int lx, out int lz))
                    continue;

                int surfaceY = FindSurfaceY(chunk, lx, lz);
                int bedY = Mathf.Clamp(Mathf.RoundToInt(p.y - bedDepth * bed01), 2, chunk.height - 2);

                for (int y = bedY; y <= surfaceY; y++)
                    chunk.Set(lx, y, lz, VoxelChunk.AIR);

                chunk.Set(lx, bedY - 1, lz, VoxelChunk.RIVERBED);

                if (dist > width && dist <= width + bankWidth)
                {
                    float t = Mathf.InverseLerp(width + bankWidth, width, dist);
                    float raise = bankStrength * t * 3f;
                    int bankTop = Mathf.Clamp(surfaceY + Mathf.RoundToInt(raise), 2, chunk.height - 2);
                    for (int y = surfaceY; y < bankTop; y++)
                        chunk.Set(lx, y, lz, VoxelChunk.SOLID);
                }
            }
    }

    private static bool TryWorldToChunk(VoxelWorld world, int wx, int wz, out VoxelChunk chunk, out int lx, out int lz)
    {
        lx = 0; lz = 0; chunk = null;

        if (wx < 0 || wz < 0 || wx >= world.WorldSizeX || wz >= world.WorldSizeZ)
            return false;

        int cx = wx / world.chunkSize;
        int cz = wz / world.chunkSize;

        if (!world.TryGetChunk(cx, cz, out chunk) || chunk == null) return false;

        lx = wx - cx * world.chunkSize;
        lz = wz - cz * world.chunkSize;
        return true;
    }

    private static int FindSurfaceY(VoxelChunk c, int x, int z)
    {
        for (int y = c.height - 2; y >= 1; y--)
        {
            if (c.Get(x, y, z) != VoxelChunk.AIR && c.Get(x, y + 1, z) == VoxelChunk.AIR)
                return y + 1;
        }
        return 1;
    }
}
