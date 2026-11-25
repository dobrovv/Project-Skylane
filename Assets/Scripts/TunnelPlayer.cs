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

    [Header("Position")]
    [Range(0, Lanes - 1)] public int startLane = 12;   // lane the player starts on (12 = floor)
    public float hoverHeight = 0.6f;

    [Header("Steering (hold left / right to move freely across the track)")]
    public float lateralSpeed = 9f;           // lanes per second at full steering
    public float lateralAcceleration = 60f;   // how quickly that speed is reached (lanes per second squared)

    public enum TiltStyle
    {
        SteeringWheel,   // rotate the phone like a steering wheel (around the axis pointing out of the screen)
        Roll             // lean it left/right around its long axis (good when held flat or leaned back)
    }

    [Header("Tilt control (phone)")]
    public bool useTilt = true;                       // steer by tilting the phone; keys / stick are used when there is no sensor
    public TiltStyle tiltStyle = TiltStyle.SteeringWheel;
    public float maxTiltDegrees = 25f;                // tilt that gives full steering speed
    public float deadZoneDegrees = 3f;                // tiny tilts are ignored
    public float tiltSmoothing = 15f;                 // higher = more direct, lower = smoother
    public bool invertTilt = false;                   // tick this if it steers the wrong way
    public float calibrationDelay = 0.4f;             // seconds after the start until the holding position is captured

    [Header("Speed (rises slowly with distance, then stays constant)")]
    public float startSpeed = 20f;
    public float maxSpeed = 60f;
    public float speedRampDistance = 15000f;   // distance at which maxSpeed is reached
    public float forwardSpeed = 20f;           // current speed (set automatically)

    [Header("Game over")]
    public float fallGravity = 30f;
    public UnityEvent onGameOver;

    [Header("Obstacles")]
    public ObstacleSpawner obstacles;
    public float halfWidthLanes = 0.2f;   // player half width, in lane units
    public float halfLength = 0.6f;       // player half length, in world units

    public bool IsDead { get; private set; }
    public float Distance { get; private set; }

    /// <summary>Cell across the track (0..15) the player is currently over.</summary>
    public int CurrentCell => Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(kPos, Lanes)), 0, Lanes - 1);

    int currentSeam;

    /// <summary>The lane the tunnel is cut at around the player (cells are counted from it).</summary>
    public int CurrentSeam => currentSeam;

    float kPos;         // position across the track in lane units, unwrapped (can exceed 0..16)
    float lateralVel;   // sideways speed in lanes per second

    // tilt control
    Quaternion tiltReference;   // how the phone was held when the game started
    bool tiltCalibrated;
    float tiltStartTime;
    float tiltSmoothed;         // smoothed tilt angle in degrees
    Vector3 fallVelocity;
    Vector3 tumble;
    float deathTime;
    bool crashed;
    string deathReason = "";
    GUIStyle hudStyle, bigStyle;

    void Start()
    {
        forwardSpeed = startSpeed;
        Recalibrate();

        // collisions are checked by the spawner: find it if the reference was left empty
        if (obstacles == null)
        {
#if UNITY_2023_1_OR_NEWER
            obstacles = FindFirstObjectByType<ObstacleSpawner>();
#else
            obstacles = FindObjectOfType<ObstacleSpawner>();
#endif
        }

        // lane id -> cell across the track (cells are counted from the lane where the tunnel is cut)
        currentSeam = tunnel != null ? tunnel.SeamLaneAt(0f) : 0;
        kPos = startLane - currentSeam + 0.5f;   // centre of the start lane
    }

    void Update()
    {
        if (tunnel == null) return;

        if (IsDead)
        {
            UpdateFall();
            if (Time.time - deathTime > 0.6f && RestartPressed())
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            return;
        }

        float dt = Time.deltaTime;
        // speed grows linearly with distance until speedRampDistance, then stays at maxSpeed
        forwardSpeed = Mathf.Lerp(startSpeed, maxSpeed, Mathf.Clamp01(Distance / Mathf.Max(1f, speedRampDistance)));
        // "s" = distance along the (bending) track. It replaces the old world Z coordinate.
        float s0 = Distance;
        Distance += forwardSpeed * dt;
        float s = Distance;

        // The seam lane changes between morph cycles (always while the tunnel is closed).
        // Keep the player on the same physical lane by shifting the cell numbers.
        int seam = tunnel.SeamLaneAt(s);
        if (seam != currentSeam)
        {
            kPos += currentSeam - seam;
            currentSeam = seam;
        }

        float openness = tunnel.OpennessAt(s);
        bool seamIsGap = openness > tunnel.seamOpenThreshold;

        // --- steering: hold left / right to slide freely across the track ---
        float steer = ReadSteer();   // -1..1
        lateralVel = Mathf.MoveTowards(lateralVel, steer * lateralSpeed, lateralAcceleration * dt);

        float prevK = kPos;
        kPos += lateralVel * dt;

        // While the tunnel is closed the ring wraps around. Once the seam is an open gap
        // (tunnel opening / flat road), crossing it means crossing the edge of the road.
        bool fellOff = seamIsGap && Mathf.FloorToInt(kPos / Lanes) != Mathf.FloorToInt(prevK / Lanes);

        float k;
        if (seamIsGap)
        {
            int wrap = Mathf.FloorToInt(prevK / Lanes);
            k = Mathf.Clamp(kPos - wrap * Lanes, 0f, Lanes);   // stay on the sheet (at the edge if we crossed it)
        }
        else
        {
            k = Mathf.Repeat(kPos, Lanes);                      // ring wraps around
        }

        // --- placement: stand on the surface, up = surface normal ---
        // forward = direction of travel along the bending track, up = surface normal
        tunnel.SurfacePoint(k, s, out Vector3 pos, out Vector3 normal, out Vector3 forward);
        transform.position = pos + normal * hoverHeight;
        transform.rotation = Quaternion.LookRotation(forward, normal);

        if (fellOff)
            Die(lateralVel >= 0f ? 1 : -1, false, "You fell off the edge");
        else if (obstacles != null && obstacles.CheckHit(k, s0, s, !seamIsGap, halfWidthLanes, halfLength, currentSeam))
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
            fallVelocity = transform.forward * forwardSpeed * 0.6f + transform.right * steerDir * 4f;
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

    /// <summary>Takes the current way the phone is held as "straight". Called automatically at the start.</summary>
    public void Recalibrate()
    {
        tiltCalibrated = false;
        tiltStartTime = Time.time;
        tiltSmoothed = 0f;
    }

    // -1 (left) .. 1 (right)
    float ReadSteer()
    {
#if ENABLE_INPUT_SYSTEM
        // phone: tilt left / right
        if (useTilt && TryReadTilt(out float tiltSteer)) return tiltSteer;

        // no sensor (editor / desktop): keys or gamepad stick
        float a = 0f;
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) a += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) a -= 1f;
        }
        var gp = Gamepad.current;
        if (gp != null)
        {
            float x = gp.leftStick.x.ReadValue();
            if (Mathf.Abs(x) > 0.2f) a += x;
        }
        return Mathf.Clamp(a, -1f, 1f);
#else
        return Mathf.Clamp(Input.GetAxisRaw("Horizontal"), -1f, 1f);   // A/D, arrows, gamepad stick
#endif
    }

#if ENABLE_INPUT_SYSTEM
    // Steering from the phone's AttitudeSensor. Returns false when the device has no such sensor.
    bool TryReadTilt(out float steer)
    {
        steer = 0f;

        var sensor = AttitudeSensor.current;
        if (sensor == null) return false;
        if (!sensor.enabled) InputSystem.EnableDevice(sensor);   // sensors are off until enabled

        Quaternion q = sensor.attitude.ReadValue();
        if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w < 0.5f) return true;   // no valid reading yet

        // remember how the phone was held at the start: that is "straight"
        if (!tiltCalibrated)
        {
            if (Time.time - tiltStartTime < calibrationDelay) return true;
            tiltReference = q;
            tiltCalibrated = true;
        }

        // how the phone has rotated since calibration, in the phone's own axes
        Quaternion rel = Quaternion.Inverse(tiltReference) * q;

        // the part of that rotation that goes around one axis (z = out of the screen, y = long axis)
        Vector3 axis = tiltStyle == TiltStyle.SteeringWheel ? Vector3.forward : Vector3.up;
        float angle = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(rel.x, rel.y, rel.z), axis), rel.w) * Mathf.Rad2Deg;
        if (angle > 180f) angle -= 360f;
        else if (angle < -180f) angle += 360f;
        if (invertTilt) angle = -angle;

        // smooth out sensor jitter, apply the dead zone, and scale to -1..1
        tiltSmoothed = Mathf.Lerp(tiltSmoothed, angle, 1f - Mathf.Exp(-tiltSmoothing * Time.deltaTime));
        float full = Mathf.Max(deadZoneDegrees + 1f, maxTiltDegrees);
        float amount = Mathf.InverseLerp(deadZoneDegrees, full, Mathf.Abs(tiltSmoothed));
        steer = Mathf.Sign(tiltSmoothed) * amount;
        return true;
    }
#endif

    bool RestartPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && (kb.rKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) return true;

        var touch = Touchscreen.current;                   // tap anywhere on the phone
        return touch != null && touch.primaryTouch.press.wasPressedThisFrame;
#else
        if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Space)) return true;
        return Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began;
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
                      $"{deathReason}  |  {Distance:0} m  |  tap the screen (or press R) to restart",
                      new GUIStyle(hudStyle) { alignment = TextAnchor.MiddleCenter });
        }
    }
}