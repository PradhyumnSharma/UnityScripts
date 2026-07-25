using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Copies the lowest plausible surface from a photogrammetry capture into a Unity Terrain.
/// It is an editor tool, not a component: open it from Tools > Terrain Snap.
/// </summary>
public class TerrainSnapWindow : EditorWindow
{
    /// <summary>
    /// Mask types for terrain sample classification during painting.
    /// </summary>
    private enum MaskType : byte
    {
        Unknown = 0,
        Ground = 1,
        Reference = 2,
        Ignore = 3
    }
    [SerializeField] private Terrain terrain;
    [SerializeField] private GameObject referenceMesh;
    [SerializeField] private float rayHeight = 2000f;
    [SerializeField, Range(0f, 1f)] private float blend = 1f;
    [SerializeField, Min(1)] private int sampleStep = 1;
    [SerializeField] private bool removeUpwardSpikes = true;
    [SerializeField, Min(1)] private int spikeNeighbourRadius = 2;
    [SerializeField, Min(0.01f)] private float spikeHeightThreshold = 5f;
    [SerializeField] private bool smoothAfter;

    [SerializeField] private bool useSelectionArea;
    [SerializeField] private bool hasSelection;
    [SerializeField] private Vector3 selectionPointA;
    [SerializeField] private Vector3 selectionPointB;

    private bool selectingFirstPoint;
    private bool selectingSecondPoint;
    private readonly RaycastHit[] raycastBuffer = new RaycastHit[128];
    private readonly HashSet<Collider> sourceColliders = new HashSet<Collider>();

    // Mask painting system
    [SerializeField] private bool maskPaintingEnabled;
    [SerializeField] private float brushRadius = 5f;
    [SerializeField, Range(0f, 1f)] private float brushStrength = 1f;
    private MaskType currentMaskMode = MaskType.Ground;
    private byte[,] terrainMask;
    private bool foldoutMaskPainting;


    [MenuItem("Tools/Terrain Snap")]
    public static void ShowWindow()
    {
        GetWindow<TerrainSnapWindow>("Terrain Snap");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Source and Target", EditorStyles.boldLabel);
        terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        referenceMesh = (GameObject)EditorGUILayout.ObjectField("Reference Mesh Root", referenceMesh, typeof(GameObject), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Sampling", EditorStyles.boldLabel);
        rayHeight = Mathf.Max(1f, EditorGUILayout.FloatField("Ray Height", rayHeight));
        sampleStep = EditorGUILayout.IntSlider("Sample Step", sampleStep, 1, 8);
        blend = EditorGUILayout.Slider("Blend", blend, 0f, 1f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Cleanup", EditorStyles.boldLabel);
        removeUpwardSpikes = EditorGUILayout.Toggle("Remove Upward Spikes", removeUpwardSpikes);
        using (new EditorGUI.DisabledScope(!removeUpwardSpikes))
        {
            spikeNeighbourRadius = EditorGUILayout.IntSlider("Neighbour Radius", spikeNeighbourRadius, 1, 8);
            spikeHeightThreshold = Mathf.Max(0.01f, EditorGUILayout.FloatField("Spike Height (metres)", spikeHeightThreshold));
        }

        smoothAfter = EditorGUILayout.Toggle("Smooth After", smoothAfter);
        EditorGUILayout.HelpBox(
            "Cleanup removes only isolated heights above their local median. " +
            "Low areas such as ponds, ditches and valleys are kept.",
            MessageType.Info);

        EditorGUILayout.Space();
        DrawSelectionControls();

        EditorGUILayout.Space();
        DrawMaskPaintingControls();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(terrain == null || referenceMesh == null || (useSelectionArea && !hasSelection)))
        {
            if (GUILayout.Button("Copy Heights", GUILayout.Height(30f)))
            {
                CopyHeights();
            }
        }
    }

    private void DrawSelectionControls()
    {
        EditorGUILayout.LabelField("Selection Area", EditorStyles.boldLabel);
        useSelectionArea = EditorGUILayout.Toggle("Limit To Selected Area", useSelectionArea);

        if (!useSelectionArea)
        {
            return;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(hasSelection ? "Pick New Area" : "Pick Area In Scene"))
        {
            selectingFirstPoint = true;
            selectingSecondPoint = false;
            hasSelection = false;
            SceneView.RepaintAll();
        }

        using (new EditorGUI.DisabledScope(!hasSelection && !selectingFirstPoint && !selectingSecondPoint))
        {
            if (GUILayout.Button("Clear"))
            {
                ClearSelection();
            }
        }
        EditorGUILayout.EndHorizontal();

        if (selectingFirstPoint)
        {
            EditorGUILayout.HelpBox("Click the first corner on the capture in Scene view.", MessageType.Info);
        }
        else if (selectingSecondPoint)
        {
            EditorGUILayout.HelpBox("Click the opposite corner on the capture in Scene view.", MessageType.Info);
        }
        else if (hasSelection)
        {
            EditorGUILayout.LabelField("Area selected.", EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.HelpBox("Pick an area before copying, or disable this option to copy the whole terrain.", MessageType.Warning);
        }
    }

    private void DrawMaskPaintingControls()
    {
        foldoutMaskPainting = EditorGUILayout.Foldout(foldoutMaskPainting, "Mask Painting", true);
        if (!foldoutMaskPainting)
        {
            return;
        }

        using (new EditorGUI.IndentLevelScope())
        {
            bool wasEnabled = maskPaintingEnabled;
            maskPaintingEnabled = EditorGUILayout.Toggle("Enable Painting", maskPaintingEnabled);

            if (maskPaintingEnabled && wasEnabled != maskPaintingEnabled && terrain != null)
            {
                InitializeMask();
            }

            using (new EditorGUI.DisabledScope(!maskPaintingEnabled))
            {
                brushRadius = Mathf.Max(0.1f, EditorGUILayout.FloatField("Brush Radius", brushRadius));
                brushStrength = EditorGUILayout.Slider("Brush Strength", brushStrength, 0f, 1f);

                EditorGUILayout.LabelField("Current Paint Mode", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Ground", GetMaskModeButtonStyle(MaskType.Ground)))
                {
                    currentMaskMode = MaskType.Ground;
                }
                if (GUILayout.Button("Reference", GetMaskModeButtonStyle(MaskType.Reference)))
                {
                    currentMaskMode = MaskType.Reference;
                }
                if (GUILayout.Button("Ignore", GetMaskModeButtonStyle(MaskType.Ignore)))
                {
                    currentMaskMode = MaskType.Ignore;
                }
                if (GUILayout.Button("Unknown (Erase)", GetMaskModeButtonStyle(MaskType.Unknown)))
                {
                    currentMaskMode = MaskType.Unknown;
                }
                EditorGUILayout.EndHorizontal();
            }
        }
    }

    private GUIStyle GetMaskModeButtonStyle(MaskType mode)
    {
        GUIStyle style = new GUIStyle(GUI.skin.button);
        if (currentMaskMode == mode)
        {
            style.normal.background = EditorGUIUtility.FindTexture("d_toggle on");
        }
        return style;
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        // Handle mask painting first if enabled
        if (maskPaintingEnabled && terrain != null)
        {
            HandleMaskPainting();
            DrawMaskVisualization();
        }

        if (!useSelectionArea)
        {
            return;
        }

        Event currentEvent = Event.current;
        if ((selectingFirstPoint || selectingSecondPoint) && currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && !currentEvent.alt)
        {
            if (TryPickReferencePoint(currentEvent.mousePosition, out Vector3 point))
            {
                if (selectingFirstPoint)
                {
                    selectionPointA = point;
                    selectingFirstPoint = false;
                    selectingSecondPoint = true;
                }
                else
                {
                    selectionPointB = point;
                    selectingSecondPoint = false;
                    hasSelection = true;
                }

                currentEvent.Use();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        if (hasSelection)
        {
            DrawSelectionRectangle();
        }
    }

    private void HandleMaskPainting()
    {
        if (terrainMask == null)
        {
            return;
        }

        Event currentEvent = Event.current;
        if (currentEvent.type == EventType.MouseMove || currentEvent.type == EventType.MouseDrag)
        {
            SceneView.RepaintAll();
        }

        if (currentEvent.type == EventType.MouseDrag && currentEvent.button == 0 && !currentEvent.alt)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(currentEvent.mousePosition);
            if (TryGetTerrainIntersection(ray, out Vector3 paintPosition))
            {
                PaintMask(paintPosition, currentMaskMode);
                currentEvent.Use();
            }
        }
    }

    private bool TryGetTerrainIntersection(Ray ray, out Vector3 hitPoint)
    {
        hitPoint = default;
        TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
        if (collider == null || !collider.Raycast(ray, out RaycastHit hit, 10000f))
        {
            return false;
        }

        hitPoint = hit.point;
        return true;
    }

    private void PaintMask(Vector3 worldPosition, MaskType maskType)
    {
        if (terrainMask == null || terrain == null || terrain.terrainData == null)
        {
            return;
        }

        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;
        Vector3 terrainSize = terrainData.size;
        int resolution = terrainData.heightmapResolution;

        // Convert world position to terrain local coordinates
        Vector3 localPos = worldPosition - terrainPos;
        float normalizedX = Mathf.Clamp01(localPos.x / terrainSize.x);
        float normalizedZ = Mathf.Clamp01(localPos.z / terrainSize.z);

        // Convert to sample coordinates
        int centerX = Mathf.RoundToInt(normalizedX * (resolution - 1));
        int centerZ = Mathf.RoundToInt(normalizedZ * (resolution - 1));

        // Apply brush with falloff
        int brushPixelRadius = Mathf.Max(1, Mathf.RoundToInt(brushRadius / (terrainSize.x / resolution)));
        for (int z = centerZ - brushPixelRadius; z <= centerZ + brushPixelRadius; z++)
        {
            if (z < 0 || z >= resolution)
                continue;

            for (int x = centerX - brushPixelRadius; x <= centerX + brushPixelRadius; x++)
            {
                if (x < 0 || x >= resolution)
                    continue;

                // Check if inside selection area
                float worldX = terrainPos.x + x / (float)(resolution - 1) * terrainSize.x;
                float worldZ = terrainPos.z + z / (float)(resolution - 1) * terrainSize.z;
                if (!IsInsideSelection(worldX, worldZ))
                    continue;

                // Calculate distance-based falloff
                float distX = x - centerX;
                float distZ = z - centerZ;
                float distSquared = distX * distX + distZ * distZ;
                float radiusSquared = brushPixelRadius * brushPixelRadius;
                float falloff = Mathf.Max(0f, 1f - (distSquared / (radiusSquared + 1f)));
                float strength = brushStrength * falloff;

                // Apply mask
                if (maskType == MaskType.Unknown)
                {
                    terrainMask[z, x] = (byte)MaskType.Unknown;
                }
                else
                {
                    byte current = terrainMask[z, x];
                    if (strength >= 1f || current == (byte)MaskType.Unknown)
                    {
                        terrainMask[z, x] = (byte)maskType;
                    }
                    else if (strength > 0f)
                    {
                        // Blend towards the new type
                        terrainMask[z, x] = (byte)maskType;
                    }
                }
            }
        }
    }

    private void DrawMaskVisualization()
    {
        if (terrainMask == null || terrain == null || terrain.terrainData == null)
        {
            return;
        }

        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;
        Vector3 terrainSize = terrainData.size;
        int resolution = terrainData.heightmapResolution;

        // Draw brush circle at current mouse position
        DrawBrushCircle();

        // Draw mask visualization as overlays
        Color[] colors = new Color[resolution * resolution];
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                MaskType maskType = (MaskType)terrainMask[z, x];
                colors[z * resolution + x] = GetMaskColor(maskType);
            }
        }

        DrawTerrainOverlay(colors, resolution, terrainPos, terrainSize);
    }

    private void DrawBrushCircle()
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        if (TryGetTerrainIntersection(ray, out Vector3 brushCenter))
        {
            Handles.color = new Color(1f, 1f, 1f, 0.5f);
            Handles.DrawWireDisc(brushCenter, Vector3.up, brushRadius);
        }
    }

    private Color GetMaskColor(MaskType maskType)
    {
        return maskType switch
        {
            MaskType.Ground => new Color(1f, 1f, 1f, 0.2f),      // White
            MaskType.Reference => new Color(0f, 0f, 1f, 0.2f),   // Blue
            MaskType.Ignore => new Color(1f, 0f, 0f, 0.2f),      // Red
            MaskType.Unknown => new Color(0f, 0f, 0f, 0f),       // Transparent
            _ => Color.clear
        };
    }

    private void DrawTerrainOverlay(Color[] colors, int resolution, Vector3 terrainPos, Vector3 terrainSize)
    {
        // Draw colored quads for each sample point
        Handles.BeginGUI();
        for (int z = 0; z < resolution - 1; z++)
        {
            for (int x = 0; x < resolution - 1; x++)
            {
                Color avgColor = (
                    colors[z * resolution + x] +
                    colors[z * resolution + x + 1] +
                    colors[(z + 1) * resolution + x] +
                    colors[(z + 1) * resolution + x + 1]
                ) * 0.25f;

                if (avgColor.a <= 0f)
                    continue;

                // Calculate world positions for the quad corners
                Vector3 p0 = GetTerrainSamplePosition(x, z, resolution, terrainPos, terrainSize);
                Vector3 p1 = GetTerrainSamplePosition(x + 1, z, resolution, terrainPos, terrainSize);
                Vector3 p2 = GetTerrainSamplePosition(x + 1, z + 1, resolution, terrainPos, terrainSize);
                Vector3 p3 = GetTerrainSamplePosition(x, z + 1, resolution, terrainPos, terrainSize);

                // Convert to screen space and draw
                Vector2 sp0 = HandleUtility.WorldToGUIPoint(p0);
                Vector2 sp1 = HandleUtility.WorldToGUIPoint(p1);
                Vector2 sp2 = HandleUtility.WorldToGUIPoint(p2);
                Vector2 sp3 = HandleUtility.WorldToGUIPoint(p3);

                Handles.color = avgColor;
                DrawQuad(sp0, sp1, sp2, sp3);
            }
        }
        Handles.EndGUI();
    }

    private Vector3 GetTerrainSamplePosition(int x, int z, int resolution, Vector3 terrainPos, Vector3 terrainSize)
    {
        float normalizedX = x / (float)(resolution - 1);
        float normalizedZ = z / (float)(resolution - 1);
        return terrainPos + new Vector3(
            normalizedX * terrainSize.x,
            terrain.terrainData.GetHeight(z, x) * terrainSize.y,
            normalizedZ * terrainSize.z
        );
    }

    private void DrawQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        Handles.DrawLine(p0, p1);
        Handles.DrawLine(p1, p2);
        Handles.DrawLine(p2, p3);
        Handles.DrawLine(p3, p0);
    }

    private void InitializeMask()
    {
        if (terrain == null || terrain.terrainData == null)
        {
            return;
        }

        int resolution = terrain.terrainData.heightmapResolution;
        terrainMask = new byte[resolution, resolution];
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                terrainMask[z, x] = (byte)MaskType.Unknown;
            }
        }
    }

    private bool TryPickReferencePoint(Vector2 mousePosition, out Vector3 point)
    {
        point = default;
        if (referenceMesh == null)
        {
            return false;
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);
        MeshCollider[] colliders = referenceMesh.GetComponentsInChildren<MeshCollider>(true);
        float closestDistance = float.MaxValue;
        bool found = false;

        for (int index = 0; index < colliders.Length; index++)
        {
            MeshCollider collider = colliders[index];
            if (collider != null && collider.enabled && collider.Raycast(ray, out RaycastHit hit, 10000f) && hit.distance < closestDistance)
            {
                closestDistance = hit.distance;
                point = hit.point;
                found = true;
            }
        }

        return found;
    }

    private void DrawSelectionRectangle()
    {
        float y = terrain == null ? 0f : terrain.transform.position.y + terrain.terrainData.size.y + 1f;
        float minX = Mathf.Min(selectionPointA.x, selectionPointB.x);
        float maxX = Mathf.Max(selectionPointA.x, selectionPointB.x);
        float minZ = Mathf.Min(selectionPointA.z, selectionPointB.z);
        float maxZ = Mathf.Max(selectionPointA.z, selectionPointB.z);

        Vector3[] corners =
        {
            new Vector3(minX, y, minZ), new Vector3(maxX, y, minZ),
            new Vector3(maxX, y, maxZ), new Vector3(minX, y, maxZ)
        };
        Handles.DrawSolidRectangleWithOutline(corners, new Color(1f, 0.1f, 0.1f, 0.08f), Color.red);
    }

    private void CopyHeights()
    {
        if (!ValidateInputs(out TerrainData terrainData))
        {
            return;
        }

        CacheSourceColliders();
        if (sourceColliders.Count == 0)
        {
            Debug.LogError("Terrain Snap: No enabled MeshColliders were found below Reference Mesh Root.");
            return;
        }

        Physics.SyncTransforms();

        int resolution = terrainData.heightmapResolution;
        float[,] originalHeights = terrainData.GetHeights(0, 0, resolution, resolution);
        float[,] sampledHeights = new float[resolution, resolution];
        bool[,] sampledMask = new bool[resolution, resolution];

        Vector3 terrainPosition = terrain.transform.position;
        Vector3 terrainSize = terrainData.size;
        int step = Mathf.Max(1, sampleStep);
        bool raycastBufferOverflowed = false;

        try
        {
            if (!SampleCapture(resolution, terrainPosition, terrainSize, step, sampledHeights, sampledMask, ref raycastBufferOverflowed))
            {
                return;
            }

            // Initial cleanup of sampled data (spikes)
            float[,] cleanedHeights = removeUpwardSpikes
                ? RemoveIsolatedUpwardSpikes(sampledHeights, sampledMask, terrainSize.y, step)
                : sampledHeights;

            // Apply painted masks (if any) and enforce Ignore semantics: any sample under Ignore is discarded.
            byte[,] maskMap = terrainMask;
            if (maskMap != null && (maskMap.GetLength(0) != resolution || maskMap.GetLength(1) != resolution))
            {
                // Mask dimensions do not match heightmap; ignore masks to preserve compatibility.
                maskMap = null;
            }

            if (maskMap != null)
            {
                for (int z = 0; z < resolution; z++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        if ((MaskType)maskMap[z, x] == MaskType.Ignore && sampledMask[z, x])
                        {
                            // Painted Ignore must never supply a sample
                            sampledMask[z, x] = false;
                        }
                    }
                }
            }

            // Reconstruct ignored areas driven by painted masks.
            ReconstructMaskedAreas(cleanedHeights, sampledMask, originalHeights, maskMap, terrainSize.y, out float[,] reconstructedHeights, out bool[,] reconstructedMask);

            // Build final result by orchestrating anchors, sampled values and reconstructed pixels.
            float[,] result = (float[,])originalHeights.Clone();
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    MaskType m = maskMap == null ? MaskType.Unknown : (MaskType)maskMap[z, x];

                    // Ground pixels are hard constraints: prefer sampled height if present, otherwise keep original.
                    if (m == MaskType.Ground)
                    {
                        if (sampledMask[z, x])
                        {
                            result[z, x] = cleanedHeights[z, x];
                        }
                        else
                        {
                            result[z, x] = originalHeights[z, x];
                        }
                        continue;
                    }

                    // Reconstructed pixels come from the solver. Blend them into the terrain by the user blend slider.
                    if (reconstructedMask[z, x])
                    {
                        result[z, x] = Mathf.Lerp(originalHeights[z, x], reconstructedHeights[z, x], blend);
                        continue;
                    }

                    // Sampled pixels (Reference or Unknown) behave like before: blend sample into original.
                    if (sampledMask[z, x])
                    {
                        result[z, x] = Mathf.Lerp(originalHeights[z, x], cleanedHeights[z, x], blend);
                    }
                }
            }

            // Edge-preserving smoothing only inside reconstructed pixels (if requested).
            if (smoothAfter)
            {
                result = SmoothReconstructedPixels(result, reconstructedMask, maskMap, terrainSize.y);
            }

            Undo.RegisterCompleteObjectUndo(terrainData, "Terrain Snap");
            terrainData.SetHeights(0, 0, result);
            EditorUtility.SetDirty(terrainData);

            Debug.Log($"Terrain Snap: copied {CountSamples(sampledMask)} height samples from {sourceColliders.Count} source colliders." +
                (raycastBufferOverflowed ? " Some rays exceeded the 128-hit buffer; use a capture-only physics layer if artifacts remain." : string.Empty));
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>
    /// Reconstructs painted Ignore regions using surrounding Ground and Reference anchors.
    /// Produces per-pixel reconstructed heights and a mask of pixels that were reconstructed.
    /// The algorithm is intentionally simple (distance-weighted interpolation) and isolated so it can be replaced later.
    /// </summary>
    private void ReconstructMaskedAreas(float[,] sampledHeights, bool[,] sampledMask, float[,] originalHeights, byte[,] maskMap, float terrainHeightMeters, out float[,] reconstructedHeights, out bool[,] reconstructedMask)
    {
        int resolution = originalHeights.GetLength(0);
        reconstructedHeights = (float[,])originalHeights.Clone();
        reconstructedMask = new bool[resolution, resolution];

        if (maskMap == null)
        {
            // No painted masks -> nothing to reconstruct.
            return;
        }

        bool[,] visited = new bool[resolution, resolution];
        int[] dirX = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dirZ = { -1, -1, -1, 0, 0, 1, 1, 1 };

        for (int startZ = 0; startZ < resolution; startZ++)
        {
            for (int startX = 0; startX < resolution; startX++)
            {
                if (visited[startZ, startX])
                {
                    continue;
                }

                if ((MaskType)maskMap[startZ, startX] != MaskType.Ignore)
                {
                    visited[startZ, startX] = true;
                    continue;
                }

                // Flood-fill connected Ignore region
                List<Vector2Int> region = new List<Vector2Int>();
                Queue<Vector2Int> q = new Queue<Vector2Int>();
                q.Enqueue(new Vector2Int(startX, startZ));
                visited[startZ, startX] = true;

                while (q.Count > 0)
                {
                    Vector2Int p = q.Dequeue();
                    region.Add(p);

                    for (int d = 0; d < 8; d++)
                    {
                        int nx = p.x + dirX[d];
                        int nz = p.y + dirZ[d];
                        if (nx < 0 || nx >= resolution || nz < 0 || nz >= resolution) continue;
                        if (visited[nz, nx]) continue;
                        if ((MaskType)maskMap[nz, nx] == MaskType.Ignore)
                        {
                            visited[nz, nx] = true;
                            q.Enqueue(new Vector2Int(nx, nz));
                        }
                    }
                }

                // Collect anchor points (Ground and Reference) surrounding this region.
                List<(int x, int z, float height, float weight)> anchors = new List<(int, int, float, float)>();
                int padding = Mathf.Max(1, sampleStep * 3);
                int minX = resolution, minZ = resolution, maxX = 0, maxZ = 0;
                foreach (var p in region)
                {
                    minX = Mathf.Min(minX, p.x);
                    maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.y);
                    maxZ = Mathf.Max(maxZ, p.y);
                }

                minX = Mathf.Max(0, minX - padding);
                minZ = Mathf.Max(0, minZ - padding);
                maxX = Mathf.Min(resolution - 1, maxX + padding);
                maxZ = Mathf.Min(resolution - 1, maxZ + padding);

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        MaskType mt = (MaskType)maskMap[z, x];
                        if (mt == MaskType.Ground || mt == MaskType.Reference)
                        {
                            float h = sampledMask[z, x] ? sampledHeights[z, x] : originalHeights[z, x];
                            float w = mt == MaskType.Ground ? 3f : 1f; // Ground anchors are stronger
                            anchors.Add((x, z, h, w));
                        }
                    }
                }

                if (anchors.Count == 0)
                {
                    // Nothing to anchor to; skip this region to preserve existing terrain.
                    continue;
                }

                // For each pixel in the region, estimate height using distance-weighted interpolation.
                const float power = 1.5f; // IDW power. Tunable and replaceable.
                foreach (var p in region)
                {
                    double sum = 0.0;
                    double wsum = 0.0;
                    for (int i = 0; i < anchors.Count; i++)
                    {
                        var a = anchors[i];
                        float dx = a.x - p.x;
                        float dz = a.z - p.y;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz);
                        // Use pixel-space distance with small offset to avoid singularities.
                        float d = Mathf.Max(1e-4f, dist);
                        double w = a.weight / Math.Pow(d, power);
                        sum += w * a.height;
                        wsum += w;
                    }

                    float estimate = wsum > 0.0 ? (float)(sum / wsum) : originalHeights[p.y, p.x];
                    reconstructedHeights[p.y, p.x] = estimate;
                    reconstructedMask[p.y, p.x] = true;
                }

                // Small smoothing inside this reconstructed region to remove artefacts but preserve anchors.
                // We do this per-region to keep the operation local and predictable.
                ApplyLocalSmoothing(reconstructedHeights, reconstructedMask, region, terrainHeightMeters);
            }
        }
    }

    // Small, edge-aware smoothing applied only to reconstructed pixels inside the provided region.
    // Anchors (Ground/Reference) are not modified here; only reconstructed pixels are adjusted.
    private void ApplyLocalSmoothing(float[,] heights, bool[,] reconstructedMask, List<Vector2Int> region, float terrainHeightMeters)
    {
        int resolution = heights.GetLength(0);
        float[,] copy = (float[,])heights.Clone();
        int iterations = 2;
        float sigmaMeters = 1.0f; // bilateral range sigma in metres
        float pixelSizeX = terrain == null ? 1f : terrain.terrainData.size.x / (resolution - 1);
        float pixelSizeZ = terrain == null ? 1f : terrain.terrainData.size.z / (resolution - 1);

        HashSet<long> regionSet = new HashSet<long>();
        foreach (var p in region)
        {
            regionSet.Add(((long)p.y << 32) | (uint)p.x);
        }

        for (int iter = 0; iter < iterations; iter++)
        {
            foreach (var p in region)
            {
                int x = p.x; int z = p.y;
                if (!reconstructedMask[z, x]) continue;

                double sum = 0.0; double wsum = 0.0;
                for (int oz = -1; oz <= 1; oz++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = Mathf.Clamp(x + ox, 0, resolution - 1);
                        int nz = Mathf.Clamp(z + oz, 0, resolution - 1);
                        if (!reconstructedMask[nz, nx])
                        {
                            // Allow using anchored neighbours as reference but do not modify them.
                            // Anchors are not in reconstructedMask.
                        }

                        float hCenter = copy[z, x] * terrainHeightMeters;
                        float hNeighbor = copy[nz, nx] * terrainHeightMeters;
                        float rangeDiff = hNeighbor - hCenter;
                        float rangeWeight = Mathf.Exp(-(rangeDiff * rangeDiff) / (2f * sigmaMeters * sigmaMeters));
                        float spatialDist = Mathf.Sqrt((ox * ox) * (pixelSizeX * pixelSizeX) + (oz * oz) * (pixelSizeZ * pixelSizeZ));
                        float spatialWeight = 1f / (1f + spatialDist);
                        float w = spatialWeight * rangeWeight;
                        sum += w * copy[nz, nx];
                        wsum += w;
                    }
                }

                if (wsum > 0.0)
                {
                    heights[z, x] = (float)(sum / wsum);
                }
            }
            // copy back for next iteration
            Array.Copy(heights, copy, heights.Length);
        }
    }

    // Smooth only inside reconstructed pixels using an edge-preserving bilateral-like filter.
    private float[,] SmoothReconstructedPixels(float[,] heights, bool[,] reconstructedMask, byte[,] maskMap, float terrainHeightMeters)
    {
        if (reconstructedMask == null) return heights;
        int resolution = heights.GetLength(0);
        float[,] copy = (float[,])heights.Clone();
        int iterations = 2;
        float sigmaMeters = 1.0f;
        float pixelSizeX = terrain == null ? 1f : terrain.terrainData.size.x / (resolution - 1);
        float pixelSizeZ = terrain == null ? 1f : terrain.terrainData.size.z / (resolution - 1);

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    if (!reconstructedMask[z, x]) continue;
                    // Never move Ground anchors.
                    if (maskMap != null && (MaskType)maskMap[z, x] == MaskType.Ground) continue;

                    double sum = 0.0; double wsum = 0.0;
                    float centerH = copy[z, x] * terrainHeightMeters;
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = Mathf.Clamp(x + ox, 0, resolution - 1);
                            int nz = Mathf.Clamp(z + oz, 0, resolution - 1);

                            float neighH = copy[nz, nx] * terrainHeightMeters;
                            float rangeDiff = neighH - centerH;
                            float rangeWeight = Mathf.Exp(-(rangeDiff * rangeDiff) / (2f * sigmaMeters * sigmaMeters));
                            float spatialDist = Mathf.Sqrt((ox * ox) * (pixelSizeX * pixelSizeX) + (oz * oz) * (pixelSizeZ * pixelSizeZ));
                            float spatialWeight = 1f / (1f + spatialDist);
                            float w = spatialWeight * rangeWeight;
                            sum += w * copy[nz, nx];
                            wsum += w;
                        }
                    }

                    if (wsum > 0.0)
                    {
                        heights[z, x] = (float)(sum / wsum);
                    }
                }
            }
            Array.Copy(heights, copy, heights.Length);
        }

        return heights;
    }

    private float[,] ApplySamples(float[,] original, float[,] sampled, bool[,] mask)
    {
        int resolution = original.GetLength(0);
        float[,] result = (float[,])original.Clone();
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                if (mask[z, x])
                {
                    result[z, x] = Mathf.Lerp(original[z, x], sampled[z, x], blend);
                }
            }
        }
        return result;
    }

    private bool SampleCapture(int resolution, Vector3 terrainPosition, Vector3 terrainSize, int step, float[,] sampledHeights, bool[,] sampledMask, ref bool raycastBufferOverflowed)
    {
        for (int z = 0; z < resolution; z += step)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Terrain Snap", $"Sampling row {z + 1} of {resolution}", z / (float)(resolution - 1)))
            {
                Debug.Log("Terrain Snap: cancelled before any terrain changes were applied.");
                return false;
            }

            for (int x = 0; x < resolution; x += step)
            {
                float worldX = terrainPosition.x + x / (float)(resolution - 1) * terrainSize.x;
                float worldZ = terrainPosition.z + z / (float)(resolution - 1) * terrainSize.z;
                if (!IsInsideSelection(worldX, worldZ))
                {
                    continue;
                }

                Ray ray = new Ray(new Vector3(worldX, terrainPosition.y + rayHeight, worldZ), Vector3.down);
                int hitCount = Physics.RaycastNonAlloc(ray, raycastBuffer, rayHeight * 2f, ~0, QueryTriggerInteraction.Ignore);
                if (hitCount == raycastBuffer.Length)
                {
                    raycastBufferOverflowed = true;
                }

                if (!TryGetLowestSourceSurface(hitCount, terrainPosition.y, terrainSize.y, out float normalizedHeight))
                {
                    continue;
                }

                FillSampleBlock(x, z, step, normalizedHeight, sampledHeights, sampledMask);
            }
        }

        return true;
    }

    private bool TryGetLowestSourceSurface(int hitCount, float terrainBaseY, float terrainHeight, out float normalizedHeight)
    {
        normalizedHeight = 0f;
        bool found = false;
        float lowestHeight = float.MaxValue;

        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = raycastBuffer[index];
            if (hit.collider == null || !sourceColliders.Contains(hit.collider))
            {
                continue;
            }

            // A nearly vertical triangle is a wall, fence, pole or scan artifact—not terrain.
            if (Vector3.Angle(hit.normal, Vector3.up) > 80f)
            {
                continue;
            }

            if (hit.point.y < lowestHeight)
            {
                lowestHeight = hit.point.y;
                found = true;
            }
        }

        if (found)
        {
            normalizedHeight = Mathf.Clamp01((lowestHeight - terrainBaseY) / terrainHeight);
        }

        return found;
    }

    private float[,] RemoveIsolatedUpwardSpikes(float[,] samples, bool[,] mask, float terrainHeight, int step)
    {
        int resolution = samples.GetLength(0);
        float[,] filtered = (float[,])samples.Clone();
        int radius = Mathf.Max(1, spikeNeighbourRadius * step);
        float[] neighbourValues = new float[(spikeNeighbourRadius * 2 + 1) * (spikeNeighbourRadius * 2 + 1)];

        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                if (!mask[z, x])
                {
                    continue;
                }

                int count = CollectNeighbourHeights(samples, mask, x, z, radius, neighbourValues);
                if (count < 3)
                {
                    continue;
                }

                Array.Sort(neighbourValues, 0, count);
                float median = neighbourValues[count / 2];
                if ((samples[z, x] - median) * terrainHeight > spikeHeightThreshold)
                {
                    filtered[z, x] = median;
                }
            }
        }

        return filtered;
    }

    private int CollectNeighbourHeights(float[,] samples, bool[,] mask, int centerX, int centerZ, int radius, float[] values)
    {
        int resolution = samples.GetLength(0);
        int count = 0;
        for (int z = Mathf.Max(0, centerZ - radius); z <= Mathf.Min(resolution - 1, centerZ + radius); z += Mathf.Max(1, sampleStep))
        {
            for (int x = Mathf.Max(0, centerX - radius); x <= Mathf.Min(resolution - 1, centerX + radius); x += Mathf.Max(1, sampleStep))
            {
                if ((x == centerX && z == centerZ) || !mask[z, x])
                {
                    continue;
                }

                values[count++] = samples[z, x];
            }
        }

        return count;
    }

    private float[,] ApplySamples(float[,] original, float[,] sampled, bool[,] mask)
    {
        int resolution = original.GetLength(0);
        float[,] result = (float[,])original.Clone();
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                if (mask[z, x])
                {
                    result[z, x] = Mathf.Lerp(original[z, x], sampled[z, x], blend);
                }
            }
        }
        return result;
    }

    private float[,] SmoothChangedSamples(float[,] heights, bool[,] mask)
    {
        int resolution = heights.GetLength(0);
        float[,] smoothed = (float[,])heights.Clone();
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                if (!mask[z, x])
                {
                    continue;
                }

                float sum = 0f;
                int count = 0;
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int neighbourX = Mathf.Clamp(x + offsetX, 0, resolution - 1);
                        int neighbourZ = Mathf.Clamp(z + offsetZ, 0, resolution - 1);
                        sum += heights[neighbourZ, neighbourX];
                        count++;
                    }
                }
                smoothed[z, x] = sum / count;
            }
        }
        return smoothed;
    }

    private void FillSampleBlock(int startX, int startZ, int step, float height, float[,] samples, bool[,] mask)
    {
        int resolution = samples.GetLength(0);
        for (int z = startZ; z < Mathf.Min(startZ + step, resolution); z++)
        {
            for (int x = startX; x < Mathf.Min(startX + step, resolution); x++)
            {
                float worldX = terrain.transform.position.x + x / (float)(resolution - 1) * terrain.terrainData.size.x;
                float worldZ = terrain.transform.position.z + z / (float)(resolution - 1) * terrain.terrainData.size.z;
                if (IsInsideSelection(worldX, worldZ))
                {
                    samples[z, x] = height;
                    mask[z, x] = true;
                }
            }
        }
    }

    private void CacheSourceColliders()
    {
        sourceColliders.Clear();
        MeshCollider[] colliders = referenceMesh.GetComponentsInChildren<MeshCollider>(true);
        for (int index = 0; index < colliders.Length; index++)
        {
            MeshCollider collider = colliders[index];
            if (collider != null && collider.enabled && !collider.isTrigger)
            {
                sourceColliders.Add(collider);
            }
        }
    }

    private bool IsInsideSelection(float worldX, float worldZ)
    {
        if (!useSelectionArea)
        {
            return true;
        }

        float minX = Mathf.Min(selectionPointA.x, selectionPointB.x);
        float maxX = Mathf.Max(selectionPointA.x, selectionPointB.x);
        float minZ = Mathf.Min(selectionPointA.z, selectionPointB.z);
        float maxZ = Mathf.Max(selectionPointA.z, selectionPointB.z);
        return hasSelection && worldX >= minX && worldX <= maxX && worldZ >= minZ && worldZ <= maxZ;
    }

    private bool ValidateInputs(out TerrainData terrainData)
    {
        terrainData = terrain == null ? null : terrain.terrainData;
        if (terrain == null || referenceMesh == null || terrainData == null)
        {
            Debug.LogError("Terrain Snap: Assign a Terrain and a Reference Mesh Root with MeshColliders.");
            return false;
        }

        return true;
    }

    private static int CountSamples(bool[,] mask)
    {
        int count = 0;
        foreach (bool value in mask)
        {
            if (value)
            {
                count++;
            }
        }
        return count;
    }

    private void ClearSelection()
    {
        selectingFirstPoint = false;
        selectingSecondPoint = false;
        hasSelection = false;
        selectionPointA = default;
        selectionPointB = default;
        SceneView.RepaintAll();
    }
}
