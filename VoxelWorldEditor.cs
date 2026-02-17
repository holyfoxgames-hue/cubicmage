#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;

public class VoxelWorldEditor : EditorWindow
{
    private VoxelWorld world;
    private string savePath = "Assets/WorldSaves/world.cmvw";
    private string meshFolder = "Assets/WorldSaves/Meshes";

    [Header("Clear Safety")]
    private int clearBatchSize = 16;

    // async clear state
    private bool _isClearing;
    private List<GameObject> _clearList;
    private int _clearIndex;

    [MenuItem("CubicMage/Voxel World Tool")]
    public static void Open() => GetWindow<VoxelWorldEditor>("Voxel World Tool");

    private void OnDisable()
    {
        StopAsyncClear();
    }

    private void OnGUI()
    {
        world = (VoxelWorld)EditorGUILayout.ObjectField("VoxelWorld", world, typeof(VoxelWorld), true);

        EditorGUILayout.Space(10);

        savePath = EditorGUILayout.TextField("Binary Save Path", savePath);
        meshFolder = EditorGUILayout.TextField("Mesh Folder", meshFolder);

        EditorGUILayout.Space(8);
        clearBatchSize = EditorGUILayout.IntSlider("Clear Batch Size", clearBatchSize, 1, 128);

        using (new EditorGUI.DisabledScope(!world))
        {
            using (new EditorGUI.DisabledScope(_isClearing))
            {
                if (GUILayout.Button("Generate World (Random Seed)"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(world.gameObject, "Generate World");
                    world.GenerateWorld();
                    EditorUtility.SetDirty(world);
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                }

                if (GUILayout.Button("Clear World (Safe)"))
                {
                    // ✅ 먹통 방지: EditorWindow에서 분할 삭제 실행
                    StartAsyncClear();
                }

                if (GUILayout.Button("Rebuild All Meshes"))
                {
                    world.RebuildAllMeshes();
                    EditorUtility.SetDirty(world);
                }

                EditorGUILayout.Space(8);

                if (GUILayout.Button("Save World Binary"))
                {
                    EnsureFolder(Path.GetDirectoryName(savePath));
                    world.SaveWorldBinary(savePath);
                    AssetDatabase.Refresh();
                }

                if (GUILayout.Button("Load World Binary"))
                {
                    world.LoadWorldBinary(savePath);
                    EditorUtility.SetDirty(world);
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                }

                EditorGUILayout.Space(8);

                if (GUILayout.Button("Save Chunk Meshes As Assets (optional)"))
                {
                    SaveMeshes(world, meshFolder);
                }
            }

            if (_isClearing)
            {
                EditorGUILayout.HelpBox($"Clearing... {_clearIndex}/{_clearList.Count}", MessageType.Info);
                if (GUILayout.Button("Cancel Clear"))
                    StopAsyncClear();
            }
        }
    }

    // ----------------------------
    // Safe Async Clear (Editor)
    // ----------------------------
    private void StartAsyncClear()
    {
        if (!world) return;
        if (_isClearing) return;

        // Undo: only once for root. (registering full hierarchy for thousands can also freeze)
        Undo.RegisterFullObjectHierarchyUndo(world.gameObject, "Clear World");

        // Gather chunk objects under world transform only (fast, predictable)
        _clearList = new List<GameObject>(world.transform.childCount);
        foreach (Transform child in world.transform)
            _clearList.Add(child.gameObject);

        _clearIndex = 0;
        _isClearing = true;

        EditorApplication.update -= AsyncClearStep;
        EditorApplication.update += AsyncClearStep;
    }

    private void StopAsyncClear()
    {
        if (!_isClearing) return;

        EditorApplication.update -= AsyncClearStep;
        EditorUtility.ClearProgressBar();

        _isClearing = false;
        _clearList = null;
        _clearIndex = 0;

        Repaint();
    }

    private void AsyncClearStep()
    {
        if (!world)
        {
            StopAsyncClear();
            return;
        }

        int total = _clearList.Count;
        if (total == 0)
        {
            FinishClear();
            return;
        }

        // progress bar
        float t = Mathf.Clamp01(_clearIndex / (float)total);
        if (EditorUtility.DisplayCancelableProgressBar("Clearing Voxel World", $"{_clearIndex}/{total}", t))
        {
            StopAsyncClear();
            return;
        }

        int processed = 0;
        while (_clearIndex < total && processed < clearBatchSize)
        {
            var go = _clearList[_clearIndex];
            _clearIndex++;
            processed++;

            if (!go) continue;

            // ✅ 핵심: collider/mesh 먼저 끊고 삭제 (프리즈 방지)
            var mc = go.GetComponent<MeshCollider>();
            if (mc) mc.sharedMesh = null;

            var mf = go.GetComponent<MeshFilter>();
            if (mf) mf.sharedMesh = null;

            // DestroyImmediate in editor
            Object.DestroyImmediate(go);
        }

        // finished?
        if (_clearIndex >= total)
        {
            FinishClear();
        }
        else
        {
            // keep UI responsive
            Repaint();
        }
    }

    private void FinishClear()
    {
        // world registry clear (if you have dictionaries/caches)
        // ✅ 가능하면 VoxelWorld에 이런 함수 하나 만들어서 호출하는게 가장 깔끔
        if (world != null)
        {
            world.ClearChunkRegistry_EditorSafe(); // 아래 주석 참고
            EditorUtility.SetDirty(world);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        }

        StopAsyncClear();
    }

    // ----------------------------
    // Utilities
    // ----------------------------
    private static void EnsureFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (!AssetDatabase.IsValidFolder(folder))
        {
            string parent = "Assets";
            foreach (var part in folder.Replace("\\", "/").Split('/'))
            {
                if (part == "Assets") continue;
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(parent, part);
                parent = next;
            }
        }
    }

    private static void SaveMeshes(VoxelWorld world, string folder)
    {
        EnsureFolder(folder);

        foreach (Transform child in world.transform)
        {
            var mf = child.GetComponent<MeshFilter>();
            var chunk = child.GetComponent<VoxelChunk>();
            if (!mf || !chunk || !mf.sharedMesh) continue;

            string path = $"{folder}/Chunk_{chunk.cx}_{chunk.cz}.asset";
            var meshCopy = Object.Instantiate(mf.sharedMesh);
            meshCopy.name = $"Chunk_{chunk.cx}_{chunk.cz}";

            AssetDatabase.CreateAsset(meshCopy, path);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"Saved meshes to: {folder}");
    }
}
#endif
