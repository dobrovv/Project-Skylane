using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Endless 16-lane track that bends left/right and up/down, and morphs between a round tunnel
/// and a flat road.
///
/// Coordinates: everything along the track uses "s" = distance travelled along the track centre line.
/// The centre line is a procedurally generated curve (see Bends). A point on the track is described
/// by (k, s): k = position across the track in lane units, s = distance along it.
///
/// Morphing = "unrolling": the tunnel ring is cut along one lane boundary (the seam) and unrolled.
/// The seam lane is random for every morph cycle.
/// </summary>
public class TunnelGenerator : MonoBehaviour
{
    public const int LaneCount = 16;
    public const float LaneAngle = Mathf.PI * 2f / LaneCount;

    // Seam lane for which the road forms on the floor. Other seams rotate the cross-section.
    const int CanonicalSeam = 4;

    const float PathStep = 1f;          // spacing of the centre line samples
    const float YawRestore = 0.0004f;   // slowly pulls the heading back toward straight ahead

    [Header("References")]
    public Transform target;            // the player (must have the TunnelPlayer component)
    public Material tunnelMaterial;     // optional, a fallback is created if empty

    [Header("Shape")]
    public float baseRadius = 8f;
    public float radiusVariation = 1.5f;   // +/- radius change from noise (only while closed)
    public float noiseFrequency = 0.02f;

    [Header("Bends (the track curves left/right and up/down)")]
    public bool bend = true;                // false = perfectly straight
    public float bendScale = 500f;          // distance of one bend wave: bigger = longer, lazier curves
    public float minBendRadius = 150f;      // sharpest left/right turn (smaller = tighter turns)
    public float maxSlopeDegrees = 15f;     // steepest climb / descent
    public float bendStartDistance = 300f;  // the track is straight until here...
    public float bendRampDistance = 1500f;  // ...then the bends grow to full strength over this distance

    [Header("Morph tunnel <-> road (distances in units)")]
    public float tunnelLengthMin = 150f;   // every tunnel section gets a random length in this range
    public float tunnelLengthMax = 500f;
    public float roadLengthMin = 150f;     // every road section gets a random length in this range
    public float roadLengthMax = 400f;
    public float transitionLength = 120f;  // length of the opening / closing phase
    [Tooltip("Above this openness the seam is a real gap and crossing it means falling off.")]
    public float seamOpenThreshold = 0.05f;
    [Tooltip("0 = a different random pattern every run, otherwise a fixed pattern.")]
    public int seed = 0;

    [Header("Segments")]
    public float segmentLength = 20f;
    public int ringsPerSegment = 10;       // longitudinal subdivisions
    public int stepsPerLane = 3;           // curvature subdivisions per lane
    public int segmentsAhead = 12;
    public int segmentsBehind = 2;
    public int crossBandsPerSegment = 4;   // dark cross lines on the texture

    [Header("Floating origin")]
    public float shiftThreshold = 2000f;   // re-centre the world when the player is this far from the origin

    class Segment
    {
        public GameObject go;
        public Mesh mesh;
        public float startS;
    }

    readonly Queue<Segment> active = new Queue<Segment>();
    // One entry per morph cycle (closed tunnel -> opening -> road -> closing), generated in order.
    class Cycle
    {
        public float start;        // distance at which the cycle begins (a closed tunnel)
        public float tunnel;       // length of the closed tunnel part
        public float road;         // length of the flat road part
        public float transition;   // length of the opening and of the closing phase
        public int seam;           // lane at which the tunnel is cut open in this cycle
        public float End => start + tunnel + 2f * transition + road;
    }

    readonly List<Cycle> cycles = new List<Cycle>();
    System.Random cycleRng;

    // centre line samples (one every PathStep), generated on demand
    readonly List<Vector3> pathPos = new List<Vector3>();
    readonly List<Quaternion> pathRot = new List<Quaternion>();
    float pathYaw;
    float offsetYaw, offsetPitch;

    TunnelPlayer playerRef;
    Material runtimeMaterial;
    int[] triangles;
    Vector3[] vertices, normals;
    Vector2[] uvs;
    float nextS;

    /// <summary>Raised when the world is re-centred: everything in the world moved by -shift.</summary>
    public event System.Action<Vector3> OriginShifted;

    // ---------- track shape along s ----------

    // Makes sure cycles exist at least up to distance s. Random lengths and seams are drawn in order,
    // so the result is the same no matter which distance is asked for first.
    void EnsureCycles(float s)
    {
        if (cycleRng == null)
            cycleRng = seed != 0 ? new System.Random(seed) : new System.Random();

        while (cycles.Count == 0 || cycles[cycles.Count - 1].End <= s)
        {
            Cycle prev = cycles.Count > 0 ? cycles[cycles.Count - 1] : null;
            cycles.Add(new Cycle
            {
                start = prev != null ? prev.End : 0f,
                tunnel = RandomBetween(tunnelLengthMin, tunnelLengthMax),
                road = RandomBetween(roadLengthMin, roadLengthMax),
                transition = Mathf.Max(0.01f, transitionLength),
                seam = prev == null
                    ? cycleRng.Next(LaneCount)
                    : (prev.seam + 1 + cycleRng.Next(LaneCount - 1)) % LaneCount   // never the same lane twice in a row
            });
        }
    }

    float RandomBetween(float a, float b)
    {
        return Mathf.Lerp(Mathf.Min(a, b), Mathf.Max(a, b), (float)cycleRng.NextDouble());
    }

    Cycle CycleAt(float s)
    {
        EnsureCycles(Mathf.Max(0f, s));

        int lo = 0, hi = cycles.Count - 1;   // last cycle that starts at or before s
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cycles[mid].start <= s) lo = mid; else hi = mid - 1;
        }
        return cycles[lo];
    }

    /// <summary>0 = closed tunnel, 1 = flat road, at distance s.</summary>
    public float OpennessAt(float s)
    {
        Cycle c = CycleAt(s);
        float p = s - c.start;

        if (p < c.tunnel) return 0f;
        p -= c.tunnel;
        if (p < c.transition) return Mathf.SmoothStep(0f, 1f, p / c.transition);
        p -= c.transition;
        if (p < c.road) return 1f;
        p -= c.road;
        return 1f - Mathf.SmoothStep(0f, 1f, p / c.transition);
    }

    /// <summary>
    /// The lane at which the tunnel is cut open when it morphs into a road at distance s.
    /// Random per morph cycle, never the same lane as in the previous cycle. Only changes while
    /// the tunnel is fully closed. Cells across the track are counted from it: cell = lane - seam (mod 16).
    /// </summary>
    public int SeamLaneAt(float s) => CycleAt(s).seam;

    float RadiusFor(float s, float openness)
    {
        float n = Mathf.PerlinNoise(s * noiseFrequency, 0.37f);
        return baseRadius + (n - 0.5f) * 2f * radiusVariation * (1f - openness);
    }

    /// <summary>Width of one lane in world units at distance s.</summary>
    public float LaneWidthAt(float s) => RadiusFor(s, OpennessAt(s)) * LaneAngle;

    // ---------- centre line (the bends) ----------

    float BendAmount(float s)
    {
        if (!bend) return 0f;
        return Mathf.SmoothStep(0f, 1f, (s - bendStartDistance) / Mathf.Max(1f, bendRampDistance));
    }

    // Perlin noise remapped to -1..1
    static float Noise11(float x, float y) => Mathf.Clamp((Mathf.PerlinNoise(x, y) - 0.5f) * 2.5f, -1f, 1f);

    // how fast the heading turns (radians per unit) at distance s
    float YawRateAt(float s, float yaw)
    {
        float n = Noise11(offsetYaw + s / Mathf.Max(1f, bendScale), 0.13f);
        return BendAmount(s) * n / Mathf.Max(10f, minBendRadius) - yaw * YawRestore;
    }

    // climb angle (radians) at distance s
    float PitchAt(float s)
    {
        float n = Noise11(offsetPitch + s / Mathf.Max(1f, bendScale * 1.3f), 0.71f);
        return BendAmount(s) * Mathf.Min(maxSlopeDegrees, 45f) * Mathf.Deg2Rad * n;
    }

    static Vector3 Direction(float yaw, float pitch)
    {
        float cp = Mathf.Cos(pitch);
        return new Vector3(cp * Mathf.Sin(yaw), Mathf.Sin(pitch), cp * Mathf.Cos(yaw));
    }

    void EnsureInit()
    {
        if (pathPos.Count > 0) return;

        var rng = seed != 0 ? new System.Random(seed ^ 0x5bd1e995) : new System.Random();
        offsetYaw = (float)rng.NextDouble() * 1000f;
        offsetPitch = (float)rng.NextDouble() * 1000f;
        pathYaw = 0f;

        pathPos.Add(Vector3.zero);
        pathRot.Add(Quaternion.LookRotation(Direction(0f, PitchAt(0f)), Vector3.up));
    }

    void AppendSample()
    {
        int i = pathPos.Count - 1;
        float s = i * PathStep;

        pathYaw += YawRateAt(s + PathStep * 0.5f, pathYaw) * PathStep;
        Vector3 t1 = Direction(pathYaw, PitchAt(s + PathStep));
        Vector3 t0 = pathRot[i] * Vector3.forward;

        pathPos.Add(pathPos[i] + (t0 + t1) * (0.5f * PathStep));
        pathRot.Add(Quaternion.LookRotation(t1, Vector3.up));
    }

    /// <summary>
    /// Centre line at distance s: position, and the orientation of the cross-section
    /// (forward = direction of travel, up = ceiling, right = right wall).
    /// </summary>
    public void FrameAt(float s, out Vector3 pos, out Quaternion rot)
    {
        EnsureInit();

        if (s <= 0f)   // before the start: continue straight backwards
        {
            rot = pathRot[0];
            pos = pathPos[0] + rot * Vector3.forward * s;
            return;
        }

        float f = s / PathStep;
        int i = (int)f;
        float t = f - i;
        while (pathPos.Count <= i + 1) AppendSample();

        pos = Vector3.Lerp(pathPos[i], pathPos[i + 1], t);
        rot = Quaternion.Slerp(pathRot[i], pathRot[i + 1], t);
    }

    // ---------- surface of the track ----------

    /// <summary>
    /// Point on the track surface, in world space.
    /// k = position across the track in lane units, 0..16, counted from the seam lane
    /// (0 = left edge of the road, 8 = middle of the road). s = distance along the track.
    /// normal points toward the inside (where the player stands), forward is the direction of travel.
    /// </summary>
    public void SurfacePoint(float k, float s, out Vector3 pos, out Vector3 normal, out Vector3 forward)
    {
        float m = OpennessAt(s);
        float rot = (SeamLaneAt(s) - CanonicalSeam) * LaneAngle;
        Surface(k, RadiusFor(s, m), 1f - m, Mathf.Cos(rot), Mathf.Sin(rot), out Vector3 lp, out Vector3 ln);

        FrameAt(s, out Vector3 origin, out Quaternion q);
        pos = origin + q * lp;
        normal = q * ln;
        forward = q * Vector3.forward;
    }

    public void SurfacePoint(float k, float s, out Vector3 pos, out Vector3 normal)
    {
        SurfacePoint(k, s, out pos, out normal, out _);
    }

    // Cross-section point in the local frame (x = right, y = up, z = 0).
    // open = 1 -> full circle, open -> 0 -> flat strip of the same width.
    // cs / sn = rotation of the cross-section around the track axis (depends on the seam lane).
    static void Surface(float k, float R, float open, float cs, float sn,
                        out Vector3 pos, out Vector3 normal)
    {
        float phi = (k - LaneCount * 0.5f) * LaneAngle;   // -PI..PI, 0 at the pivot opposite the seam

        float px, py, nx, ny;
        if (open < 0.001f)
        {
            px = R * phi; py = -R;
            nx = 0f; ny = 1f;
        }
        else
        {
            float th = open * phi;
            float s = Mathf.Sin(th), c = Mathf.Cos(th);
            px = R * s / open;
            py = -R + R * (1f - c) / open;
            nx = -s; ny = c;
        }

        pos = new Vector3(px * cs - py * sn, px * sn + py * cs, 0f);
        normal = new Vector3(nx * cs - ny * sn, nx * sn + ny * cs, 0f);
    }

    // ---------- lifecycle ----------

    void Start()
    {
        if (target == null)
        {
            Debug.LogError("TunnelGenerator: assign a target (the player).");
            enabled = false;
            return;
        }

        playerRef = target.GetComponent<TunnelPlayer>();
        if (playerRef == null)
        {
            Debug.LogError("TunnelGenerator: the target needs a TunnelPlayer component.");
            enabled = false;
            return;
        }

        EnsureInit();
        BuildSharedData();
        runtimeMaterial = CreateMaterial();
        nextS = Mathf.Floor(playerRef.Distance / segmentLength) * segmentLength
                - segmentsBehind * segmentLength;
    }

    void Update()
    {
        float s = playerRef.Distance;

        while (nextS < s + segmentsAhead * segmentLength)
        {
            Segment seg;
            if (active.Count > 0 && active.Peek().startS + segmentLength < s - segmentsBehind * segmentLength)
                seg = active.Dequeue();     // recycle the oldest one
            else
                seg = CreateSegment();

            BuildSegment(seg, nextS);
            active.Enqueue(seg);
            nextS += segmentLength;
        }

        if (target.position.sqrMagnitude > shiftThreshold * shiftThreshold) ShiftOrigin();
    }

    // ---------- floating origin ----------

    // Moves the whole world so the player is back near (0,0,0). Distances along the track (s) don't change.
    void ShiftOrigin()
    {
        Vector3 shift = target.position;

        for (int i = 0; i < pathPos.Count; i++) pathPos[i] -= shift;
        foreach (var seg in active) seg.go.transform.position -= shift;
        target.position -= shift;

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

    void BuildSegment(Segment seg, float startS)
    {
        seg.startS = startS;

        // the segment object sits at the start of its piece of centre line; vertices are relative to it
        FrameAt(startS, out Vector3 segOrigin, out _);
        seg.go.transform.position = segOrigin;
        seg.go.transform.rotation = Quaternion.identity;

        float ds = segmentLength / ringsPerSegment;
        int i = 0;

        for (int r = 0; r <= ringsPerSegment; r++)
        {
            float s = startS + r * ds;
            float m = OpennessAt(s);
            float radius = RadiusFor(s, m);
            float v = (float)r / ringsPerSegment * crossBandsPerSegment;

            FrameAt(s, out Vector3 ringOrigin, out Quaternion q);
            Vector3 rel = ringOrigin - segOrigin;

            int seam = SeamLaneAt(s);
            float rot = (seam - CanonicalSeam) * LaneAngle;
            float cs = Mathf.Cos(rot), sn = Mathf.Sin(rot);

            for (int l = 0; l < LaneCount; l++)
            {
                // position of this lane across the track (the ring is cut at the seam lane)
                int cell = (l - seam + LaneCount) % LaneCount;

                for (int st = 0; st <= stepsPerLane; st++)
                {
                    float tLane = (float)st / stepsPerLane;
                    Surface(cell + tLane, radius, 1f - m, cs, sn, out Vector3 p, out Vector3 n);

                    vertices[i] = rel + q * p;
                    normals[i] = q * n;
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