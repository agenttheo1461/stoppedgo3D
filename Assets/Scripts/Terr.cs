using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

public class Terr : MonoBehaviour
{
    [Header("terrain")]
    [SerializeField] int sectionSize = 8;
    [SerializeField] int seed = 0;
    [SerializeField] int viewRadius = 8;
    [SerializeField] float waveFrequency = 0.04f;
    [SerializeField] float height = 12f;
    [SerializeField] private Material chunkMaterial;

    [Header("oceans")]
    [SerializeField] private float waterHeight = -2.0f; 
    [SerializeField] private Material waterMaterial;
    [SerializeField] float continentalScale = 0.01f; 
    [SerializeField] float deepOceanDepth = 30f;
    [SerializeField] float warpIntensity = 15.0f;
    [SerializeField] float warpScale = 0.008f;

    Transform localPLayerTransform;
    Dictionary<Vector2Int, GameObject> terrainSections = new Dictionary<Vector2Int, GameObject>();
    Dictionary<Vector2Int, GameObject> waterSections = new Dictionary<Vector2Int, GameObject>();

    void Update()
    {
        if (localPLayerTransform == null)
        {
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
            {
                var localClient = NetworkManager.Singleton.LocalClient;
                if (localClient != null && localClient.PlayerObject != null)
                {
                    localPLayerTransform = localClient.PlayerObject.transform;
                }
            } else {
                GameObject playerObject = GameObject.FindWithTag("Player");
                if (playerObject != null) localPLayerTransform = playerObject.transform;
            }
        }
        updateTerrain();
    }

    void updateTerrain()
    {
        HashSet<Vector2Int> neededSections = new HashSet<Vector2Int>();
        Vector3 centerPos = (localPLayerTransform != null) ? localPLayerTransform.position : Vector3.zero;

        int px = Mathf.FloorToInt(centerPos.x / sectionSize);
        int pz = Mathf.FloorToInt(centerPos.z / sectionSize);
        
        for (int x = -viewRadius; x <= viewRadius; x++)
        {
            for (int z = -viewRadius; z <= viewRadius; z++)
            {
                Vector2Int sectionCoord = new Vector2Int(px + x, pz + z);
                neededSections.Add(sectionCoord);
                if (!terrainSections.ContainsKey(sectionCoord))
                {
                    GenerateSection(sectionCoord);
                }
            }
        }
        
        List<Vector2Int> sectionsToRemove = new List<Vector2Int>();
        foreach (var section in terrainSections)
        {            
            if (!neededSections.Contains(section.Key)) {
                sectionsToRemove.Add(section.Key);
            }
        }
        foreach (var section in sectionsToRemove)
        {
            GameObject chunk = terrainSections[section];
            if (chunk != null) Destroy(chunk);
            terrainSections.Remove(section);

            if (waterSections.ContainsKey(section))
            {
                GameObject waterChunk = waterSections[section];
                if (waterChunk != null) Destroy(waterChunk);
                waterSections.Remove(section);
            }
        }
    }

    void GenerateSection(Vector2Int sectionCoord)
    {
        GameObject chunk = new GameObject($"Chunk {sectionCoord.x} {sectionCoord.y}");
        chunk.transform.position = new Vector3(sectionCoord.x * sectionSize, 0, sectionCoord.y * sectionSize);
        chunk.transform.parent = transform;
        MeshFilter meshFilter = chunk.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = chunk.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = chunkMaterial;
        
        bool hasWaterInChunk = false;
        Mesh mesh = NewMesh(sectionCoord, out hasWaterInChunk);
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
            wMeshFilter.mesh = NewWaterMesh();
            
            BoxCollider waterTrigger = waterChunk.AddComponent<BoxCollider>();
            waterTrigger.isTrigger = true;
            waterTrigger.center = new Vector3(sectionSize / 2f, -25f, sectionSize / 2f);
            waterTrigger.size = new Vector3(sectionSize, 50f, sectionSize);

            waterSections.Add(sectionCoord, waterChunk);
        }
    }

    Mesh NewMesh(Vector2Int sectionCoord, out bool hasWater)
    {
        Mesh mesh = new Mesh();
        float thickness = 1.0f;
        int res = sectionSize + 1;
        int totalVertices = res * res;
        Vector3[] vertices = new Vector3[totalVertices * 2];
        Vector2[] uvs = new Vector2[totalVertices * 2];
        
        int topTriangles = sectionSize * sectionSize * 6;
        int[] triangles = new int[topTriangles * 2];
        
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

                // Top layer vertex
                vertices[index] = new Vector3(x, finalHeight, z);
                uvs[index] = new Vector2((float)x / sectionSize, (float)z / sectionSize);
                
                // Bottom layer vertex
                float bottomHeight = finalHeight - thickness;
                vertices[bottomIndex] = new Vector3(x, bottomHeight, z);
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
                triangles[tIndex + 1] = b_topLeft;
                triangles[tIndex + 2] = b_topRight;

                triangles[tIndex + 3] = b_bottomLeft;
                triangles[tIndex + 4] = b_topRight;
                triangles[tIndex + 5] = b_bottomRight;
            
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

    Mesh NewWaterMesh()
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