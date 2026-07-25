using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class TerrainSnapWindow : EditorWindow
{
    public Terrain terrain;
    public GameObject referenceMesh;
    public float rayHeight = 2000f;
    [Range(0f, 1f)] public float blend = 1f;
    public bool smoothAfter = false;
    public int sampleStep = 1;

    // Ground detection parameters (single intelligent algorithm)
    [Header("Ground Detection")]
    [Tooltip("Maximum surface slope (degrees) to consider as ground. Surfaces steeper than this are ignored.")]
    public float MaxSlope = 70f;

    [Tooltip("Minimum object thickness (meters). Objects thinner than this (wires, cables) are ignored.")]
    public float MinObjectThickness = 1f;

    [Tooltip("Ignore isolated spikes higher than this value (meters) compared to neighbouring terrain samples.")]
    public float SpikeHeightThreshold = 5f;

    [Tooltip("Neighbour radius (in heightmap samples) used to compute local average for spike detection.")]
    public int NeighbourRadius = 2;

    [Tooltip("Draw debug rays in the Scene view: green = accepted hit, red = rejected hit.")]
    public bool DebugDraw = false;

    // Reusable buffers to avoid allocations in the inner loop
    private RaycastHit[] hitsBuffer = new RaycastHit[32];
    private RaycastHit[] thicknessBuffer = new RaycastHit[8];

    [Header("Selection Area")]
    [Tooltip("When enabled, only sample heights inside the selected area on the reference mesh.")]
    public bool useSelectionArea = false;

    // internal selection state (XZ world-space rectangle)
    private bool isSelectingArea = false;
    private bool hasValidSelection = false;
    private Vector3 areaPointA;
    private Vector3 areaPointB;
    private float areaMinX, areaMaxX, areaMinZ, areaMaxZ;

    [MenuItem("Tools/Terrain Snap Window")]
    public static void ShowWindow()
    {
        GetWindow<TerrainSnapWindow>("Terrain Snap");
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Source and Target", EditorStyles.boldLabel);
        terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        referenceMesh = (GameObject)EditorGUILayout.ObjectField("Reference Mesh Root", referenceMesh, typeof(GameObject), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        rayHeight = EditorGUILayout.FloatField("Ray Height", rayHeight);
        sampleStep = EditorGUILayout.IntField("Sample Step", Mathf.Max(1, sampleStep));
        blend = EditorGUILayout.Slider("Blend", blend, 0f, 1f);
        smoothAfter = EditorGUILayout.Toggle("Smooth After", smoothAfter);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Ground Detection", EditorStyles.boldLabel);
        MaxSlope = EditorGUILayout.Slider("Max Slope (deg)", MaxSlope, 0f, 89f);
        MinObjectThickness = EditorGUILayout.FloatField("Min Object Thickness (m)", MinObjectThickness);
        SpikeHeightThreshold = EditorGUILayout.FloatField("Spike Height Threshold (m)", SpikeHeightThreshold);
        NeighbourRadius = EditorGUILayout.IntField("Neighbour Radius (samples)", Mathf.Max(0, NeighbourRadius));
        DebugDraw = EditorGUILayout.Toggle("Debug Draw Rays", DebugDraw);

        if (GUILayout.Button("Copy Heights"))
        {
            CopyHeights();
        }
    }

    void OnEnable()
    {
        // subscribe to SceneView events so user can pick selection area
        SceneView.duringSceneGui += OnSceneGUI;
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    void OnSceneGUI(SceneView sv)
    {
        if (!useSelectionArea) return;

        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10, 10, 220, 120), EditorStyles.helpBox);
        GUILayout.Label("Selection Area", EditorStyles.boldLabel);
        if (!isSelectingArea)
        {
            if (GUILayout.Button("Start Area Selection (scene click A then B)"))
            {
                isSelectingArea = true;
            }
        }
        else
        {
            if (GUILayout.Button("Cancel Selection"))
            {
                isSelectingArea = false;
            }
        }

        if (GUILayout.Button("Clear Selection"))
        {
            ResetSelection();
        }

        GUILayout.EndArea();
        Handles.EndGUI();

        // handle scene mouse events for picking points
        Event e = Event.current;
        if (isSelectingArea && e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            Ray worldRay = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (referenceMesh != null)
            {
                // raycast against mesh colliders under referenceMesh
                MeshCollider[] cols = referenceMesh.GetComponentsInChildren<MeshCollider>(true);
                RaycastHit bestHit = default;
                bool hitAny = false;
                float bestDist = float.MaxValue;
                foreach (var mc in cols)
                {
                    if (mc == null) continue;
                    if (mc.Raycast(worldRay, out RaycastHit hit, 10000f))
                    {
                        if (hit.distance < bestDist)
                        {
                            bestDist = hit.distance;
                            bestHit = hit;
                            hitAny = true;
                        }
                    }
                }

                if (hitAny)
                {
                    Vector3 hitPoint = bestHit.point;
                    if (!areaPointA.Equals(Vector3.zero) && !areaPointB.Equals(Vector3.zero) && !isSelectingArea)
                    {
                        // noop
                    }

                    // first click sets A, second click sets B and finishes selection
                    if (areaPointA == Vector3.zero)
                    {
                        areaPointA = hitPoint;
                        e.Use();
                    }
                    else
                    {
                        areaPointB = hitPoint;
                        // finalize bounds
                        areaMinX = Mathf.Min(areaPointA.x, areaPointB.x);
                        areaMaxX = Mathf.Max(areaPointA.x, areaPointB.x);
                        areaMinZ = Mathf.Min(areaPointA.z, areaPointB.z);
                        areaMaxZ = Mathf.Max(areaPointA.z, areaPointB.z);
                        isSelectingArea = false;
                        useSelectionArea = true;
                        hasValidSelection = true;
                        e.Use();
                    }
                }
            }
        }

        // draw selection rectangle if defined
        if (useSelectionArea && !(areaMinX == 0f && areaMaxX == 0f && areaMinZ == 0f && areaMaxZ == 0f))
        {
            Vector3 a = new Vector3(areaMinX, terrain != null ? terrain.transform.position.y + terrain.terrainData.size.y + 1f : 0f, areaMinZ);
            Vector3 b = new Vector3(areaMaxX, terrain != null ? terrain.transform.position.y + terrain.terrainData.size.y + 1f : 0f, areaMinZ);
            Vector3 c = new Vector3(areaMaxX, terrain != null ? terrain.transform.position.y + terrain.terrainData.size.y + 1f : 0f, areaMaxZ);
            Vector3 d = new Vector3(areaMinX, terrain != null ? terrain.transform.position.y + terrain.terrainData.size.y + 1f : 0f, areaMaxZ);
            Handles.color = new Color(1f, 0f, 0f, 0.25f);
            Handles.DrawSolidRectangleWithOutline(new Vector3[] { a, b, c, d }, new Color(1f, 0f, 0f, 0.1f), Color.red);
        }
    }

    void CopyHeights()
    {
        if (terrain == null || referenceMesh == null)
        {
            Debug.LogError("TerrainSnap: Assign both Terrain and Reference Mesh Root.");
            return;
        }

        TerrainData data = terrain.terrainData;
        if (data == null)
        {
            Debug.LogError("TerrainSnap: Assigned Terrain has no TerrainData.");
            return;
        }

        Undo.RegisterCompleteObjectUndo(data, "Terrain Snap");

        int resolution = data.heightmapResolution;
        float[,] heights = data.GetHeights(0, 0, resolution, resolution);

        Vector3 terrainPos = terrain.transform.position;
        Vector3 terrainSize = data.size;

        MeshCollider[] colliders = referenceMesh.GetComponentsInChildren<MeshCollider>(true);
        if (colliders.Length == 0)
        {
            Debug.LogError("TerrainSnap: No MeshColliders found under Reference Mesh Root.");
            return;
        }

        int step = Mathf.Max(1, sampleStep);
        int sampledCount = 0;

        try // noop
        {
            for (int z = 0; z < resolution; z += step)
            {
                EditorUtility.DisplayProgressBar("Copying Heights", $"Processing row {z}/{resolution}...", (float)z / resolution);

                for (int x = 0; x < resolution; x += step)
                {
                    // world position for this heightmap sample
                    float nx = x / (float)(resolution - 1);
                    float nz = z / (float)(resolution - 1);
                    float worldX = terrainPos.x + nx * terrainSize.x;
                    float worldZ = terrainPos.z + nz * terrainSize.z;

                    // selection area test
                    if (useSelectionArea && !(worldX >= areaMinX && worldX <= areaMaxX && worldZ >= areaMinZ && worldZ <= areaMaxZ))
                        continue;

                    Vector3 rayOrigin = new Vector3(worldX, terrainPos.y + rayHeight, worldZ);
                    Ray ray = new Ray(rayOrigin, Vector3.down);

                    // perform a non-alloc raycast for all hits
                    int hitCount = Physics.RaycastNonAlloc(ray, hitsBuffer, rayHeight * 2f);
                    if (hitCount == 0) continue;

                    float bestNormalized = float.MaxValue; // choose lowest valid hit
                    bool anyValid = false;

                    // iterate hits and evaluate candidates
                    for (int hi = 0; hi < hitCount; hi++)
                    {
                        RaycastHit rh = hitsBuffer[hi];
                        if (rh.collider == null) continue;

                        // only consider colliders that belong to the reference mesh set
                        bool belongs = false;
                        for (int ci = 0; ci < colliders.Length; ci++)
                        {
                            if (colliders[ci] == null) continue;
                            if (rh.collider == colliders[ci]) { belongs = true; break; }
                        }
                        if (!belongs) continue;

                        // ignore disabled or trigger colliders
                        if (!rh.collider.enabled) continue;
                        if (rh.collider.isTrigger) continue;

                        // ignore steep surfaces
                        float angle = Vector3.Angle(rh.normal, Vector3.up);
                        if (angle > MaxSlope) // too steep
                        {
                            if (DebugDraw) Debug.DrawRay(rayOrigin, Vector3.down * rh.distance, Color.red, 1f);
                            continue;
                        }

                        // thickness test: cast a short ray starting slightly above the hit, measure if object is thin
                        bool thin = false;
                        Vector3 thicknessStart = rh.point + Vector3.up * 0.01f;
                        int tHits = Physics.RaycastNonAlloc(new Ray(thicknessStart, Vector3.down), thicknessBuffer, MinObjectThickness + 0.01f);
                        if (tHits == 0)
                        {
                            // no nearby geometry within MinObjectThickness -> likely thin/isolated
                            thin = true;
                        }
                        else
                        {
                            // if the closest thickness hit is far (> MinObjectThickness) consider thin
                            float minT = float.MaxValue;
                            for (int ti = 0; ti < tHits; ti++)
                            {
                                if (thicknessBuffer[ti].collider == null) continue;
                                if (thicknessBuffer[ti].distance < minT) minT = thicknessBuffer[ti].distance;
                            }
                            if (minT > MinObjectThickness) thin = true;
                        }

                        if (thin)
                        {
                            if (DebugDraw) Debug.DrawRay(rayOrigin, Vector3.down * rh.distance, Color.red, 1f);
                            continue;
                        }

                        // spike detection: compare to neighbouring terrain heights
                        float sum = 0f; int cnt = 0;
                        int r = Mathf.Max(1, NeighbourRadius);
                        for (int oy = -r; oy <= r; oy++)
                        {
                            int yy = z + oy * step;
                            if (yy < 0 || yy >= resolution) continue;
                            for (int ox = -r; ox <= r; ox++)
                            {
                                int xx = x + ox * step;
                                if (xx < 0 || xx >= resolution) continue;
                                sum += heights[yy, xx] * terrainSize.y + terrainPos.y;
                                cnt++;
                            }
                        }
                        float neighborAvg = cnt > 0 ? sum / cnt : (terrainPos.y + heights[z, x] * terrainSize.y);
                        float worldHeight = rh.point.y;
                        if (worldHeight - neighborAvg > SpikeHeightThreshold)
                        {
                            if (DebugDraw) Debug.DrawRay(rayOrigin, Vector3.down * rh.distance, Color.red, 1f);
                            continue;
                        }

                        // candidate is valid; take lowest normalized height
                        float normalized = (worldHeight - terrainPos.y) / terrainSize.y;
                        normalized = Mathf.Clamp01(normalized);
                        if (!anyValid || normalized < bestNormalized)
                        {
                            bestNormalized = normalized;
                            anyValid = true;
                        }

                        if (DebugDraw) Debug.DrawRay(rayOrigin, Vector3.down * rh.distance, Color.green, 1f);
                    }

                    if (anyValid)
                    {
                        float blended = Mathf.Lerp(heights[z, x], bestNormalized, blend);
                        heights[z, x] = blended;
                        sampledCount++;

                        // fill skipped cells for step > 1
                        if (step > 1)
                        {
                            for (int dz = 0; dz < step && (z + dz) < resolution; dz++)
                            {
                                for (int dx = 0; dx < step && (x + dx) < resolution; dx++)
                                {
                                    heights[z + dz, x + dx] = blended;
                                }
                            }
                        }
                    }
                }
            }

            // write heights back
            data.SetHeights(0, 0, heights);

            if (smoothAfter) Smooth(data);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log($"TerrainSnap: Complete. Sampled {sampledCount} cells from {colliders.Length} colliders.");
    }

    // Simple smoothing pass (box blur)
    void Smooth(TerrainData data)
    {
        int res = data.heightmapResolution;
        float[,] h = data.GetHeights(0, 0, res, res);
        float[,] outH = new float[res, res];

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float sum = 0f; int count = 0;
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = Mathf.Clamp(x + ox, 0, res - 1);
                        int ny = Mathf.Clamp(y + oy, 0, res - 1);
                        sum += h[ny, nx]; count++;
                    }
                }
                outH[y, x] = sum / count;
            }
        }

        data.SetHeights(0, 0, outH);
    }

    private void ResetSelection()
    {
        isSelectingArea = false;
        hasValidSelection = false;
        areaPointA = Vector3.zero;
        areaPointB = Vector3.zero;
        areaMinX = areaMaxX = areaMinZ = areaMaxZ = 0f;
        useSelectionArea = false;
    }
}
