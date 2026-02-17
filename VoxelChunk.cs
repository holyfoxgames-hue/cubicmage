using System;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class VoxelChunk : MonoBehaviour
{
    public const byte AIR = 0;
    public const byte SOLID = 1;   // grass/topsoil (표면)
    public const byte DIRT = 6;   // ✅ NEW: soil layer
    public const byte ROAD = 2;
    public const byte RIVERBED = 3;
    public const byte WATER = 4;
    public const byte STONE = 5;   // sky-island rock

    [Header("Chunk Meta")]
    public int cx;
    public int cz;

    [HideInInspector] public int size;
    [HideInInspector] public int height;

    [NonSerialized] public VoxelWorld world;

    public byte[,,] voxels;

    private MeshFilter mf;
    private MeshRenderer mr;
    private MeshCollider mc;

    public Vector3 ChunkWorldOrigin => transform.position;

    public void Init(VoxelWorld world, int cx, int cz, int size, int height, Material mat)
    {
        this.world = world;
        this.cx = cx;
        this.cz = cz;
        this.size = size;
        this.height = height;

        voxels = new byte[size, height, size];

        mf = GetComponent<MeshFilter>();
        mr = GetComponent<MeshRenderer>();
        mc = GetComponent<MeshCollider>();

        if (mr && mat) mr.sharedMaterial = mat;
    }

    public byte Get(int x, int y, int z)
    {
        if (x < 0 || x >= size) return AIR;
        if (z < 0 || z >= size) return AIR;
        if (y < 0 || y >= height) return AIR;
        return voxels[x, y, z];
    }

    public void Set(int x, int y, int z, byte v)
    {
        if (x < 0 || x >= size) return;
        if (z < 0 || z >= size) return;
        if (y < 0 || y >= height) return;
        voxels[x, y, z] = v;
    }

    public void BuildMesh()
    {
        if (!mf) mf = GetComponent<MeshFilter>();
        if (!mr) mr = GetComponent<MeshRenderer>();
        if (!mc) mc = GetComponent<MeshCollider>();

        var mesh = VoxelMesher.BuildChunkMesh(this);

        mf.sharedMesh = mesh;
        mc.sharedMesh = mesh;
    }

    // =========================
    // RLE Save/Load
    // =========================
    public void WriteRLE(BinaryWriter bw)
    {
        byte last = voxels[0, 0, 0];
        int run = 0;

        for (int y = 0; y < height; y++)
            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    byte v = voxels[x, y, z];
                    if (v == last && run < int.MaxValue - 1)
                    {
                        run++;
                    }
                    else
                    {
                        bw.Write(run);
                        bw.Write(last);
                        last = v;
                        run = 1;
                    }
                }

        bw.Write(run);
        bw.Write(last);
    }

    public void ReadRLE(BinaryReader br)
    {
        int total = size * size * height;

        int idx = 0;
        while (idx < total)
        {
            int run = br.ReadInt32();
            byte v = br.ReadByte();

            for (int i = 0; i < run && idx < total; i++)
            {
                int y = idx / (size * size);
                int rem = idx - y * size * size;
                int z = rem / size;
                int x = rem - z * size;

                voxels[x, y, z] = v;
                idx++;
            }
        }
    }
}
