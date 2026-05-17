using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

public class ClientWorldLoader : MonoBehaviour
{
    [Header("Render Distance")]
    [SerializeField] private int viewRadius = 8;
    [SerializeField] private Material chunkMaterial;
    [SerializeField] private Material waterMaterial;

    [Header("Continental Settings")]
    [SerializeField] private float continentalScale = 0.01f; 
    [SerializeField] private float deepOceanDepth = 30f;
    [SerializeField] private float warpIntensity = 15.0f;
    [SerializeField] private float warpScale = 0.008f;

    // Synced properties gathered from the server
    private int seed;
    private int sectionSize;
    private float waveFrequency;
    private float height;
    private float waterHeight;

    private Transform localPlayerTransform;
    private Vector2Int lastPlayerChunkPos = new Vector2Int(int.MaxValue, int.MaxValue);
    private bool isInitialized = false;

    private Dictionary<Vector2Int, GameObject> terrainSections = new Dictionary<Vector2Int, GameObject>();
    private Dictionary<Vector2Int, GameObject> waterSections = new Dictionary<Vector2Int, GameObject>();

    void Update()
    {
        if (ServerWorldManager.Instance == null)
        {
            Debug.LogWarning("⚠️ ClientWorldLoader is stuck: Cannot find ServerWorldManager.Instance!");
            return;
        }
        
        // FIX: Check if we are either the active Server/Host OR a connected Client
        bool isNetworkReady = NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsConnectedClient;

        if (!isNetworkReady)
        {
            Debug.LogWarning("⚠️ ClientWorldLoader is stuck: NetworkManager is not active or connected yet!");
            return;
        }
        // 1. Wait until Netcode connects and synchronizes server world settings
        if (!isInitialized)
        {
            if (ServerWorldManager.Instance != null && NetworkManager.Singleton.IsConnectedClient)
            {
                seed = ServerWorldManager.Instance.NetworkSeed.Value;
                sectionSize = ServerWorldManager.Instance.NetworkSectionSize.Value;
                waveFrequency = ServerWorldManager.Instance.NetworkWaveFrequency.Value;
                height = ServerWorldManager.Instance.NetworkHeight.Value;
                waterHeight = ServerWorldManager.Instance.NetworkWaterHeight.Value;
                isInitialized = true;
            }
            return; 
        }

        // 2. Locate the local player avatar running on this machine
        if (localPlayerTransform == null)
        {
            var localClient = NetworkManager.Singleton.LocalClient;
            if (localClient != null && localClient.PlayerObject != null)
            {
                localPlayerTransform = localClient.PlayerObject.transform;
            }
            return;
        }

        // 3. Infinite Chunk Check: Only update terrain when moving into a new chunk grid tile
        int currentChunkX = Mathf.FloorToInt(localPlayerTransform.position.x / sectionSize);
        int currentChunkZ = Mathf.FloorToInt(localPlayerTransform.position.z / sectionSize);
        Vector2Int currentPlayerChunk = new Vector2Int(currentChunkX, currentChunkZ);

        if (currentPlayerChunk != lastPlayerChunkPos)
        {
            UpdateInfiniteTerrain(currentPlayerChunk);
            lastPlayerChunkPos = currentPlayerChunk;
        }
    }

    void UpdateInfiniteTerrain(Vector2Int centerChunk)
    {
        HashSet<Vector2Int> neededSections = new HashSet<Vector2Int>();

        // Sweep out matching chunks inside your view radius relative to the player
        for (int x = -viewRadius; x <= viewRadius; x++)
        {
            for (int z = -viewRadius; z <= viewRadius; z++)
            {
                Vector2Int sectionCoord = new Vector2Int(centerChunk.x + x, centerChunk.y + z);
                neededSections.Add(sectionCoord);

                if (!terrainSections.ContainsKey(sectionCoord))
                {
                    GenerateClientChunk(sectionCoord);
                }
            }
        }

        // Clean up out-of-range chunks to keep memory clear infinitely
        List<Vector2Int> sectionsToRemove = new List<Vector2Int>();
        foreach (var section in terrainSections.Keys)
        {
            if (!neededSections.Contains(section))
            {
                sectionsToRemove.Add(section);
            }
        }

        foreach (var section in sectionsToRemove)
        {
            if (terrainSections.TryGetValue(section, out GameObject chunk)) Destroy(chunk);
            if (waterSections.TryGetValue(section, out GameObject water)) Destroy(water);

            terrainSections.Remove(section);
            waterSections.Remove(section);
        }
    }

    void GenerateClientChunk(Vector2Int sectionCoord)
    {
        GameObject chunk = new GameObject($"Chunk {sectionCoord.x} {sectionCoord.y}");
        chunk.transform.position = new Vector3(sectionCoord.x * sectionSize, 0, sectionCoord.y * sectionSize);
        chunk.transform.parent = transform;

        MeshFilter meshFilter = chunk.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunk.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = chunkMaterial;

        bool hasWaterInChunk;
        Mesh mesh = BuildMeshProcedural(sectionCoord, out hasWaterInChunk);
        meshFilter.mesh = mesh;
        chunk.AddComponent<MeshCollider>().sharedMesh = mesh;

        terrainSections.Add(sectionCoord, chunk);
        chunk.tag = "collision";

        if (hasWaterInChunk && waterMaterial != null)
        {
            GameObject waterChunk = new GameObject($"Water {sectionCoord.x} {sectionCoord.y}");
            waterChunk.transform.position = new Vector3(sectionCoord.x * sectionSize, waterHeight, sectionCoord.y * sectionSize);
            waterChunk.transform.parent = transform;

            MeshFilter wMeshFilter = waterChunk.AddComponent<MeshFilter>();
            MeshRenderer wMeshRenderer = waterChunk.AddComponent<MeshRenderer>();
            wMeshRenderer.sharedMaterial = waterMaterial;
            wMeshFilter.mesh = BuildWaterMesh();

            BoxCollider waterTrigger = waterChunk.AddComponent<BoxCollider>();
            waterTrigger.isTrigger = true;
            waterTrigger.center = new Vector3(sectionSize / 2f, -25f, sectionSize / 2f);
            waterTrigger.size = new Vector3(sectionSize, 50f, sectionSize);

            waterSections.Add(sectionCoord, waterChunk);
        }
    }

    Mesh BuildMeshProcedural(Vector2Int sectionCoord, out bool hasWater)
    {
        Mesh mesh = new Mesh();
        float thickness = 1.0f;
        int res = sectionSize + 1;
        int totalVertices = res * res;
        Vector3[] vertices = new Vector3[totalVertices * 2];
        Vector2[] uvs = new Vector2[totalVertices * 2];
        int[] triangles = new int[sectionSize * sectionSize * 12];
        
        hasWater = false;

        for (int z = 0; z <= sectionSize; z++)
        {
            for (int x = 0; x <= sectionSize; x++)
            {
                int index = x + (z * res);
                int bottomIndex = index + totalVertices;
                
                float worldXPos = (sectionCoord.x * sectionSize + x);
                float worldZPos = (sectionCoord.y * sectionSize + z);

                float warpOffsetX = (Mathf.PerlinNoise(worldXPos * warpScale + seed + 142.3f, worldZPos * warpScale + seed + 311.7f) * 2f - 1f) * warpIntensity;
                float warpOffsetZ = (Mathf.PerlinNoise(worldXPos * warpScale + seed + 561.1f, worldZPos * warpScale + seed + 893.4f) * 2f - 1f) * warpIntensity;

                float warpedX = worldXPos + warpOffsetX;
                float warpedZ = worldZPos + warpOffsetZ;

                float nA = Mathf.PerlinNoise(warpedX * continentalScale + seed, warpedZ * continentalScale + seed);
                float nB = Mathf.PerlinNoise(warpedX * (continentalScale * 0.4f) + seed + 235, warpedZ * (continentalScale * 0.4f) + seed + 711);
                float macroNoise = (nA * 0.7f) + (nB * 0.3f);

                float worldX = warpedX * waveFrequency;
                float worldZ = warpedZ * waveFrequency;
                float noiseY = Mathf.PerlinNoise(worldX * 0.2f + seed, worldZ * 0.2f + seed); 
                float subtleModifier = Mathf.Lerp(0.6f, 1.4f, noiseY);
                float wave = Mathf.Sin(worldX + waveFrequency) * Mathf.Cos(worldZ + waveFrequency) * 0.5f;
                float noise = Mathf.PerlinNoise(worldX * 0.1f + seed, worldZ * 0.1f + seed) * 0.5f;
                
                float baseLandHeight = (wave + noise) * height * subtleModifier + (waterHeight + 4.0f);
                float finalHeight = 0f;

                if (macroNoise >= 0.48f)
                {
                    finalHeight = baseLandHeight;
                }
                else if (macroNoise < 0.48f && macroNoise >= 0.38f)
                {
                    float tBeach = (macroNoise - 0.38f) / 0.1f; 
                    float flattenedBeaches = Mathf.Lerp(waterHeight - 0.3f, waterHeight + 2.0f, tBeach);
                    finalHeight = Mathf.Lerp(flattenedBeaches, baseLandHeight, Mathf.SmoothStep(0f, 1f, tBeach));
                }
                else
                {
                    float tOcean = macroNoise / 0.38f; 
                    float slopeCurve = Mathf.SmoothStep(0f, 1f, tOcean);
                    float oceanFloor = waterHeight - deepOceanDepth;
                    float coastEdge = waterHeight - 0.3f;
                    finalHeight = Mathf.Lerp(oceanFloor, coastEdge, slopeCurve);
                }

                if (finalHeight < waterHeight) hasWater = true;

                vertices[index] = new Vector3(x, finalHeight, z);
                uvs[index] = new Vector2((float)x / sectionSize, (float)z / sectionSize);
                
                vertices[bottomIndex] = new Vector3(x, finalHeight - thickness, z);
                uvs[bottomIndex] = new Vector2((float)x / sectionSize, (float)z / sectionSize);
            }
        }
    
        int tIndex = 0;
        for (int z = 0; z < sectionSize; z++)
        {
            for (int x = 0; x < sectionSize; x++)
            {
                int bottomLeft = x + (z * res);
                int bottomRight = (x + 1) + (z * res);
                int topLeft = x + ((z + 1) * res);
                int topRight = (x + 1) + ((z + 1) * res);

                triangles[tIndex + 0] = bottomLeft;
                triangles[tIndex + 1] = topLeft;
                triangles[tIndex + 2] = topRight;
                triangles[tIndex + 3] = bottomLeft;
                triangles[tIndex + 4] = topRight;
                triangles[tIndex + 5] = bottomRight;
                tIndex += 6;

                int b_bottomLeft = bottomLeft + totalVertices;
                int b_bottomRight = bottomRight + totalVertices;
                int b_topLeft = topLeft + totalVertices;
                int b_topRight = topRight + totalVertices;

                triangles[tIndex + 0] = b_bottomLeft;
                triangles[tIndex + 1] = b_topRight;
                triangles[tIndex + 2] = b_topLeft;
                triangles[tIndex + 3] = b_bottomLeft;
                triangles[tIndex + 4] = b_bottomRight;
                triangles[tIndex + 5] = b_topRight;
                tIndex += 6;
            }
        }    
    
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    Mesh BuildWaterMesh()
    {
        Mesh mesh = new Mesh();
        int res = sectionSize + 1;
        Vector3[] vertices = new Vector3[res * res];
        Vector2[] uvs = new Vector2[res * res];
        int[] triangles = new int[sectionSize * sectionSize * 6];

        for (int z = 0; z <= sectionSize; z++)
        {
            for (int x = 0; x <= sectionSize; x++)
            {
                int index = x + (z * res);
                vertices[index] = new Vector3(x, 0, z);
                uvs[index] = new Vector2((float)x / sectionSize, (float)z / sectionSize);
            }
        }

        int tIndex = 0;
        for (int z = 0; z < sectionSize; z++)
        {
            for (int x = 0; x < sectionSize; x++)
            {
                int bottomLeft = x + (z * res);
                int bottomRight = (x + 1) + (z * res);
                int topLeft = x + ((z + 1) * res);
                int topRight = (x + 1) + ((z + 1) * res);

                triangles[tIndex + 0] = bottomLeft;
                triangles[tIndex + 1] = topLeft;
                triangles[tIndex + 2] = topRight;
                triangles[tIndex + 3] = bottomLeft;
                triangles[tIndex + 4] = topRight;
                triangles[tIndex + 5] = bottomRight;
                tIndex += 6;
            }
        }

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}