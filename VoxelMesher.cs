using System.Collections.Generic;
using UnityEngine;

public static class VoxelMesher
{
    private static readonly Vector3Int[] NeighborDirs =
    {
        new Vector3Int( 1, 0, 0),
        new Vector3Int(-1, 0, 0),
        new Vector3Int( 0, 1, 0),
        new Vector3Int( 0,-1, 0),
        new Vector3Int( 0, 0, 1),
        new Vector3Int( 0, 0,-1),
    };

    private static readonly Vector3[] FaceNormals =
    {
        Vector3.right,
        Vector3.left,
        Vector3.up,
        Vector3.down,
        Vector3.forward,
        Vector3.back
    };

    private static readonly Vector3[][] FaceVerts =
    {
        new [] { new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0) },
        new [] { new Vector3(0,0,1), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1) },
        new [] { new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1) },
        new [] { new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0) },
        new [] { new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1) },
        new [] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0) },
    };

    public static Mesh BuildChunkMesh(VoxelChunk chunk)
    {
        int sx = chunk.size;
        int sy = chunk.height;
        int sz = chunk.size;

        var verts = new List<Vector3>(sx * sy * sz);
        var tris = new List<int>(sx * sy * sz * 6);
        var norms = new List<Vector3>(sx * sy * sz);
        var cols = new List<Color>(sx * sy * sz);

        Vector3 origin = chunk.ChunkWorldOrigin;

        for (int y = 0; y < sy; y++)
        {
            for (int z = 0; z < sz; z++)
            {
                for (int x = 0; x < sx; x++)
                {
                    byte v = chunk.Get(x, y, z);
                    if (v == VoxelChunk.AIR) continue;

                    Vector3 worldPos = new Vector3(origin.x + x, y, origin.z + z);
                    Vector4 bw = chunk.world.SampleBiomeWeights01(worldPos + new Vector3(0.5f, 0f, 0.5f));

                    Color baseColor = chunk.world.ComputeVertexColor(worldPos, bw);
                    baseColor = chunk.world.ApplyBlockTint(baseColor, v, worldPos); // ✅ changed

                    for (int f = 0; f < 6; f++)
                    {
                        Vector3Int d = NeighborDirs[f];
                        int nx = x + d.x;
                        int ny = y + d.y;
                        int nz = z + d.z;

                        byte neighbor;
                        if (nx >= 0 && nx < sx && ny >= 0 && ny < sy && nz >= 0 && nz < sz)
                        {
                            neighbor = chunk.Get(nx, ny, nz);
                        }
                        else
                        {
                            // NOTE: 현재 프로젝트엔 cross-chunk Get이 없었음.
                            // OOB는 AIR로 둠(기존 유지). 막대/기둥 문제를 이걸로 겪는다면 다음 단계에서 cross-chunk 샘플링 넣어야 함.
                            neighbor = VoxelChunk.AIR;
                        }

                        if (neighbor != VoxelChunk.AIR) continue;

                        int vi = verts.Count;
                        var fv = FaceVerts[f];

                        verts.Add(new Vector3(x, y, z) + fv[0]);
                        verts.Add(new Vector3(x, y, z) + fv[1]);
                        verts.Add(new Vector3(x, y, z) + fv[2]);
                        verts.Add(new Vector3(x, y, z) + fv[3]);

                        norms.Add(FaceNormals[f]);
                        norms.Add(FaceNormals[f]);
                        norms.Add(FaceNormals[f]);
                        norms.Add(FaceNormals[f]);

                        cols.Add(baseColor);
                        cols.Add(baseColor);
                        cols.Add(baseColor);
                        cols.Add(baseColor);

                        tris.Add(vi + 0);
                        tris.Add(vi + 2);
                        tris.Add(vi + 1);

                        tris.Add(vi + 0);
                        tris.Add(vi + 3);
                        tris.Add(vi + 2);
                    }
                }
            }
        }

        var mesh = new Mesh();
        mesh.indexFormat = (verts.Count > 65000)
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetNormals(norms);
        mesh.SetColors(cols);
        mesh.RecalculateBounds();

        return mesh;
    }
}
