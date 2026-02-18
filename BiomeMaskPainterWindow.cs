#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// BiomeMaskPainterWindow
/// - Paint BiomeMaskRGBA (R/G/B/A weights)
/// - Paint FeatureMaskRG (R=Road, G=River)
///
/// Overwrite Painting Mode (FIX):
/// - Painting a biome ALWAYS overwrites: selected->1, others->0 (no mixing / no stacking)
/// - Eraser (Biome) drives all channels toward 0 using MoveTowards (no residuals)
/// - Blur is allowed, but after blur we resolve pixels back to one-hot (overwrite rule)
///
/// Fixes included:
/// - strokeMask reset timing (before first dab)
/// - work/overlay caches correctly use workPixels when editing
/// - removed per-step normalization behavior that fights erasing
/// </summary>
public class BiomeMaskPainterWindow : EditorWindow
{
    private enum PaintLayer
    {
        Plains_R,
        Hills_G,
        Mountains_B,
        Plateau_A,
        Road_R,
        River_G
    }

    // -------------------------
    // Targets
    // -------------------------
    [Header("Targets")]
    [SerializeField] private Texture2D biomeMaskRGBA;
    [SerializeField] private Texture2D featureMaskRG;

    // -------------------------
    // Create New
    // -------------------------
    [Header("Create New")]
    private int newWidth = 1024;
    private int newHeight = 1024;
    private string newBiomePngPath = "Assets/WorldGen/BiomeMasks/BiomeMaskRGBA.png";
    private string newFeaturePngPath = "Assets/WorldGen/BiomeMasks/FeatureMask_RoadRiver.png";

    // -------------------------
    // Brush
    // -------------------------
    [Header("Brush")]
    [SerializeField] private PaintLayer layer = PaintLayer.Plains_R;
    [SerializeField] private bool erase = false;
    [SerializeField] private float brushSize = 40f;
    [SerializeField] private float brushStrength = 0.35f;
    [SerializeField] private float brushHardness = 0.25f;

    // Overwrite mode => these are kept only for UI compatibility, but not used for mixing
    [Header("Biome Mode")]
    [Tooltip("Biome painting is overwrite-only. This toggle is kept for UI but not used.")]
    [SerializeField] private bool conserveSumRGBA = false;

    [Tooltip("Overwrite mode does not need normalization. Keep this OFF.")]
    [SerializeField] private bool autoNormalizeRGBA = false;

    [Tooltip("Biome eraser clears to VOID (0,0,0,0). Recommended.")]
    [SerializeField] private bool biomeEraseToVoid = true;

    [SerializeField] private bool repaintContinuously = true;

    // -------------------------
    // Blur
    // -------------------------
    [Header("Smoothing (MouseUp blur)")]
    [SerializeField] private bool strokeBlurOnMouseUp = true;
    [SerializeField] private int blurKernel = 3; // 3 or 5
    [SerializeField] private float blurStrength = 0.65f;
    [SerializeField] private int blurPasses = 1;

    [Tooltip("In overwrite mode, blur mixes channels. We'll resolve back to one-hot after blur.")]
    [SerializeField] private bool resolveOverwriteAfterBlur = true;

    // -------------------------
    // Overlay
    // -------------------------
    [Header("Overlay (Editor Convenience)")]
    [SerializeField] private bool overlayEnabled = true;
    [SerializeField] private bool overlayShowOtherMask = true;
    [Range(0f, 1f)]
    [SerializeField] private float overlayOpacity = 0.55f;

    [Tooltip("Show cursor sample values")]
    [SerializeField] private bool showCursorSampler = true;

    // -------------------------
    // View
    // -------------------------
    [Header("View (Zoom/Pan)")]
    [SerializeField] private float zoom = 1.0f;
    private const float ZoomMin = 0.10f;
    private const float ZoomMax = 8.00f;
    private const float ViewPadding = 16f;

    // -------------------------
    // Working
    // -------------------------
    private Texture2D targetTex;     // WORK texture (copy)
    private Texture2D targetSrc;     // asset texture currently being edited (biomeMaskRGBA or featureMaskRG)
    private Color[] workPixels;
    private float[] strokeMask;

    private bool isDirty;
    private Vector2 scroll;
    private Vector2 controlsScroll;
    private Rect texRect;
    private double lastApplyTime;

    private bool hasDirtyRect;
    private int dirtyX0, dirtyY0, dirtyX1, dirtyY1;

    private bool isPanning;
    private Vector2 panStartMouse;
    private Vector2 panStartScroll;

    // Feature viz cache (colored + alpha=strength)
    private Texture2D featureVizTex;
    private bool featureVizDirty;
    private double lastVizBuildTime;

    // Opaque display cache
    private Texture2D opaqueDisplayTex;
    private bool opaqueDisplayDirty;

    private const float EPS = 1e-6f;

    [MenuItem("CubicMage/Biome Mask Painter")]
    public static void Open() => GetWindow<BiomeMaskPainterWindow>("Biome Mask Painter");

    private void OnEnable()
    {
        wantsMouseMove = true;
        LoadOrRebuildWorkTexture();
        MarkFeatureVizDirty();
        MarkOpaqueDirty();
    }

    private void OnDisable()
    {
        if (featureVizTex != null) { DestroyImmediate(featureVizTex); featureVizTex = null; }
        if (opaqueDisplayTex != null) { DestroyImmediate(opaqueDisplayTex); opaqueDisplayTex = null; }
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6);

        using (new EditorGUILayout.HorizontalScope())
        {
            // LEFT: controls
            using (var sv = new EditorGUILayout.ScrollViewScope(controlsScroll, GUILayout.Width(380)))
            {
                controlsScroll = sv.scrollPosition;

                DrawTargetsBox();

                if (!HasValidActiveTarget())
                {
                    EditorGUILayout.HelpBox("현재 선택된 레이어에 필요한 텍스처가 없습니다.\n- Biome => BiomeMaskRGBA 필요\n- Feature => FeatureMaskRG 필요", MessageType.Info);
                    return;
                }

                DrawOverlayBox();
                DrawLegend();
                DrawViewBox();

                WarnIfSizeMismatch();
            }

            // RIGHT: canvas
            using (new EditorGUILayout.VerticalScope())
            {
                if (targetTex == null || workPixels == null)
                {
                    EditorGUILayout.HelpBox("Target texture not loaded.", MessageType.Info);
                    return;
                }

                EditorGUILayout.Space(2);

                // Paint UI on right
                DrawPaintBox();

                scroll = EditorGUILayout.BeginScrollView(scroll);

                float drawW = targetTex.width * zoom;
                float drawH = targetTex.height * zoom;

                Rect contentRect = GUILayoutUtility.GetRect(drawW + ViewPadding * 2f, drawH + ViewPadding * 2f, GUILayout.ExpandWidth(true));
                texRect = new Rect(contentRect.x + ViewPadding, contentRect.y + ViewPadding, drawW, drawH);

                DrawComposite(texRect);

                HandleViewEvents(contentRect);
                HandlePaintEvents(texRect);

                EditorGUILayout.EndScrollView();

                // Preview update throttle (does NOT write to disk)
                if (isDirty && EditorApplication.timeSinceStartup - lastApplyTime > 0.10)
                {
                    targetTex.SetPixels(workPixels);
                    targetTex.Apply(false, false);

                    MarkOpaqueDirty();

                    if (!IsBiomeLayer(layer))
                        MarkFeatureVizDirty();

                    lastApplyTime = EditorApplication.timeSinceStartup;
                    Repaint();
                }

                if (showCursorSampler)
                    DrawCursorSampleBar();

                if (repaintContinuously) Repaint();
            }
        }
    }

    // =========================
    // UI
    // =========================
    private void DrawTargetsBox()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Targets", EditorStyles.boldLabel);

            var newBiome = (Texture2D)EditorGUILayout.ObjectField("BiomeMaskRGBA", biomeMaskRGBA, typeof(Texture2D), false);
            if (newBiome != biomeMaskRGBA)
            {
                biomeMaskRGBA = newBiome;
                LoadOrRebuildWorkTexture();
                MarkFeatureVizDirty();
            }

            var newFeature = (Texture2D)EditorGUILayout.ObjectField("FeatureMask (R=Road, G=River)", featureMaskRG, typeof(Texture2D), false);
            if (newFeature != featureMaskRG)
            {
                featureMaskRG = newFeature;
                LoadOrRebuildWorkTexture();
                MarkFeatureVizDirty();
            }

            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Create New PNGs", EditorStyles.boldLabel);
            newWidth = EditorGUILayout.IntField("Width", newWidth);
            newHeight = EditorGUILayout.IntField("Height", newHeight);
            newBiomePngPath = EditorGUILayout.TextField("Biome PNG Path", newBiomePngPath);
            newFeaturePngPath = EditorGUILayout.TextField("Feature PNG Path", newFeaturePngPath);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Create Biome RGBA (and Load)"))
                {
                    // Start as VOID so you can paint sky-island silhouette.
                    CreateNewPngRGBA(newBiomePngPath, newWidth, newHeight, new Color(0, 0, 0, 0));
                    AssetDatabase.Refresh();
                    biomeMaskRGBA = AssetDatabase.LoadAssetAtPath<Texture2D>(newBiomePngPath);
                    LoadOrRebuildWorkTexture();
                    MarkFeatureVizDirty();
                }

                if (GUILayout.Button("Create Feature RG (and Load)"))
                {
                    CreateNewPngRGBA(newFeaturePngPath, newWidth, newHeight, new Color(0, 0, 0, 0));
                    AssetDatabase.Refresh();
                    featureMaskRG = AssetDatabase.LoadAssetAtPath<Texture2D>(newFeaturePngPath);
                    LoadOrRebuildWorkTexture();
                    MarkFeatureVizDirty();
                }
            }
        }
    }

    private void DrawPaintBox()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Paint", EditorStyles.boldLabel);

            var newLayer = (PaintLayer)EditorGUILayout.EnumPopup("Layer", layer);
            if (newLayer != layer)
            {
                layer = newLayer;
                LoadOrRebuildWorkTexture();
                MarkFeatureVizDirty();
            }

            erase = EditorGUILayout.Toggle("Erase (toward 0)", erase);

            brushSize = EditorGUILayout.Slider("Brush Size", brushSize, 1f, 256f);
            brushStrength = EditorGUILayout.Slider("Strength", brushStrength, 0.01f, 1f);
            brushHardness = EditorGUILayout.Slider("Hardness", brushHardness, 0f, 1f);

            EditorGUILayout.Space(6);

            if (IsBiomeLayer(layer))
            {
                EditorGUILayout.LabelField("Biome Mode (Overwrite)", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Biome painting is OVERWRITE-only:\n- Paint: selected -> 1, others -> 0\n- Erase: -> VOID\n(Prevents layer stacking / blending bugs)",
                    MessageType.None
                );
                biomeEraseToVoid = EditorGUILayout.Toggle("Biome Erase => VOID", biomeEraseToVoid);

                autoNormalizeRGBA = false;
                conserveSumRGBA = false;
            }
            else
            {
                EditorGUILayout.HelpBox("FeatureMask는 합=1 규칙 없음. 각 채널 0~1 독립.", MessageType.None);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Smoothing (MouseUp blur)", EditorStyles.boldLabel);
            strokeBlurOnMouseUp = EditorGUILayout.Toggle("Stroke Blur On MouseUp", strokeBlurOnMouseUp);

            using (new EditorGUI.DisabledScope(!strokeBlurOnMouseUp))
            {
                blurKernel = EditorGUILayout.IntPopup("Blur Kernel", blurKernel, new[] { "3x3", "5x5" }, new[] { 3, 5 });
                blurStrength = EditorGUILayout.Slider("Blur Strength", blurStrength, 0f, 1f);
                blurPasses = EditorGUILayout.IntSlider("Blur Passes", blurPasses, 1, 2);
                resolveOverwriteAfterBlur = EditorGUILayout.Toggle("Resolve Overwrite After Blur", resolveOverwriteAfterBlur);
            }

            repaintContinuously = EditorGUILayout.Toggle("Continuous Repaint", repaintContinuously);

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (IsBiomeLayer(layer))
                {
                    if (GUILayout.Button("Fill Default (Plains)")) FillAll(new Color(1, 0, 0, 0));
                    if (GUILayout.Button("Clear (All 0)")) FillAll(new Color(0, 0, 0, 0));
                }
                else
                {
                    if (GUILayout.Button("Clear Features (All 0)")) FillAll(new Color(0, 0, 0, 0));
                    if (GUILayout.Button("Road=1 (R)")) FillAll(new Color(1, 0, 0, 0));
                    if (GUILayout.Button("River=1 (G)")) FillAll(new Color(0, 1, 0, 0));
                }
            }

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply Now")) ApplyToDiskAndReimport();

                using (new EditorGUI.DisabledScope(!isDirty))
                {
                    if (GUILayout.Button("Save PNG (Apply + Reimport)")) ApplyToDiskAndReimport();
                }
            }
        }
    }

    private void DrawOverlayBox()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Overlay", EditorStyles.boldLabel);

            overlayEnabled = EditorGUILayout.Toggle("Enable Overlay", overlayEnabled);
            using (new EditorGUI.DisabledScope(!overlayEnabled))
            {
                overlayShowOtherMask = EditorGUILayout.Toggle("Show Other Mask", overlayShowOtherMask);
                overlayOpacity = EditorGUILayout.Slider("Overlay Opacity", overlayOpacity, 0f, 1f);
                showCursorSampler = EditorGUILayout.Toggle("Cursor Sampler", showCursorSampler);
            }
        }
    }

    private void DrawLegend()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Legend", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("BiomeMaskRGBA: Plains(R) / Hills(G) / Mountains(B) / Plateau(A)");
            EditorGUILayout.LabelField("FeatureMaskRG: Road(R) / River(G)  (Viz: Road=Red, River=Blue, Alpha=strength)");
        }
    }

    private void DrawViewBox()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("View", EditorStyles.boldLabel);

            zoom = EditorGUILayout.Slider("Zoom", zoom, ZoomMin, ZoomMax);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("100%", GUILayout.Width(60))) zoom = 1f;
                if (GUILayout.Button("Fit", GUILayout.Width(60))) FitToWindow();
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Wheel: Zoom | MMB Drag / Alt+LMB: Pan", GUILayout.Width(260));
            }
        }
    }

    // =========================
    // DISPLAY
    // =========================
    private void DrawComposite(Rect r)
    {
        Texture2D baseTex = GetBaseDisplayTexture();
        if (baseTex == null) return;

        Texture2D disp = GetOpaqueDisplay(baseTex, useWork: (baseTex == targetTex));
        GUI.DrawTexture(r, disp, ScaleMode.StretchToFill, alphaBlend: false);

        if (!overlayEnabled || !overlayShowOtherMask) return;

        Texture2D ov = GetOverlayDisplayTexture();
        if (ov == null) return;

        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, overlayOpacity);
        GUI.DrawTexture(r, ov, ScaleMode.StretchToFill, alphaBlend: true);
        GUI.color = prev;
    }

    private Texture2D GetBaseDisplayTexture()
    {
        // Feature 편집 중: Base는 무조건 Biome (원본 표시)
        if (!IsBiomeLayer(layer) && biomeMaskRGBA != null)
        {
            EnsureReadable(biomeMaskRGBA);
            return biomeMaskRGBA;
        }

        // Biome 편집 중: Base는 work
        return targetTex;
    }

    private Texture2D GetOverlayDisplayTexture()
    {
        if (IsBiomeLayer(layer))
        {
            if (featureMaskRG == null) return null;
            EnsureReadable(featureMaskRG);
            return GetFeatureViz(useWork: false);
        }

        // Feature 편집 중: overlay는 feature viz (work)
        return GetFeatureViz(useWork: true);
    }

    private void MarkFeatureVizDirty() => featureVizDirty = true;
    private void MarkOpaqueDirty() => opaqueDisplayDirty = true;

    private Texture2D GetFeatureViz(bool useWork)
    {
        if (EditorApplication.timeSinceStartup - lastVizBuildTime > 0.06f && featureVizDirty)
        {
            BuildOrUpdateFeatureViz(useWork);
            featureVizDirty = false;
            lastVizBuildTime = EditorApplication.timeSinceStartup;
        }

        if (featureVizTex == null)
        {
            BuildOrUpdateFeatureViz(useWork);
            featureVizDirty = false;
            lastVizBuildTime = EditorApplication.timeSinceStartup;
        }

        return featureVizTex;
    }

    private void BuildOrUpdateFeatureViz(bool useWork)
    {
        if (featureMaskRG == null) return;
        EnsureReadable(featureMaskRG);

        Texture2D src = featureMaskRG;
        int w = src.width;
        int h = src.height;

        if (featureVizTex == null || featureVizTex.width != w || featureVizTex.height != h)
        {
            if (featureVizTex != null) DestroyImmediate(featureVizTex);
            featureVizTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "FeatureViz",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }

        // ✅ FIX: useWork일 때는 '현재 편집중인 대상이 featureMaskRG인 경우' workPixels를 사용
        Color[] srcPx = (useWork && targetSrc == featureMaskRG && workPixels != null)
            ? workPixels
            : src.GetPixels();

        var vizPx = new Color[srcPx.Length];

        for (int i = 0; i < srcPx.Length; i++)
        {
            float road = Mathf.Clamp01(srcPx[i].r);
            float river = Mathf.Clamp01(srcPx[i].g);
            float a = Mathf.Max(road, river);
            vizPx[i] = new Color(road, 0f, river, a);
        }

        featureVizTex.SetPixels(vizPx);
        featureVizTex.Apply(false, false);
    }

    private Texture2D GetOpaqueDisplay(Texture2D src, bool useWork)
    {
        if (src == null) return null;

        int w = src.width;
        int h = src.height;

        if (opaqueDisplayTex == null || opaqueDisplayTex.width != w || opaqueDisplayTex.height != h)
        {
            if (opaqueDisplayTex != null) DestroyImmediate(opaqueDisplayTex);
            opaqueDisplayTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "OpaqueDisplay",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            opaqueDisplayDirty = true;
        }

        if (opaqueDisplayDirty)
        {
            // ✅ FIX: work 표시가 필요하면 workPixels를 사용
            Color[] srcPx = (useWork && workPixels != null && src == targetTex)
                ? workPixels
                : src.GetPixels();

            var dispPx = new Color[srcPx.Length];
            for (int i = 0; i < srcPx.Length; i++)
            {
                Color c = srcPx[i];
                c.a = 1f;
                dispPx[i] = c;
            }

            opaqueDisplayTex.SetPixels(dispPx);
            opaqueDisplayTex.Apply(false, false);
            opaqueDisplayDirty = false;
        }

        return opaqueDisplayTex;
    }

    private void WarnIfSizeMismatch()
    {
        if (biomeMaskRGBA == null || featureMaskRG == null) return;
        if (biomeMaskRGBA.width == featureMaskRG.width && biomeMaskRGBA.height == featureMaskRG.height) return;

        EditorGUILayout.HelpBox(
            $"BiomeMaskRGBA({biomeMaskRGBA.width}x{biomeMaskRGBA.height}) 와 FeatureMaskRG({featureMaskRG.width}x{featureMaskRG.height}) 해상도가 다릅니다.\n" +
            "오버레이는 표시되지만 좌표 의미가 어긋날 수 있어요. 가능하면 같은 해상도로 맞추는 것을 권장합니다.",
            MessageType.Warning
        );
    }

    // =========================
    // VIEW
    // =========================
    private void FitToWindow()
    {
        if (targetTex == null) return;

        float viewW = Mathf.Max(64f, position.width - 50f);
        float viewH = Mathf.Max(64f, position.height - 620f);

        float zx = viewW / Mathf.Max(1f, targetTex.width);
        float zy = viewH / Mathf.Max(1f, targetTex.height);
        zoom = Mathf.Clamp(Mathf.Min(zx, zy), ZoomMin, ZoomMax);
    }

    private static bool IsPanGesture(Event e)
    {
        if (e.button == 2) return true;
        if (e.button == 0 && e.alt) return true;
        return false;
    }

    private void HandleViewEvents(Rect contentRect)
    {
        Event e = Event.current;
        if (e == null) return;

        bool overContent = contentRect.Contains(e.mousePosition);

        if (overContent && e.type == EventType.MouseDown && IsPanGesture(e))
        {
            isPanning = true;
            panStartMouse = e.mousePosition;
            panStartScroll = scroll;
            e.Use();
            return;
        }

        if (isPanning && e.type == EventType.MouseDrag)
        {
            Vector2 delta = e.mousePosition - panStartMouse;
            scroll = panStartScroll - delta;
            e.Use();
            Repaint();
            return;
        }

        if (isPanning && (e.type == EventType.MouseUp || e.type == EventType.MouseLeaveWindow))
        {
            isPanning = false;
            e.Use();
            return;
        }

        if (overContent && e.type == EventType.ScrollWheel)
        {
            if (!texRect.Contains(e.mousePosition)) return;

            float oldZoom = zoom;
            float factor = Mathf.Exp(-e.delta.y * 0.08f);
            zoom = Mathf.Clamp(zoom * factor, ZoomMin, ZoomMax);

            if (!Mathf.Approximately(oldZoom, zoom))
            {
                Vector2 mouse = e.mousePosition;
                Vector2 localOld = mouse - texRect.position;
                float u = (texRect.width > 1f) ? (localOld.x / texRect.width) : 0f;
                float v = (texRect.height > 1f) ? (localOld.y / texRect.height) : 0f;

                float newW = targetTex.width * zoom;
                float newH = targetTex.height * zoom;

                Vector2 localNew = new Vector2(u * newW, v * newH);
                Vector2 deltaLocal = localNew - localOld;

                scroll += deltaLocal;

                e.Use();
                Repaint();
            }
        }
    }

    // =========================
    // PAINT EVENTS
    // =========================
    private void HandlePaintEvents(Rect r)
    {
        Event e = Event.current;
        if (e == null) return;

        if (isPanning || IsPanGesture(e)) return;
        if (!r.Contains(e.mousePosition)) return;

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            e.Use();
            ResetDirtyRect();

            // ✅ FIX: strokeMask must be cleared BEFORE first dab
            if (strokeMask != null) System.Array.Clear(strokeMask, 0, strokeMask.Length);

            PaintAt(e.mousePosition);
        }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        {
            e.Use();
            PaintAt(e.mousePosition);
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            e.Use();

            if (strokeBlurOnMouseUp && hasDirtyRect)
            {
                int pad = Mathf.CeilToInt((brushSize * 0.5f) + (blurKernel * 2));
                int bx0 = Mathf.Clamp(dirtyX0 - pad, 0, targetTex.width - 1);
                int bx1 = Mathf.Clamp(dirtyX1 + pad, 0, targetTex.width - 1);
                int by0 = Mathf.Clamp(dirtyY0 - pad, 0, targetTex.height - 1);
                int by1 = Mathf.Clamp(dirtyY1 + pad, 0, targetTex.height - 1);

                LocalBoxBlur(bx0, by0, bx1, by1, blurKernel, blurPasses, blurStrength);

                // ✅ FIX: overwrite 모드에서 blur는 섞기 => one-hot 규칙으로 다시 정리
                if (IsBiomeLayer(layer) && resolveOverwriteAfterBlur)
                    ResolveBiomeOverwriteRect(bx0, by0, bx1, by1);

                ClearMaskRegion(bx0, by0, bx1, by1);
            }

            targetTex.SetPixels(workPixels);
            targetTex.Apply(false, false);

            MarkOpaqueDirty();
            if (!IsBiomeLayer(layer))
                MarkFeatureVizDirty();

            if (hasDirtyRect) ClearMaskRegion(dirtyX0, dirtyY0, dirtyX1, dirtyY1);

            // Write to disk on mouse up (current behavior)
            ApplyToDiskAndReimport();
        }
    }

    private void ResetDirtyRect()
    {
        hasDirtyRect = false;
        dirtyX0 = dirtyY0 = int.MaxValue;
        dirtyX1 = dirtyY1 = int.MinValue;
    }

    private void ExpandDirtyRect(int x0, int y0, int x1, int y1)
    {
        if (!hasDirtyRect)
        {
            hasDirtyRect = true;
            dirtyX0 = x0; dirtyY0 = y0; dirtyX1 = x1; dirtyY1 = y1;
        }
        else
        {
            dirtyX0 = Mathf.Min(dirtyX0, x0);
            dirtyY0 = Mathf.Min(dirtyY0, y0);
            dirtyX1 = Mathf.Max(dirtyX1, x1);
            dirtyY1 = Mathf.Max(dirtyY1, y1);
        }
    }

    private void ClearMaskRegion(int x0, int y0, int x1, int y1)
    {
        if (strokeMask == null || targetTex == null) return;

        int w = targetTex.width;
        int h = targetTex.height;

        x0 = Mathf.Clamp(x0, 0, w - 1);
        x1 = Mathf.Clamp(x1, 0, w - 1);
        y0 = Mathf.Clamp(y0, 0, h - 1);
        y1 = Mathf.Clamp(y1, 0, h - 1);

        for (int y = y0; y <= y1; y++)
        {
            int row = y * w;
            for (int x = x0; x <= x1; x++)
                strokeMask[row + x] = 0f;
        }
    }

    private void PaintAt(Vector2 mousePos)
    {
        if (targetTex == null || workPixels == null) return;

        // Convert mouse->UV (note: v inverted due to GUI y-down)
        float u = Mathf.InverseLerp(texRect.xMin, texRect.xMax, mousePos.x);
        float v = Mathf.InverseLerp(texRect.yMax, texRect.yMin, mousePos.y);

        int cx = Mathf.Clamp(Mathf.RoundToInt(u * (targetTex.width - 1)), 0, targetTex.width - 1);
        int cy = Mathf.Clamp(Mathf.RoundToInt(v * (targetTex.height - 1)), 0, targetTex.height - 1);

        int rad = Mathf.CeilToInt(brushSize * 0.5f);
        int x0 = Mathf.Clamp(cx - rad, 0, targetTex.width - 1);
        int x1 = Mathf.Clamp(cx + rad, 0, targetTex.width - 1);
        int y0 = Mathf.Clamp(cy - rad, 0, targetTex.height - 1);
        int y1 = Mathf.Clamp(cy + rad, 0, targetTex.height - 1);

        ExpandDirtyRect(x0, y0, x1, y1);

        float radSqr = Mathf.Max(1f, rad * rad);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float d2 = dx * dx + dy * dy;
                if (d2 > radSqr) continue;

                float dist01 = Mathf.Sqrt(d2) / Mathf.Max(1f, rad);
                float falloff = Mathf.Clamp01(1f - dist01);

                float soft = Mathf.SmoothStep(0f, 1f, falloff);
                float hard = falloff;
                float f = Mathf.Lerp(soft, hard, brushHardness);

                int idx = y * targetTex.width + x;

                if (strokeMask != null)
                    strokeMask[idx] = Mathf.Max(strokeMask[idx], f);

                float k = Mathf.Clamp01(brushStrength * f);

                Color c = workPixels[idx];
                if (IsBiomeLayer(layer)) c = PaintBiomeOverwrite(c, layer, erase, k);
                else c = PaintFeature(c, layer, erase, k);

                workPixels[idx] = c;
            }
        }

        isDirty = true;
        MarkOpaqueDirty();
        if (!IsBiomeLayer(layer)) MarkFeatureVizDirty();
    }

    // =========================
    // CURSOR SAMPLER
    // =========================
    private void DrawCursorSampleBar()
    {
        Event e = Event.current;
        if (e == null) return;
        if (!texRect.Contains(e.mousePosition)) return;

        float u = Mathf.InverseLerp(texRect.xMin, texRect.xMax, e.mousePosition.x);
        float v = Mathf.InverseLerp(texRect.yMax, texRect.yMin, e.mousePosition.y);

        int px = Mathf.Clamp(Mathf.RoundToInt(u * (targetTex.width - 1)), 0, targetTex.width - 1);
        int py = Mathf.Clamp(Mathf.RoundToInt(v * (targetTex.height - 1)), 0, targetTex.height - 1);

        string biomeStr = "Biome: (none)";
        if (biomeMaskRGBA != null)
        {
            EnsureReadable(biomeMaskRGBA);
            Color bc = biomeMaskRGBA.GetPixel(
                Mathf.Clamp(Mathf.RoundToInt(u * (biomeMaskRGBA.width - 1)), 0, biomeMaskRGBA.width - 1),
                Mathf.Clamp(Mathf.RoundToInt(v * (biomeMaskRGBA.height - 1)), 0, biomeMaskRGBA.height - 1)
            );
            biomeStr = $"Biome RGBA: P={bc.r:0.000} H={bc.g:0.000} M={bc.b:0.000} Plt={bc.a:0.000} (sum={(bc.r + bc.g + bc.b + bc.a):0.000})";
        }

        string featStr = "Feature: (none)";
        if (featureMaskRG != null)
        {
            EnsureReadable(featureMaskRG);
            Color fc = featureMaskRG.GetPixel(
                Mathf.Clamp(Mathf.RoundToInt(u * (featureMaskRG.width - 1)), 0, featureMaskRG.width - 1),
                Mathf.Clamp(Mathf.RoundToInt(v * (featureMaskRG.height - 1)), 0, featureMaskRG.height - 1)
            );
            featStr = $"Feature RG: Road={fc.r:0.000} River={fc.g:0.000}";
        }

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField($"Cursor Pixel: ({px},{py})", EditorStyles.miniBoldLabel);
        EditorGUILayout.LabelField(biomeStr, EditorStyles.miniLabel);
        EditorGUILayout.LabelField(featStr, EditorStyles.miniLabel);
        EditorGUILayout.EndVertical();
    }

    // =========================
    // BLUR
    // =========================
    private void LocalBoxBlur(int x0, int y0, int x1, int y1, int kernel, int passes, float strength)
    {
        int w = targetTex.width;
        int rw = (x1 - x0) + 1;
        int rh = (y1 - y0) + 1;

        var region = new Color[rw * rh];

        for (int yy = 0; yy < rh; yy++)
        {
            int sy = y0 + yy;
            for (int xx = 0; xx < rw; xx++)
            {
                int sx = x0 + xx;
                region[yy * rw + xx] = workPixels[sy * w + sx];
            }
        }

        int rad = (kernel == 5) ? 2 : 1;

        for (int pass = 0; pass < passes; pass++)
        {
            var tmp = new Color[region.Length];

            for (int yy = 0; yy < rh; yy++)
            {
                for (int xx = 0; xx < rw; xx++)
                {
                    Color sum = Color.black;
                    int cnt = 0;

                    for (int ky = -rad; ky <= rad; ky++)
                    {
                        int y = yy + ky;
                        if (y < 0 || y >= rh) continue;

                        for (int kx = -rad; kx <= rad; kx++)
                        {
                            int x = xx + kx;
                            if (x < 0 || x >= rw) continue;

                            sum += region[y * rw + x];
                            cnt++;
                        }
                    }

                    tmp[yy * rw + xx] = (cnt > 0) ? (sum / cnt) : region[yy * rw + xx];
                }
            }

            region = tmp;
        }

        for (int yy = 0; yy < rh; yy++)
        {
            int sy = y0 + yy;
            int dstRow = sy * w;
            int srcRow = yy * rw;

            for (int xx = 0; xx < rw; xx++)
            {
                int di = dstRow + (x0 + xx);

                float m01 = strokeMask != null ? Mathf.Clamp01(strokeMask[di]) : 1f;
                if (m01 <= 0f) continue;

                // smooth mask
                m01 = m01 * m01 * (3f - 2f * m01);

                Color orig = workPixels[di];
                Color blurred = region[srcRow + xx];

                float k = strength * m01;
                Color mixed = Color.Lerp(orig, blurred, k);

                if (!IsBiomeLayer(layer))
                {
                    mixed = Clamp01(mixed);
                    mixed.b = 0f;
                    mixed.a = 0f;
                }
                else
                {
                    mixed = Clamp01(mixed);
                }

                workPixels[di] = mixed;
            }
        }

        isDirty = true;
        MarkOpaqueDirty();
    }

    /// <summary>
    /// After blur, enforce overwrite rule:
    /// - if sum is small -> void
    /// - else choose dominant channel and set one-hot (1,0,0,0) etc
    /// This removes the "separated red islands / yellow-green bands" after erase+paint.
    /// </summary>
    private void ResolveBiomeOverwriteRect(int x0, int y0, int x1, int y1)
    {
        int w = targetTex.width;
        int h = targetTex.height;

        x0 = Mathf.Clamp(x0, 0, w - 1);
        x1 = Mathf.Clamp(x1, 0, w - 1);
        y0 = Mathf.Clamp(y0, 0, h - 1);
        y1 = Mathf.Clamp(y1, 0, h - 1);

        const float voidThreshold = 0.02f; // treat tiny sum as void
        const float snap = 0.00075f;

        for (int y = y0; y <= y1; y++)
        {
            int row = y * w;
            for (int x = x0; x <= x1; x++)
            {
                int i = row + x;
                Color c = workPixels[i];

                float sum = c.r + c.g + c.b + c.a;
                if (sum < voidThreshold)
                {
                    workPixels[i] = new Color(0, 0, 0, 0);
                    continue;
                }

                float r = c.r, g = c.g, b = c.b, a = c.a;
                if (r < snap) r = 0f;
                if (g < snap) g = 0f;
                if (b < snap) b = 0f;
                if (a < snap) a = 0f;

                int maxCh = 0;
                float maxV = r;
                if (g > maxV) { maxV = g; maxCh = 1; }
                if (b > maxV) { maxV = b; maxCh = 2; }
                if (a > maxV) { maxV = a; maxCh = 3; }
                float keep = Mathf.Clamp01(maxV);
                workPixels[i] =
                    (maxCh == 0) ? new Color(keep, 0, 0, 0) :
                    (maxCh == 1) ? new Color(0, keep, 0, 0) :
                    (maxCh == 2) ? new Color(0, 0, keep, 0) :
                                   new Color(0, 0, 0, keep);
            }
        }

        isDirty = true;
        MarkOpaqueDirty();
    }

    // =========================
    // LOAD / SAVE
    // =========================
    private bool HasValidActiveTarget()
        => IsBiomeLayer(layer) ? biomeMaskRGBA != null : featureMaskRG != null;

    private void LoadOrRebuildWorkTexture()
    {
        targetTex = null;
        targetSrc = null;
        workPixels = null;
        isDirty = false;
        ResetDirtyRect();

        Texture2D src = IsBiomeLayer(layer) ? biomeMaskRGBA : featureMaskRG;
        if (src == null) return;

        EnsureReadable(src);

        targetSrc = src;

        targetTex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true)
        {
            name = src.name + "_WORK",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var pixels = src.GetPixels();
        targetTex.SetPixels(pixels);
        targetTex.Apply(false, false);

        workPixels = targetTex.GetPixels();
        strokeMask = new float[workPixels.Length];

        MarkFeatureVizDirty();
        MarkOpaqueDirty();
    }

    private void FillAll(Color c)
    {
        if (targetTex == null || workPixels == null) return;

        Color fill = c;

        if (IsBiomeLayer(layer))
        {
            fill = Clamp01(fill);
            // overwrite mode: allow void or one-hot, do not normalize
        }
        else
        {
            fill = Clamp01(fill);
            fill.b = 0f; fill.a = 0f;
        }

        for (int i = 0; i < workPixels.Length; i++)
            workPixels[i] = fill;

        isDirty = true;

        targetTex.SetPixels(workPixels);
        targetTex.Apply(false, false);

        MarkOpaqueDirty();
        MarkFeatureVizDirty();
        ApplyToDiskAndReimport();
    }

    private void ApplyToDiskAndReimport()
    {
        if (targetTex == null) return;

        Texture2D realTarget = IsBiomeLayer(layer) ? biomeMaskRGBA : featureMaskRG;
        if (realTarget == null) return;

        string path = AssetDatabase.GetAssetPath(realTarget);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError("Target texture path is invalid.");
            return;
        }

        // write png from WORK
        byte[] png = targetTex.EncodeToPNG();
        File.WriteAllBytes(path, png);

        AssetDatabase.ImportAsset(path);

        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null)
        {
            ti.textureType = TextureImporterType.Default;
            ti.alphaIsTransparency = false;
#if UNITY_2021_2_OR_NEWER
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
#endif
            ti.sRGBTexture = false;
            ti.isReadable = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SaveAndReimport();
        }

        // reload asset ref
        if (IsBiomeLayer(layer))
            biomeMaskRGBA = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        else
            featureMaskRG = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

        // rebuild work from asset to avoid drift
        LoadOrRebuildWorkTexture();
        isDirty = false;

        MarkFeatureVizDirty();
        MarkOpaqueDirty();
        Repaint();
    }

    private static void EnsureReadable(Texture2D tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path)) return;

        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return;

        bool changed = false;

        if (ti.alphaIsTransparency) { ti.alphaIsTransparency = false; changed = true; }
#if UNITY_2021_2_OR_NEWER
        if (ti.alphaSource != TextureImporterAlphaSource.FromInput) { ti.alphaSource = TextureImporterAlphaSource.FromInput; changed = true; }
#endif
        if (!ti.isReadable) { ti.isReadable = true; changed = true; }
        if (ti.mipmapEnabled) { ti.mipmapEnabled = false; changed = true; }
        if (ti.textureCompression != TextureImporterCompression.Uncompressed) { ti.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
        if (ti.wrapMode != TextureWrapMode.Clamp) { ti.wrapMode = TextureWrapMode.Clamp; changed = true; }
        if (ti.filterMode != FilterMode.Bilinear) { ti.filterMode = FilterMode.Bilinear; changed = true; }
        if (ti.sRGBTexture) { ti.sRGBTexture = false; changed = true; }

        if (changed) ti.SaveAndReimport();
    }

    private static void CreateNewPngRGBA(string path, int w, int h, Color fill)
    {
        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var arr = new Color[w * h];
        for (int i = 0; i < arr.Length; i++) arr[i] = fill;

        tex.SetPixels(arr);
        tex.Apply(false, false);

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }

    // =========================
    // PAINT LOGIC
    // =========================
    private static bool IsBiomeLayer(PaintLayer l)
        => l == PaintLayer.Plains_R || l == PaintLayer.Hills_G || l == PaintLayer.Mountains_B || l == PaintLayer.Plateau_A;

    private static int BiomeChannel(PaintLayer l)
        => (l == PaintLayer.Plains_R) ? 0 :
           (l == PaintLayer.Hills_G) ? 1 :
           (l == PaintLayer.Mountains_B) ? 2 : 3;

    /// <summary>
    /// OVERWRITE biome paint:
    /// - Paint: selected -> 1, others -> 0 (always overwrite)
    /// - Erase: (if biomeEraseToVoid) all channels -> 0 via MoveTowards (no residuals)
    /// </summary>
    private Color PaintBiomeOverwrite(Color c, PaintLayer l, bool isErase, float k01)
    {
        int ch = BiomeChannel(l);

        float r = c.r, g = c.g, b = c.b, a = c.a;

        // -------- ERASE --------
        if (isErase)
        {
            if (biomeEraseToVoid)
            {
                // ✅ FIX: MoveTowards avoids residuals that blur can resurrect
                r = Mathf.MoveTowards(r, 0f, k01);
                g = Mathf.MoveTowards(g, 0f, k01);
                b = Mathf.MoveTowards(b, 0f, k01);
                a = Mathf.MoveTowards(a, 0f, k01);

                const float snap = 0.00075f;
                if (r < snap) r = 0f;
                if (g < snap) g = 0f;
                if (b < snap) b = 0f;
                if (a < snap) a = 0f;

                if ((r + g + b + a) <= EPS) return new Color(0, 0, 0, 0);
                return Clamp01(new Color(r, g, b, a));
            }
            else
            {
                // selected-only erase (optional)
                if (ch == 0) r = Mathf.MoveTowards(r, 0f, k01);
                else if (ch == 1) g = Mathf.MoveTowards(g, 0f, k01);
                else if (ch == 2) b = Mathf.MoveTowards(b, 0f, k01);
                else a = Mathf.MoveTowards(a, 0f, k01);

                const float snap = 0.00075f;
                if (r < snap) r = 0f;
                if (g < snap) g = 0f;
                if (b < snap) b = 0f;
                if (a < snap) a = 0f;

                if ((r + g + b + a) <= EPS) return new Color(0, 0, 0, 0);
                return Clamp01(new Color(r, g, b, a));
            }
        }

        // -------- PAINT (OVERWRITE) --------
        // selected up
        if (ch == 0) r = r + (1f - r) * k01;
        else if (ch == 1) g = g + (1f - g) * k01;
        else if (ch == 2) b = b + (1f - b) * k01;
        else a = a + (1f - a) * k01;

        // others down
        if (ch != 0) r = Mathf.MoveTowards(r, 0f, k01);
        if (ch != 1) g = Mathf.MoveTowards(g, 0f, k01);
        if (ch != 2) b = Mathf.MoveTowards(b, 0f, k01);
        if (ch != 3) a = Mathf.MoveTowards(a, 0f, k01);

        // snap tiny residues
        const float snap2 = 0.00075f;
        if (r < snap2) r = 0f;
        if (g < snap2) g = 0f;
        if (b < snap2) b = 0f;
        if (a < snap2) a = 0f;

        return Clamp01(new Color(r, g, b, a));
    }

    private Color PaintFeature(Color c, PaintLayer l, bool isErase, float k01)
    {
        float target = isErase ? 0f : 1f;

        if (l == PaintLayer.Road_R) c.r = Mathf.MoveTowards(c.r, target, k01);
        else if (l == PaintLayer.River_G) c.g = Mathf.MoveTowards(c.g, target, k01);

        c.r = Mathf.Clamp01(c.r);
        c.g = Mathf.Clamp01(c.g);
        c.b = 0f;
        c.a = 0f;
        return c;
    }

    private static Color Clamp01(Color c)
        => new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
}
#endif
