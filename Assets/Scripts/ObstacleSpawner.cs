using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns rows of cube obstacles on the track lanes ahead of the player.
/// - The number of cubes per row ramps up slowly with distance, then stays constant.
/// - Every row keeps a guaranteed free "safe path" (it moves at most one lane between rows),
///   so the course can always be completed.
/// - Collision is a cheap analytic check (lane + Z overlap): no colliders or rigidbodies needed.
/// </summary>
public class ObstacleSpawner : MonoBehaviour
{
    const int Lanes = TunnelGenerator.LaneCount;

    class Obstacle
    {
        public Transform tf;
        public float z;     // scene-space Z of the cube centre
        public int cell;    // position across the track, 0..15
    }

    [Header("References")]
    public TunnelGenerator tunnel;
    public TunnelPlayer player;
    public Material obstacleMaterial;   // optional, a red material is created if empty

    [Header("Cube size")]
    [Range(0.2f, 1f)] public float widthFraction = 0.7f;   // of one lane's width
    public float height = 1.5f;
    public float length = 2f;

    [Header("Spawning")]
    public float firstRowOffset = 80f;    // first row appears this far ahead of the start
    public float spawnAhead = 200f;       // keep rows spawned this far ahead (stay below the tunnel view distance)
    public float despawnBehind = 15f;
    public float minRowSpacing = 16f;     // minimum distance between rows
    public float minRowTime = 0.45f;      // ...and at least this many seconds apart at the current speed

    [Header("Difficulty (obstacles per row)")]
    public int startObstaclesPerRow = 1;
    public int maxObstaclesPerRow = 8;    // hard cap, difficulty stays constant after this
    public float rampDistance = 3000f;    // distance at which the cap is reached

    readonly List<Obstacle> active = new List<Obstacle>();
    readonly Stack<Obstacle> pool = new Stack<Obstacle>();
    readonly int[] cellBuffer = new int[Lanes];
    Material mat;
    float nextRowZ;
    int safeCell = -1;

    // ---------- lifecycle ----------

    void Start()
    {
        if (tunnel == null || player == null)
        {
            Debug.LogError("ObstacleSpawner: assign Tunnel and Player.");
            enabled = false;
            return;
        }

        mat = obstacleMaterial != null ? new Material(obstacleMaterial) : CreateDefaultMaterial();
        tunnel.OriginShifted += OnOriginShifted;
        nextRowZ = player.transform.position.z + firstRowOffset;
    }

    void OnDestroy()
    {
        if (tunnel != null) tunnel.OriginShifted -= OnOriginShifted;
    }

    void Update()
    {
        if (player.IsDead) return;

        float pz = player.transform.position.z;

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

    void OnOriginShifted(float shift)
    {
        nextRowZ -= shift;
        foreach (var o in active)
        {
            o.z -= shift;
            o.tf.position -= new Vector3(0, 0, shift);
        }
    }

    // ---------- difficulty ----------

    /// <summary>0 at the start, 1 once the maximum difficulty is reached.</summary>
    public float DifficultyT => Mathf.Clamp01(player.Distance / Mathf.Max(1f, rampDistance));

    int ObstacleCount()
    {
        float target = Mathf.Lerp(startObstaclesPerRow, maxObstaclesPerRow, DifficultyT);
        int c = Mathf.FloorToInt(target);
        if (Random.value < target - c) c++;          // fractional part = chance of one extra cube
        return Mathf.Clamp(c, 0, Lanes - 1);         // never fill a whole row
    }

    // ---------- spawning ----------

    void SpawnRow(float z)
    {
        // the safe path: first row starts at the player's lane, then wanders by at most one lane per row
        if (safeCell < 0) safeCell = player.CurrentCell;
        else safeCell = Mathf.Clamp(safeCell + Random.Range(-1, 2), 0, Lanes - 1);

        int n = 0;
        for (int c = 0; c < Lanes; c++)
            if (c != safeCell) cellBuffer[n++] = c;

        int count = Mathf.Min(ObstacleCount(), n);
        for (int i = 0; i < count; i++)
        {
            int j = Random.Range(i, n);              // partial Fisher-Yates shuffle
            (cellBuffer[i], cellBuffer[j]) = (cellBuffer[j], cellBuffer[i]);
            SpawnCube(cellBuffer[i], z);
        }
    }

    void SpawnCube(int cell, float z)
    {
        Obstacle o = pool.Count > 0 ? pool.Pop() : CreateObstacle();
        o.cell = cell;
        o.z = z;

        float laneWidth = tunnel.LaneWidthAt(z);
        tunnel.SurfacePoint(cell + 0.5f, z, out Vector3 p, out Vector3 normal);

        o.tf.localScale = new Vector3(laneWidth * widthFraction, height, length);
        o.tf.rotation = Quaternion.LookRotation(Vector3.forward, normal);
        o.tf.position = p + normal * (height * 0.5f);
        o.tf.gameObject.SetActive(true);

        active.Add(o);
    }

    Obstacle CreateObstacle()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Obstacle";
        Destroy(go.GetComponent<Collider>());        // collision is done analytically
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.transform.SetParent(transform, true);
        return new Obstacle { tf = go.transform };
    }

    Material CreateDefaultMaterial()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        m.color = new Color(1f, 0.25f, 0.2f);
        return m;
    }

    // ---------- collision ----------

    /// <summary>
    /// Is the player overlapping any cube? kPlayer is the player's position across the track
    /// (lane units, 0..16). The Z range from zFrom to zTo is swept so fast frames can't tunnel through.
    /// </summary>
    public bool CheckHit(float kPlayer, float zFrom, float zTo, bool wrapAround,
                         float playerHalfWidthLanes, float playerHalfLength)
    {
        float zMin = Mathf.Min(zFrom, zTo) - playerHalfLength;
        float zMax = Mathf.Max(zFrom, zTo) + playerHalfLength;
        float reach = widthFraction * 0.5f + playerHalfWidthLanes;
        float halfLen = length * 0.5f;

        foreach (var o in active)
        {
            if (o.z + halfLen < zMin || o.z - halfLen > zMax) continue;

            float d = Mathf.Abs(kPlayer - (o.cell + 0.5f));
            if (wrapAround) d = Mathf.Min(d, Lanes - d);   // the ring wraps while the tunnel is closed
            if (d < reach) return true;
        }
        return false;
    }
}