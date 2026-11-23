using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Rides along one of the 16 lanes of the tunnel / road.
/// Game over when the player crosses the track edge (the open seam of the tunnel
/// or either side of the flat road) and falls off.
/// </summary>
public class TunnelPlayer : MonoBehaviour
{
    const int Lanes = TunnelGenerator.LaneCount;

    public TunnelGenerator tunnel;

    [Header("Lane")]
    [Range(0, Lanes - 1)] public int startLane = 12;   // 12 = floor, centre-ish
    public float laneChangeTime = 0.12f;
    public float hoverHeight = 0.6f;

    [Header("Speed")]
    public float forwardSpeed = 20f;
    public float acceleration = 0.3f;
    public float maxSpeed = 60f;

    [Header("Game over")]
    public float fallGravity = 30f;
    public UnityEvent onGameOver;

    [Header("Obstacles")]
    public ObstacleSpawner obstacles;
    public float halfWidthLanes = 0.2f;   // player half width, in lane units
    public float halfLength = 0.6f;       // player half length, in world units

    public bool IsDead { get; private set; }
    public float Distance { get; private set; }

    /// <summary>Cell across the track (0..15) the player is currently aiming for.</summary>
    public int CurrentCell => ((kExt % Lanes) + Lanes) % Lanes;

    int kExt;           // unwrapped position across the track (cell index, can exceed 0..15)
    float kCur, kVel;   // smoothed version, in lane units
    Vector3 fallVelocity;
    Vector3 tumble;
    float deathTime;
    bool crashed;
    string deathReason = "";
    GUIStyle hudStyle, bigStyle;

    void Start()
    {
        // lane id -> cell across the track (the ring is cut right before lane 4)
        kExt = startLane - TunnelGenerator.SeamLane;
        kCur = kExt + 0.5f;
    }

    void Update()
    {
        if (tunnel == null) return;

        if (IsDead)
        {
            UpdateFall();
            if (RestartPressed())
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            return;
        }

        float dt = Time.deltaTime;
        forwardSpeed = Mathf.Min(maxSpeed, forwardSpeed + acceleration * dt);
        float z0 = transform.position.z;
        float z = z0 + forwardSpeed * dt;
        Distance += forwardSpeed * dt;

        float openness = tunnel.OpennessAt(z);
        bool seamIsGap = openness > tunnel.seamOpenThreshold;

        // --- steering ---
        int steer = ReadSteer();
        bool fellOff = false;
        if (steer != 0)
        {
            int oldWrap = Mathf.FloorToInt(kExt / (float)Lanes);
            kExt += steer;
            int newWrap = Mathf.FloorToInt(kExt / (float)Lanes);

            // Stepping over the seam is fine while the tunnel is closed,
            // but once it is open that is the edge of the road.
            if (newWrap != oldWrap && seamIsGap) fellOff = true;
        }

        kCur = Mathf.SmoothDamp(kCur, kExt + 0.5f, ref kVel, laneChangeTime);

        float k;
        if (seamIsGap)
        {
            int wrap = Mathf.FloorToInt(kExt / (float)Lanes);
            k = Mathf.Clamp(kCur - wrap * Lanes, 0f, Lanes);   // stay on the sheet
        }
        else
        {
            k = Mathf.Repeat(kCur, Lanes);                      // ring wraps around
        }

        // --- placement: stand on the surface, up = surface normal ---
        tunnel.SurfacePoint(k, z, out Vector3 pos, out Vector3 normal);
        transform.position = pos + normal * hoverHeight;
        transform.rotation = Quaternion.LookRotation(Vector3.forward, normal);

        if (fellOff)
            Die(steer, false, "You fell off the edge");
        else if (obstacles != null && obstacles.CheckHit(k, z0, z, !seamIsGap, halfWidthLanes, halfLength))
            Die(0, true, "You hit an obstacle");
    }

    void Die(int steerDir, bool hitObstacle, string reason)
    {
        IsDead = true;
        crashed = hitObstacle;
        deathReason = reason;
        deathTime = Time.time;

        if (!hitObstacle)
        {
            // keep the forward momentum and get pushed outward a bit
            fallVelocity = Vector3.forward * forwardSpeed * 0.6f + transform.right * steerDir * 4f;
            tumble = new Vector3(Random.Range(-90f, 90f), Random.Range(-90f, 90f), Random.Range(-180f, 180f));
        }

        onGameOver?.Invoke();
    }

    void UpdateFall()
    {
        if (crashed) return;   // hit a cube: stop dead in place

        float dt = Time.deltaTime;
        fallVelocity += Vector3.down * fallGravity * dt;
        transform.position += fallVelocity * dt;
        transform.Rotate(tumble * dt, Space.Self);
    }

    // ---------- input ----------

    int ReadSteer()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return 0;
        if (kb.dKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame) return 1;
        if (kb.aKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame) return -1;
        return 0;
#else
        if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) return 1;
        if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) return -1;
        return 0;
#endif
    }

    bool RestartPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        return kb != null && (kb.rKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame);
#else
        return Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Space);
#endif
    }

    // ---------- minimal HUD ----------

    void OnGUI()
    {
        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            hudStyle.normal.textColor = Color.white;
            bigStyle = new GUIStyle(hudStyle) { fontSize = 52, alignment = TextAnchor.MiddleCenter };
        }

        GUI.Label(new Rect(20, 15, 500, 40), $"Distance: {Distance:0} m", hudStyle);

        if (IsDead && Time.time - deathTime > 0.6f)
        {
            GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 80), "GAME OVER", bigStyle);
            GUI.Label(new Rect(0, Screen.height * 0.35f + 80, Screen.width, 50),
                      $"{deathReason}  |  {Distance:0} m  |  press R or Space to restart",
                      new GUIStyle(hudStyle) { alignment = TextAnchor.MiddleCenter });
        }
    }
}