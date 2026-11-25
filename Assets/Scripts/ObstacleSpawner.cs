using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns rows of cube obstacles on the track lanes ahead of the player.
/// - The number of cubes per row ramps up slowly with distance, then stays constant.
/// - Every row keeps a guaranteed free "safe path" (it moves at most one lane between rows),
///   so the course can always be completed.
/// - Collision is a cheap analytic check (lane + Z overlap): no colliders or rigidbodies needed.
/// - Look: dark glossy cubes with glowing neon edge lines (Tron style).
/// </summary>
public class ObstacleSpawner : MonoBehaviour
{
    const int Lanes = TunnelGenerator.LaneCount;

    class Obstacle
    {
        public Transform tf;
        public Renderer rend;
        public float z;     // scene-space Z of the cube centre
        public int cell;    // position across the track, 0..15, counted from the seam lane
        public int seam;    // seam lane that "cell" is counted from
    }

    [Header("References")]
    public TunnelGenerator tunnel;
    public TunnelPlayer player;
    public Material obstacleMaterial;   // optional: if set it is used as-is, otherwise the neon material is created

    [Header("Cube size")]
    [Range(0.2f, 1f)] public float widthFraction = 0.7f;   // of one lane's width
    public float height = 1.5f;
    public float length = 2f;

    [Header("Look")]
    [Tooltip("Row colours: the first is used at the start, the last at the highest difficulty.")]
    public Color[] palette =
    {
        new Color(0.15f, 0.85f, 1.00f),   // cyan   (easy)
        new Color(0.55f, 0.40f, 1.00f),   // violet
        new Color(1.00f, 0.25f, 0.75f),   // pink
        new Color(1.00f, 0.55f, 0.10f)    // orange (hard)
    };
    [Range(0f, 0.6f)] public float bodyGlow = 0.2f;   // self-lighting of the faces: higher = brighter, never dark
    public float edgeBrightness = 2.5f;               // glow of the edge lines

    [Header("Spawning")]
    public float firstRowOffset = 80f;    // first row appears this far ahead of the start
    public float spawnAhead = 200f;       // keep rows spawned this far ahead (stay below the tunnel view distance)
    public float despawnBehind = 15f;
    public float minRowSpacing = 16f;     // minimum distance between rows
    public float minRowTime = 0.45f;      // ...and at least this many seconds apart at the current speed

    [Header("Difficulty (obstacles per row)")]
    public int startObstaclesPerRow = 1;
    public int maxObstaclesPerRow = 5;    // hard cap, difficulty stays constant after this
    public float rampDistance = 15000f;   // distance at which the cap is reached (same as the speed ramp)

    [Header("Density noise (empty stretches and packed stretches)")]
    public bool useNoise = true;
    public float noiseScale = 350f;                 // distance of one noise "wave": smaller = shorter gaps and bursts
    [Range(0f, 1f)] public float emptyBelow = 0.4f; // noise below this value: no obstacles at all
    [Range(0f, 1f)] public float fullAbove = 0.6f;  // noise above this value: the maximum number of obstacles
    float noiseOffset;                              // random per run, so every run has a different pattern

    readonly List<Obstacle> active = new List<Obstacle>();
    readonly Stack<Obstacle> pool = new Stack<Obstacle>();
    readonly int[] cellBuffer = new int[Lanes];
    Material[] mats;
    float nextRowZ;
    int safeCell = -1;
    int rowSeam;

    static int Wrap(int v) => ((v % Lanes) + Lanes) % Lanes;

    // ---------- lifecycle ----------

    void Start()
    {
        if (tunnel == null || player == null)
        {
            Debug.LogError("ObstacleSpawner: assign Tunnel and Player.");
            enabled = false;
            return;
        }

        // Collision is checked by the player, which needs a reference to this spawner.
        // Connect it automatically so a missing inspector reference can't silently disable collisions.
        if (player.obstacles != this)
        {
            if (player.obstacles == null)
                Debug.Log("ObstacleSpawner: the player had no 'Obstacles' reference, connected it automatically.", this);
            player.obstacles = this;
        }

        noiseOffset = Random.Range(0f, 1000f);
        mats = CreateMaterials();
        tunnel.OriginShifted += OnOriginShifted;
        nextRowZ = player.Distance + firstRowOffset;   // positions along the track are distances (s)
    }

    void OnDestroy()
    {
        if (tunnel != null) tunnel.OriginShifted -= OnOriginShifted;
    }

    void Update()
    {
        if (player.IsDead) return;

        float pz = player.Distance;

        // recycle obstacles that are behind the player
        while (active.Count > 0 && active[0].z + length * 0.5f < pz - despawnBehind)
        {
            var o = active[0];
            active.RemoveAt(0);
            o.tf.gameObject.SetActive(false);
            pool.Push(o);
        }

        // spawn new rows ahead
        while (nextRowZ < pz + spawnAhead)
        {
            SpawnRow(nextRowZ);
            nextRowZ += Mathf.Max(minRowSpacing, player.forwardSpeed * minRowTime);
        }
    }

    // The world was re-centred: move the cubes with it. Distances along the track (o.z) don't change.
    void OnOriginShifted(Vector3 shift)
    {
        foreach (var o in active) o.tf.position -= shift;
    }

    // ---------- difficulty ----------

    /// <summary>0 at the start, 1 once the maximum difficulty is reached.</summary>
    public float DifficultyT => Mathf.Clamp01(player.Distance / Mathf.Max(1f, rampDistance));

    /// <summary>
    /// 0..1 density at a row: Perlin noise over the distance travelled.
    /// Below emptyBelow = 0 (no obstacles), above fullAbove = 1 (maximum), smooth in between.
    /// </summary>
    float DensityAt(float rowZ)
    {
        if (!useNoise) return 1f;

        float rowDistance = rowZ;   // distance along the track
        float n = Mathf.PerlinNoise(noiseOffset + rowDistance / Mathf.Max(1f, noiseScale), 0.37f);
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(emptyBelow, fullAbove, n));
    }

    int ObstacleCount(float rowZ)
    {
        // the ceiling rises slowly with distance (start -> max), the noise decides how much of it is used
        float cap = Mathf.Lerp(startObstaclesPerRow, maxObstaclesPerRow, DifficultyT);
        float target = cap * DensityAt(rowZ);
        int c = Mathf.FloorToInt(target);
        if (Random.value < target - c) c++;          // fractional part = chance of one extra cube
        return Mathf.Clamp(c, 0, Lanes - 1);         // never fill a whole row
    }

    // ---------- spawning ----------

    void SpawnRow(float z)
    {
        // Cells are counted from the lane where the tunnel is cut, which changes every morph cycle,
        // so the safe path is converted to the seam of this row first (keeps the same physical lane).
        int seam = tunnel.SeamLaneAt(z);

        // the safe path: first row starts at the player's lane, then wanders by at most one lane per row
        if (safeCell < 0)
        {
            safeCell = Wrap(player.CurrentCell + player.CurrentSeam - seam);
        }
        else
        {
            if (seam != rowSeam) safeCell = Wrap(safeCell + rowSeam - seam);
            safeCell = Mathf.Clamp(safeCell + Random.Range(-1, 2), 0, Lanes - 1);
        }
        rowSeam = seam;

        int n = 0;
        for (int c = 0; c < Lanes; c++)
            if (c != safeCell) cellBuffer[n++] = c;

        int colorIndex = PickColor();                // one colour per row
        int count = Mathf.Min(ObstacleCount(z), n);
        for (int i = 0; i < count; i++)
        {
            int j = Random.Range(i, n);              // partial Fisher-Yates shuffle
            (cellBuffer[i], cellBuffer[j]) = (cellBuffer[j], cellBuffer[i]);
            SpawnCube(cellBuffer[i], z, seam, colorIndex);
        }
    }

    void SpawnCube(int cell, float z, int seam, int colorIndex)
    {
        Obstacle o = pool.Count > 0 ? pool.Pop() : CreateObstacle();
        o.rend.sharedMaterial = mats[Mathf.Clamp(colorIndex, 0, mats.Length - 1)];
        o.cell = cell;
        o.seam = seam;
        o.z = z;

        float laneWidth = tunnel.LaneWidthAt(z);
        tunnel.SurfacePoint(cell + 0.5f, z, out Vector3 p, out Vector3 normal, out Vector3 forward);

        o.tf.localScale = new Vector3(laneWidth * widthFraction, height, length);
        o.tf.rotation = Quaternion.LookRotation(forward, normal);
        o.tf.position = p + normal * (height * 0.5f);
        o.tf.gameObject.SetActive(true);

        active.Add(o);
    }

    Obstacle CreateObstacle()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Obstacle";
        Destroy(go.GetComponent<Collider>());        // collision is done analytically
        var rend = go.GetComponent<Renderer>();
        rend.sharedMaterial = mats[0];
        go.transform.SetParent(transform, true);
        return new Obstacle { tf = go.transform, rend = rend };
    }

    // ---------- look ----------

    // Colour follows the difficulty: the palette is walked from first to last, with some mixing
    // so the change is gradual.
    int PickColor()
    {
        if (mats.Length <= 1) return 0;
        float f = DifficultyT * (mats.Length - 1) + Random.Range(-0.6f, 0.6f);
        return Mathf.Clamp(Mathf.RoundToInt(f), 0, mats.Length - 1);
    }

    // One material per palette colour (shared by all cubes of that colour).
    Material[] CreateMaterials()
    {
        if (obstacleMaterial != null)                // your own material is used as-is
            return new[] { new Material(obstacleMaterial) };

        Color[] colors = (palette != null && palette.Length > 0)
            ? palette
            : new[] { new Color(1f, 0.55f, 0.1f) };

        Texture2D albedo = BuildAlbedoTexture();
        Texture2D glow = BuildGlowTexture();

        var result = new Material[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            result[i] = MakeMaterial(colors[i], albedo, glow);
        return result;
    }

    Material MakeMaterial(Color c, Texture2D albedo, Texture2D glow)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);

        m.mainTexture = albedo;      // bright, slightly lighter toward the edges
        m.color = c;

        if (m.HasProperty("_EmissionColor"))
        {
            Color e = Color.Lerp(c, Color.white, 0.35f);   // edges glow a bit lighter than the body
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", glow);
            m.SetColor("_EmissionColor", e * edgeBrightness);
        }
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.45f);   // URP
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.45f);   // Standard
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);          // metal would look dark
        return m;
    }

    // Colour texture: bright body, a little lighter toward the edges (tinted by the palette colour).
    static Texture2D BuildAlbedoTexture()
    {
        const int N = 64;
        var tex = NewTexture(N);
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float d = EdgeDistance(x, y, N);                        // 0 at the edge, 0.5 in the middle
                byte b = (byte)((0.72f + 0.28f * (1f - d * 2f)) * 255f);
                px[y * N + x] = new Color32(b, b, b, 255);
            }
        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }

    // Emission texture: soft glow over the whole face, bright edge lines and a thin inner line.
    Texture2D BuildGlowTexture()
    {
        const int N = 128;
        var tex = NewTexture(N);
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float d = EdgeDistance(x, y, N);

                float rim = Mathf.Clamp01(1f - d / 0.07f);
                rim *= rim;                                              // bright edge line, soft falloff
                float inner = Mathf.Exp(-Mathf.Pow((d - 0.20f) / 0.012f, 2f)) * 0.45f;   // thin inner line
                float body = bodyGlow * (0.8f + 0.4f * (1f - d * 2f));   // faces glow a little, more near the edges

                byte b = (byte)(Mathf.Clamp01(body + rim + inner) * 255f);
                px[y * N + x] = new Color32(b, b, b, 255);
            }
        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }

    static Texture2D NewTexture(int n)
    {
        return new Texture2D(n, n, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4
        };
    }

    static float EdgeDistance(int x, int y, int n)
    {
        float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
        return Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
    }

    // ---------- collision ----------

    /// <summary>
    /// Is the player overlapping any cube? kPlayer is the player's position across the track
    /// (lane units, 0..16). The Z range from zFrom to zTo is swept so fast frames can't tunnel through.
    /// </summary>
    public bool CheckHit(float kPlayer, float zFrom, float zTo, bool wrapAround,
                         float playerHalfWidthLanes, float playerHalfLength, int playerSeam)
    {
        float zMin = Mathf.Min(zFrom, zTo) - playerHalfLength;
        float zMax = Mathf.Max(zFrom, zTo) + playerHalfLength;
        float reach = widthFraction * 0.5f + playerHalfWidthLanes;
        float halfLen = length * 0.5f;

        foreach (var o in active)
        {
            if (o.z + halfLen < zMin || o.z - halfLen > zMax) continue;

            // the obstacle's cell may be counted from a different seam (only near a cycle boundary)
            float d = Mathf.Abs(kPlayer - (o.cell + 0.5f + (o.seam - playerSeam)));
            if (wrapAround)
            {
                d = Mathf.Repeat(d, Lanes);
                d = Mathf.Min(d, Lanes - d);               // the ring wraps while the tunnel is closed
            }
            if (d < reach) return true;
        }
        return false;
    }
}