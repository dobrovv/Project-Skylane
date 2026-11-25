using UnityEngine;

/// <summary>
/// Procedural starfield background: thousands of tiny stars with different sizes, brightness and tints,
/// a faint Milky Way band, soft nebula glows and gentle twinkling.
///
/// Setup: add this script to an empty GameObject. That's it (it finds the main camera by itself).
///
/// How it works: one mesh of small quads is placed on a huge sphere around the camera. The sphere follows
/// the camera's position but not its rotation, so the stars look infinitely far away and the sky swings
/// around when the track bends. Everything is generated in code (no textures, no assets), and the
/// "Sprites/Default" shader is used, which works in the built-in pipeline and in URP.
/// The sky is only visible where the track is open: through the tunnel gap and above the flat road.
/// </summary>
public class Starfield : MonoBehaviour
{
    [Header("Setup")]
    public Camera cam;                           // defaults to the main camera
    public bool setCameraBackground = true;      // deep space colour behind the stars
    public Color backgroundColor = new Color(0.008f, 0.010f, 0.030f);
    public float radius = 800f;                  // automatically kept inside the camera's far clip plane

    [Header("Stars")]
    public int starCount = 1800;
    [Range(0.0005f, 0.02f)] public float minStarSize = 0.0025f;   // angular size in radians (0.0025 is about 0.15 degrees)
    [Range(0.0005f, 0.03f)] public float maxStarSize = 0.009f;
    [Range(0f, 1f)] public float colorVariety = 0.6f;             // 0 = all stars white
    [Range(0f, 1f)] public float bandAmount = 0.45f;              // share of the stars packed into the Milky Way band

    [Header("Twinkle")]
    public bool twinkle = true;
    [Range(0f, 1f)] public float twinkleShare = 0.5f;             // share of the stars that twinkle
    [Range(0f, 1f)] public float twinkleDepth = 0.5f;             // how much they dim at the weakest point
    public float twinkleSpeed = 2.5f;

    [Header("Nebula glow")]
    public bool nebulae = true;
    public int nebulaCount = 22;
    [Range(0f, 0.3f)] public float nebulaStrength = 0.1f;         // maximum opacity of the soft clouds
    public Color[] nebulaColors =
    {
        new Color(0.15f, 0.25f, 0.90f),   // blue
        new Color(0.50f, 0.20f, 0.85f),   // purple
        new Color(0.10f, 0.60f, 0.70f),   // teal
        new Color(0.80f, 0.20f, 0.55f)    // magenta
    };

    [Header("Motion")]
    public float driftDegreesPerSecond = 0.4f;   // slow rotation of the whole sky (0 = still)

    [Header("Random")]
    public int seed = 12345;                     // same seed = same sky every time

    // star tints: blue-white, white, pale yellow, orange, soft pink
    static readonly Color[] StarTints =
    {
        new Color(0.70f, 0.80f, 1.00f),
        new Color(1.00f, 1.00f, 1.00f),
        new Color(1.00f, 0.95f, 0.75f),
        new Color(1.00f, 0.75f, 0.55f),
        new Color(1.00f, 0.80f, 0.90f)
    };

    // the Milky Way band: a tilted great circle
    Vector3 bandN, bandU, bandW;

    Mesh mesh;
    Color32[] colors;
    int starFirstVertex;                // vertices before this index belong to nebula clouds
    float[] baseBrightness, twinkleAmount, twinklePhase, twinkleRate;
    float nextTwinkleUpdate;
    float sphereRadius;

    // ---------- lifecycle ----------

    void Start()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null)
        {
            Debug.LogError("Starfield: no camera found. Assign one in the inspector.", this);
            enabled = false;
            return;
        }

        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null)
        {
            Debug.LogError("Starfield: shader 'Sprites/Default' not found.", this);
            enabled = false;
            return;
        }

        if (setCameraBackground)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = backgroundColor;
        }

        sphereRadius = Mathf.Min(radius, cam.farClipPlane * 0.95f);

        var material = new Material(sh) { mainTexture = BuildAtlas() };
        BuildMesh();

        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = gameObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        transform.position = cam.transform.position;
    }

    void LateUpdate()
    {
        if (cam == null) return;

        // follow the camera's position (not its rotation): the stars are infinitely far away
        transform.position = cam.transform.position;
        if (driftDegreesPerSecond != 0f)
            transform.Rotate(new Vector3(0.3f, 1f, 0.1f).normalized, driftDegreesPerSecond * Time.deltaTime, Space.World);

        if (twinkle && Time.time >= nextTwinkleUpdate)
        {
            nextTwinkleUpdate = Time.time + 0.05f;   // 20 updates per second is plenty
            UpdateTwinkle();
        }
    }

    // ---------- mesh ----------

    void BuildMesh()
    {
        var rng = new System.Random(seed);

        bandN = new Vector3(0.35f, 1f, 0.25f).normalized;
        bandU = Vector3.Cross(bandN, Vector3.forward).normalized;
        bandW = Vector3.Cross(bandN, bandU);

        int nebCount = nebulae ? Mathf.Max(0, nebulaCount) : 0;
        int stars = Mathf.Max(0, starCount);
        int quads = nebCount + stars;

        var verts = new Vector3[quads * 4];
        var uvs = new Vector2[quads * 4];
        colors = new Color32[quads * 4];
        var tris = new int[quads * 6];

        baseBrightness = new float[stars];
        twinkleAmount = new float[stars];
        twinklePhase = new float[stars];
        twinkleRate = new float[stars];
        starFirstVertex = nebCount * 4;

        Color[] nebPalette = (nebulaColors != null && nebulaColors.Length > 0)
            ? nebulaColors
            : new[] { new Color(0.3f, 0.3f, 0.9f) };

        int q = 0;

        // nebula clouds first, so the stars are drawn on top of them
        for (int i = 0; i < nebCount; i++, q++)
        {
            Vector3 dir = RandomDirection(rng, true);
            float size = Mathf.Lerp(0.30f, 0.70f, (float)rng.NextDouble());
            Color c = nebPalette[rng.Next(nebPalette.Length)];
            float a = nebulaStrength * Mathf.Lerp(0.4f, 1f, (float)rng.NextDouble());
            AddQuad(verts, uvs, colors, tris, q, dir, size, new Color32(
                (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(a * 255f)), true);
        }

        // stars
        for (int i = 0; i < stars; i++, q++)
        {
            Vector3 dir = RandomDirection(rng, rng.NextDouble() < bandAmount);

            // many small stars, a few big ones
            float t = Mathf.Pow((float)rng.NextDouble(), 3f);
            float size = Mathf.Lerp(minStarSize, Mathf.Max(minStarSize, maxStarSize), t);
            float bright = Mathf.Clamp01(0.45f + 0.55f * Mathf.Sqrt(t) + 0.15f * (float)rng.NextDouble());

            Color tint = Color.Lerp(Color.white, StarTints[rng.Next(StarTints.Length)], colorVariety);

            baseBrightness[i] = bright;
            twinkleAmount[i] = rng.NextDouble() < twinkleShare ? twinkleDepth * Mathf.Lerp(0.5f, 1f, (float)rng.NextDouble()) : 0f;
            twinklePhase[i] = (float)rng.NextDouble() * Mathf.PI * 2f;
            twinkleRate[i] = Mathf.Lerp(0.6f, 1.6f, (float)rng.NextDouble());

            AddQuad(verts, uvs, colors, tris, q, dir, size, new Color32(
                (byte)(tint.r * 255f), (byte)(tint.g * 255f), (byte)(tint.b * 255f), (byte)(bright * 255f)), false);
        }

        mesh = new Mesh { name = "Starfield" };
        if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.colors32 = colors;
        mesh.triangles = tris;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * sphereRadius * 2.5f);   // never culled
        mesh.MarkDynamic();
    }

    // A small quad on the sphere, facing the centre. The atlas has the star on the left half
    // and the soft cloud on the right half.
    void AddQuad(Vector3[] verts, Vector2[] uvs, Color32[] cols, int[] tris,
                 int q, Vector3 dir, float angularSize, Color32 color, bool cloud)
    {
        Vector3 right = Vector3.Cross(Vector3.up, dir);
        if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(Vector3.forward, dir);
        right.Normalize();
        Vector3 up = Vector3.Cross(dir, right);

        Vector3 center = dir * sphereRadius;
        float h = sphereRadius * angularSize * 0.5f;

        int v = q * 4;
        verts[v + 0] = center - right * h - up * h;
        verts[v + 1] = center + right * h - up * h;
        verts[v + 2] = center - right * h + up * h;
        verts[v + 3] = center + right * h + up * h;

        float u0 = cloud ? 0.5f : 0f, u1 = cloud ? 1f : 0.5f;
        uvs[v + 0] = new Vector2(u0, 0f);
        uvs[v + 1] = new Vector2(u1, 0f);
        uvs[v + 2] = new Vector2(u0, 1f);
        uvs[v + 3] = new Vector2(u1, 1f);

        for (int k = 0; k < 4; k++) cols[v + k] = color;

        int t = q * 6;
        tris[t + 0] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
        tris[t + 3] = v + 2; tris[t + 4] = v + 3; tris[t + 5] = v + 1;
    }

    Vector3 RandomDirection(System.Random rng, bool inBand)
    {
        if (!inBand)
        {
            // uniform on the sphere
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            float a = (float)(rng.NextDouble() * Mathf.PI * 2.0);
            float r = Mathf.Sqrt(1f - z * z);
            return new Vector3(r * Mathf.Cos(a), z, r * Mathf.Sin(a));
        }

        // around the great circle of the band, with a roughly gaussian thickness
        float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0);
        float g = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 2.0) * 1.73f;
        Vector3 d = bandU * Mathf.Cos(ang) + bandW * Mathf.Sin(ang) + bandN * (g * 0.22f);
        return d.normalized;
    }

    // ---------- twinkle ----------

    void UpdateTwinkle()
    {
        float t = Time.time * twinkleSpeed;

        for (int i = 0; i < baseBrightness.Length; i++)
        {
            float amount = twinkleAmount[i];
            if (amount <= 0f) continue;

            float wave = 0.5f + 0.5f * Mathf.Sin(t * twinkleRate[i] + twinklePhase[i]);
            byte a = (byte)(baseBrightness[i] * (1f - amount * wave) * 255f);

            int v = starFirstVertex + i * 4;
            colors[v].a = a; colors[v + 1].a = a; colors[v + 2].a = a; colors[v + 3].a = a;
        }
        mesh.colors32 = colors;
    }

    // ---------- texture ----------

    // One small texture with two pictures: a round star with a bright core (left half)
    // and a very soft, slightly uneven cloud (right half).
    static Texture2D BuildAtlas()
    {
        const int N = 64;
        var tex = new Texture2D(N * 2, N, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var px = new Color32[N * 2 * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N * 2; x++)
            {
                bool cloud = x >= N;
                int lx = cloud ? x - N : x;

                float dx = ((lx + 0.5f) / N - 0.5f) * 2f;
                float dy = ((y + 0.5f) / N - 0.5f) * 2f;
                float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));

                float a;
                if (!cloud)
                {
                    // tight bright core plus a faint halo
                    a = Mathf.Exp(-r * r * 18f) * 0.9f + Mathf.Pow(1f - r, 3f) * 0.3f;
                }
                else
                {
                    float soft = Mathf.SmoothStep(0f, 1f, 1f - r);
                    a = soft * soft * Mathf.Lerp(0.5f, 1.2f, Mathf.PerlinNoise(lx * 0.09f + 5f, y * 0.09f + 5f));
                }

                byte b = (byte)(Mathf.Clamp01(a) * 255f);
                px[y * N * 2 + x] = new Color32(255, 255, 255, b);
            }
        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }
}