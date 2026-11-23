using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Endless 16-lane track along +Z that morphs between a round tunnel and a flat road.
///
/// Morphing = "unrolling": the tunnel ring is cut along its top seam and unrolled
/// (curvature goes from 1/R to 0). The lanes keep the same width the whole time,
/// so a fully open tunnel is a flat road 16 lanes wide.
/// The morph is a function of distance travelled: tunnel -> opening -> road -> closing -> repeat.
/// </summary>
public class TunnelGenerator : MonoBehaviour
{
    public const int LaneCount = 16;
    public const float LaneAngle = Mathf.PI * 2f / LaneCount;
    public const int SeamLane = 4;   // the ring is cut at the top, right before lane 4

    [Header("References")]
    public Transform target;            // the player
    public Material tunnelMaterial;     // optional, a fallback is created if empty

    [Header("Shape")]
    public float baseRadius = 8f;
    public float radiusVariation = 1.5f;   // +/- radius change from noise (only while closed)
    public float noiseFrequency = 0.02f;

    [Header("Morph tunnel <-> road (distances in units)")]
    public float tunnelLength = 300f;      // stays a closed tunnel for this long
    public float transitionLength = 120f;  // length of the opening / closing phase
    public float roadLength = 250f;        // stays a flat road for this long
    [Tooltip("Above this openness the top seam is a real gap and crossing it means falling off.")]
    public float seamOpenThreshold = 0.05f;

    [Header("Segments")]
    public float segmentLength = 20f;
    public int ringsPerSegment = 10;       // longitudinal subdivisions
    public int stepsPerLane = 3;           // curvature subdivisions per lane
    public int segmentsAhead = 12;
    public int segmentsBehind = 2;
    public int crossBandsPerSegment = 4;   // dark cross lines on the texture

    [Header("Floating origin")]
    public float shiftThreshold = 2000f;   // shift the world back when the target passes this Z

    class Segment
    {
        public GameObject go;
        public Mesh mesh;
        public float startZ;
    }

    readonly Queue<Segment> active = new Queue<Segment>();
    Material runtimeMaterial;
    int[] triangles;
    Vector3[] vertices, normals;
    Vector2[] uvs;
    float nextZ;
    double worldOffsetZ;   // total distance removed by floating-origin shifts

    /// <summary>Raised when the world is shifted back by this many units (other objects must follow).</summary>
    public event System.Action<float> OriginShifted;

    // ---------- public shape queries ----------

    /// <summary>0 = closed tunnel, 1 = flat road, at a scene-space Z.</summary>
    public float OpennessAt(float sceneZ)
    {
        float period = tunnelLength + 2f * transitionLength + roadLength;
        float p = (float)((sceneZ + worldOffsetZ) % period);
        if (p < 0f) p += period;

        if (p < tunnelLength) return 0f;
        p -= tunnelLength;
        if (p < transitionLength) return Mathf.SmoothStep(0f, 1f, p / transitionLength);
        p -= transitionLength;
        if (p < roadLength) return 1f;
        p -= roadLength;
        return 1f - Mathf.SmoothStep(0f, 1f, p / transitionLength);
    }

    float RadiusFor(float sceneZ, float openness)
    {
        float n = Mathf.PerlinNoise((float)(sceneZ + worldOffsetZ) * noiseFrequency, 0.37f);
        return baseRadius + (n - 0.5f) * 2f * radiusVariation * (1f - openness);
    }

    /// <summary>Width of one lane in world units at a scene-space Z.</summary>
    public float LaneWidthAt(float sceneZ) => RadiusFor(sceneZ, OpennessAt(sceneZ)) * LaneAngle;

    /// <summary>
    /// Point on the track surface.
    /// k = position across the track in lane units, 0..16 (0 = left edge of the road / top seam,
    /// 8 = bottom centre). normal points toward the inside (where the player stands).
    /// </summary>
    public void SurfacePoint(float k, float sceneZ, out Vector3 pos, out Vector3 normal)
    {
        float m = OpennessAt(sceneZ);
        Surface(k, RadiusFor(sceneZ, m), 1f - m, out pos, out normal);
        pos.z = sceneZ;
    }

    // open = 1 -> full circle, open -> 0 -> flat strip of the same width
    static void Surface(float k, float R, float open, out Vector3 pos, out Vector3 normal)
    {
        float phi = (k - LaneCount * 0.5f) * LaneAngle;   // -PI..PI, 0 at the bottom

        if (open < 0.001f)
        {
            pos = new Vector3(R * phi, -R, 0f);
            normal = Vector3.up;
            return;
        }

        float th = open * phi;
        float s = Mathf.Sin(th), c = Mathf.Cos(th);
        pos = new Vector3(R * s / open, -R + R * (1f - c) / open, 0f);
        normal = new Vector3(-s, c, 0f);
    }

    // ---------- lifecycle ----------

    void Start()
    {
        if (target == null)
        {
            Debug.LogError("TunnelGenerator: assign a target.");
            enabled = false;
            return;
        }

        BuildSharedData();
        runtimeMaterial = CreateMaterial();
        nextZ = Mathf.Floor(target.position.z / segmentLength) * segmentLength
                - segmentsBehind * segmentLength;
    }

    void Update()
    {
        float z = target.position.z;

        while (nextZ < z + segmentsAhead * segmentLength)
        {
            Segment seg;
            if (active.Count > 0 && active.Peek().startZ + segmentLength < z - segmentsBehind * segmentLength)
                seg = active.Dequeue();     // recycle the oldest one
            else
                seg = CreateSegment();

            BuildSegment(seg, nextZ);
            active.Enqueue(seg);
            nextZ += segmentLength;
        }

        if (z > shiftThreshold) ShiftOrigin();
    }

    // ---------- floating origin ----------

    void ShiftOrigin()
    {
        float shift = Mathf.Floor(target.position.z / segmentLength) * segmentLength;
        foreach (var s in active)
        {
            s.startZ -= shift;
            s.go.transform.position = new Vector3(0, 0, s.startZ);
        }
        nextZ -= shift;
        worldOffsetZ += shift;
        target.position -= new Vector3(0, 0, shift);
        OriginShifted?.Invoke(shift);
    }

    // ---------- mesh generation ----------

    int Idx(int ring, int lane, int step) => (ring * LaneCount + lane) * (stepsPerLane + 1) + step;

    void BuildSharedData()
    {
        int vertCount = (ringsPerSegment + 1) * LaneCount * (stepsPerLane + 1);
        vertices = new Vector3[vertCount];
        normals = new Vector3[vertCount];
        uvs = new Vector2[vertCount];

        triangles = new int[ringsPerSegment * LaneCount * stepsPerLane * 6];
        int t = 0;
        for (int r = 0; r < ringsPerSegment; r++)
            for (int l = 0; l < LaneCount; l++)
                for (int s = 0; s < stepsPerLane; s++)
                {
                    int v00 = Idx(r, l, s);
                    int v10 = Idx(r, l, s + 1);
                    int v01 = Idx(r + 1, l, s);
                    int v11 = Idx(r + 1, l, s + 1);

                    // wound so the faces point toward the inside (the side the player is on)
                    triangles[t++] = v00; triangles[t++] = v01; triangles[t++] = v10;
                    triangles[t++] = v10; triangles[t++] = v01; triangles[t++] = v11;
                }
    }

    Segment CreateSegment()
    {
        var go = new GameObject("TunnelSegment");
        go.transform.SetParent(transform, false);

        var mesh = new Mesh { name = "TunnelSegmentMesh" };
        if (vertices.Length > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.MarkDynamic();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = runtimeMaterial;

        return new Segment { go = go, mesh = mesh };
    }

    void BuildSegment(Segment seg, float startZ)
    {
        seg.startZ = startZ;
        seg.go.transform.position = new Vector3(0, 0, startZ);

        float dz = segmentLength / ringsPerSegment;
        int i = 0;

        for (int r = 0; r <= ringsPerSegment; r++)
        {
            float localZ = r * dz;
            float sceneZ = startZ + localZ;
            float m = OpennessAt(sceneZ);
            float radius = RadiusFor(sceneZ, m);
            float v = (float)r / ringsPerSegment * crossBandsPerSegment;

            for (int l = 0; l < LaneCount; l++)
            {
                // position of this lane across the track (the ring is cut before SeamLane)
                int cell = (l - SeamLane + LaneCount) % LaneCount;

                for (int s = 0; s <= stepsPerLane; s++)
                {
                    float tLane = (float)s / stepsPerLane;
                    Surface(cell + tLane, radius, 1f - m, out Vector3 p, out Vector3 n);

                    vertices[i] = new Vector3(p.x, p.y, localZ);
                    normals[i] = n;
                    uvs[i] = new Vector2((l + tLane) / LaneCount, v);
                    i++;
                }
            }
        }

        var mesh = seg.mesh;
        mesh.Clear();
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
    }

    // ---------- material / texture ----------

    Material CreateMaterial()
    {
        Material mat;
        if (tunnelMaterial != null)
        {
            mat = new Material(tunnelMaterial);
        }
        else
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            mat = new Material(sh);
        }
        mat.mainTexture = BuildLaneTexture();
        return mat;
    }

    // 16 coloured lanes with dark edges + a dark band for cross lines
    Texture2D BuildLaneTexture()
    {
        const int laneW = 16, h = 16;
        var tex = new Texture2D(LaneCount * laneW, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapModeU = TextureWrapMode.Clamp,
            wrapModeV = TextureWrapMode.Repeat
        };

        var px = new Color32[LaneCount * laneW * h];
        for (int y = 0; y < h; y++)
            for (int l = 0; l < LaneCount; l++)
            {
                Color c = Color.HSVToRGB(l / (float)LaneCount, 0.55f, 0.85f);
                for (int x = 0; x < laneW; x++)
                {
                    bool border = x < 1 || x >= laneW - 1 || y < 1;
                    px[y * LaneCount * laneW + l * laneW + x] = border ? (Color32)(c * 0.2f) : (Color32)c;
                }
            }
        tex.SetPixels32(px);
        tex.Apply(false);
        return tex;
    }
}