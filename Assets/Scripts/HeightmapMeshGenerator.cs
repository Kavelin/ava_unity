using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HeightmapMeshGenerator : MonoBehaviour
{
    public enum HeightSource
    {
        PerlinNoise,
        HeightmapTexture
    }

    [Header("Mesh")]
    [Min(2)] public int resolutionX = 128;
    [Min(2)] public int resolutionZ = 128;
    [Min(1f)] public float sizeX = 200f;
    [Min(1f)] public float sizeZ = 200f;
    public bool generateOnStart = true;

    [Header("Heights")]
    public HeightSource heightSource = HeightSource.PerlinNoise;
    public Texture2D heightmap;
    [Min(0f)] public float heightScale = 20f;

    [Header("Perlin")]
    [Min(0.0001f)] public float perlinScale = 0.05f;
    public Vector2 perlinOffset = Vector2.zero;

    [Header("Collision")]
    public bool addMeshCollider = true;

    Mesh _mesh;

    void Start()
    {
        if (generateOnStart)
        {
            Generate();
        }
    }

    public void Generate()
    {
        if (resolutionX < 2 || resolutionZ < 2)
        {
            Debug.LogWarning("HeightmapMeshGenerator: resolution must be >= 2.");
            return;
        }

        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (_mesh == null)
        {
            _mesh = new Mesh
            {
                name = "HeightmapMesh",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };
        }

        Vector3[] verts = new Vector3[(resolutionX + 1) * (resolutionZ + 1)];
        Vector2[] uvs = new Vector2[verts.Length];

        int i = 0;
        for (int z = 0; z <= resolutionZ; z++)
        {
            float v = z / (float)resolutionZ;
            float zPos = (v - 0.5f) * sizeZ;

            for (int x = 0; x <= resolutionX; x++)
            {
                float u = x / (float)resolutionX;
                float xPos = (u - 0.5f) * sizeX;
                float height = SampleHeight01(u, v) * heightScale;

                verts[i] = new Vector3(xPos, height, zPos);
                uvs[i] = new Vector2(u, v);
                i++;
            }
        }

        int[] triangles = new int[resolutionX * resolutionZ * 6];
        int ti = 0;
        int vert = 0;
        for (int z = 0; z < resolutionZ; z++)
        {
            for (int x = 0; x < resolutionX; x++)
            {
                triangles[ti++] = vert;
                triangles[ti++] = vert + resolutionX + 1;
                triangles[ti++] = vert + 1;

                triangles[ti++] = vert + 1;
                triangles[ti++] = vert + resolutionX + 1;
                triangles[ti++] = vert + resolutionX + 2;

                vert++;
            }
            vert++;
        }

        _mesh.Clear();
        _mesh.vertices = verts;
        _mesh.uv = uvs;
        _mesh.triangles = triangles;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        meshFilter.sharedMesh = _mesh;

        if (addMeshCollider)
        {
            MeshCollider collider = GetComponent<MeshCollider>();
            if (collider == null)
            {
                collider = gameObject.AddComponent<MeshCollider>();
            }
            collider.sharedMesh = _mesh;
        }
    }

    float SampleHeight01(float u, float v)
    {
        if (heightSource == HeightSource.HeightmapTexture && heightmap != null)
        {
            // Heightmap must have Read/Write enabled for GetPixelBilinear to work.
            return heightmap.GetPixelBilinear(u, v).grayscale;
        }

        float px = u * sizeX * perlinScale + perlinOffset.x;
        float pz = v * sizeZ * perlinScale + perlinOffset.y;
        return Mathf.PerlinNoise(px, pz);
    }

    public bool TryGetHeightWorld(Vector3 worldPos, out float heightWorld)
    {
        Vector3 local = transform.InverseTransformPoint(worldPos);
        float u = (local.x / sizeX) + 0.5f;
        float v = (local.z / sizeZ) + 0.5f;

        if (u < 0f || u > 1f || v < 0f || v > 1f)
        {
            heightWorld = 0f;
            return false;
        }

        float heightLocal = SampleHeight01(u, v) * heightScale;
        Vector3 localPoint = new Vector3(local.x, heightLocal, local.z);
        heightWorld = transform.TransformPoint(localPoint).y;
        return true;
    }
}
