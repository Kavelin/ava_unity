using System;
using UnityEngine;
using Mapbox.Utils;
using Mapbox.Unity.Map;

public class MapboxDroneController : MonoBehaviour
{
    [Header("Mapbox")]
    public AbstractMap map;
    public bool useTerrainHeight = true;
    public HeightmapMeshGenerator heightMesh;
    public bool recenterMap = true;
    public bool useTileBasedThreshold = true;
    [Min(0.1f)] public float tilesBeforeRecenter = 4f;
    [Min(0f)] public float recenterDistanceMeters = 200f;
    [Min(0f)] public float recenterCooldownSeconds = 0.5f;
    public bool resetRootOnRecenter = true;

    [Header("Drone Root")]
    public Transform drone;
    public Transform droneVisual;
    public bool applyRotation = true;
    public bool lockRootY = true;

    [Header("Altitude (meters)")]
    public bool useAltitude = true;
    public float altitudeOffsetMeters = 0f;
    public bool altitudeIsAgl = true;

    [Header("Smoothing")]
    public bool enableSmoothing = true;
    [Min(0f)] public float positionSmoothTime = 0.25f;
    [Min(0f)] public float rotationSmoothSpeed = 10f;
    [Min(0f)] public float minMoveMeters = 0.5f;
    [Min(0f)] public float minAltitudeMeters = 0.1f;

    [Header("Debug")]
    public bool logUpdates = false;
    [Min(0f)] public float logInterval = 1f;
    public bool logRecenters = true;
    public bool drawBufferGizmo = false;
    public Color bufferGizmoColor = new Color(0f, 0.6f, 1f, 0.35f);

    [Header("Visual Scaling")]
    [Tooltip("Multiplicative scale applied to horizontal (X,Z) local visual movement for perception tuning.")]
    [Min(0f)] public float visualScale = 1f;
    [Tooltip("Multiplicative scale applied to vertical (Y) local visual movement for perception tuning.")]
    [Min(0f)] public float visualAltitudeScale = 1f;

    bool _hasFix;
    bool _hasTarget;
    bool _warnedNoVisual;
    bool _hasCenter;
    Vector2d _latestLatLon;
    Vector2d _centerLatLon;
    double _latestAlt;
    float _latestRoll;
    float _latestPitch;
    float _latestYaw;
    Vector3 _targetVisualLocal;
    Quaternion _targetRot;
    Vector3 _targetRootPos;
    Vector3 _visualVel;
    float _lastLogTime;
    float _lastRecenterTime;

    void Start()
    {

        if (map == null)
        {
            Debug.LogWarning("MapboxDroneController: Map reference not set.");
            return;
        }

        // Don't try to inject or replace Mapbox factories at runtime.
        // Rely on the AbstractMap inspector configuration. Subscribe to initialization instead.
        map.OnInitialized += () =>
        {
            if (logUpdates) Debug.Log("MapboxDroneController: Map initialized.");
        };
    }

    public void ApplyGpsFix(double latitude, double longitude, double altitudeMeters, float rollDeg, float pitchDeg, float yawDeg)
    {
        if (map == null || drone == null)
        {
            Debug.LogWarning("MapboxDroneController: Assign map and drone before calling ApplyGpsFix.");
            return;
        }

        if (double.IsNaN(latitude) || double.IsNaN(longitude) || Math.Abs(latitude) > 90.0 || Math.Abs(longitude) > 180.0)
        {
            Debug.LogWarning($"MapboxDroneController: Invalid GPS fix lat={latitude}, lon={longitude}");
            return;
        }

        _latestLatLon = new Vector2d(latitude, longitude);
        _latestAlt = altitudeMeters;
        _latestRoll = rollDeg;
        _latestPitch = pitchDeg;
        _latestYaw = yawDeg;
        _hasFix = true;
    }

    public Vector2d ToVector2d(double latitude, double longitude)
    {
        return new Vector2d(latitude, longitude);
    }

    void Update()
    {
        if (!_hasFix || map == null || drone == null)
        {
            return;
        }

        if (recenterMap)
        {
            if (!_hasCenter)
            {
                RecenterTo(_latestLatLon);
            }
            else
            {
                float distMeters = (float)HaversineMeters(_centerLatLon, _latestLatLon);
                float thresholdMeters = GetRecenterThresholdMeters();
                float timeSince = Time.time - _lastRecenterTime;
                if (distMeters >= thresholdMeters && timeSince >= recenterCooldownSeconds)
                {
                    RecenterTo(_latestLatLon);
                }
            }
        }

        if (resetRootOnRecenter)
        {
            drone.position = Vector3.zero;
        }

        if (recenterMap)
        {
            if (!_hasCenter)
            {
                UpdateMapCenter(_latestLatLon);
                _centerLatLon = _latestLatLon;
                _hasCenter = true;
                _lastRecenterTime = Time.time;
                if (resetRootOnRecenter)
                {
                    _targetRootPos = Vector3.zero;
                    drone.position = Vector3.zero;
                }
            }
            else
            {
                float distMeters = (float)HaversineMeters(_centerLatLon, _latestLatLon);
                float timeSince = Time.time - _lastRecenterTime;
                if (distMeters >= recenterDistanceMeters && timeSince >= recenterCooldownSeconds)
                {
                    UpdateMapCenter(_latestLatLon);
                    _centerLatLon = _latestLatLon;
                    _lastRecenterTime = Time.time;
                    if (resetRootOnRecenter)
                    {
                        _targetRootPos = Vector3.zero;
                        drone.position = Vector3.zero;
                    }
                }
            }
        }

        Vector3 centerWorld = map.GeoToWorldPosition(_centerLatLon, false);
        Vector3 currentWorld = map.GeoToWorldPosition(_latestLatLon, false);
        Vector3 flatPos = currentWorld - centerWorld;
        float groundY = 0f;
        if (useTerrainHeight)
        {
            Vector3 groundPos = map.GeoToWorldPosition(_latestLatLon, true);
            groundY = groundPos.y - centerWorld.y;

            if (heightMesh != null && heightMesh.TryGetHeightWorld(groundPos, out float meshHeight))
            {
                groundY = meshHeight - centerWorld.y;
            }
        }

        float altitudeUnity = useAltitude ? (float)(_latestAlt + altitudeOffsetMeters) * map.WorldRelativeScale : 0f;
        float desiredVisualY = altitudeIsAgl ? groundY + altitudeUnity : altitudeUnity;

        // Apply tunable visual scales so developers can adjust perception without changing world coordinates.
        Vector3 desiredLocal = new Vector3(flatPos.x * visualScale, desiredVisualY * visualAltitudeScale, flatPos.z * visualScale);

        Quaternion desiredRot = Quaternion.Euler(-_latestPitch, _latestYaw, _latestRoll);

        if (!_hasTarget)
        {
            _targetVisualLocal = desiredLocal;
            _targetRot = desiredRot;
            _hasTarget = true;
        }
        else
        {
            float scale = Mathf.Max(map.WorldRelativeScale, 0.0001f);
            Vector3 delta = desiredLocal - _targetVisualLocal;
            float moveMeters = new Vector2(delta.x, delta.z).magnitude / scale;
            float altMeters = Mathf.Abs(delta.y) / scale;

            if (moveMeters >= minMoveMeters || altMeters >= minAltitudeMeters)
            {
                _targetVisualLocal = desiredLocal;
            }
            _targetRot = desiredRot;
        }

        Transform visual = droneVisual != null ? droneVisual : drone;
        if (droneVisual != null)
        {
            Vector3 newLocal = enableSmoothing
                ? Vector3.SmoothDamp(visual.localPosition, _targetVisualLocal, ref _visualVel, positionSmoothTime)
                : _targetVisualLocal;
            if (lockRootY)
            {
                newLocal.y = _targetVisualLocal.y;
            }
            visual.localPosition = newLocal;
        }
        else if (useAltitude && !_warnedNoVisual)
        {
            _warnedNoVisual = true;
            Debug.LogWarning("MapboxDroneController: DroneVisual not set. Altitude will not be applied to the model.");
        }

        if (applyRotation)
        {
            Quaternion newRot = enableSmoothing
                ? Quaternion.Slerp(visual.localRotation, _targetRot, 1f - Mathf.Exp(-rotationSmoothSpeed * Time.deltaTime))
                : _targetRot;
            visual.localRotation = newRot;
        }

        if (logUpdates && Time.time - _lastLogTime >= logInterval)
        {
            _lastLogTime = Time.time;
            Debug.Log($"GPS lat={_latestLatLon.x:F7} lon={_latestLatLon.y:F7} alt={_latestAlt:F1} | local=({_targetVisualLocal.x:F1},{_targetVisualLocal.z:F1}) y={_targetVisualLocal.y:F1}");
        }
    }

    void RecenterTo(Vector2d latLon)
    {
        map.UpdateMap(latLon, map.Zoom);
        _centerLatLon = latLon;
        _hasCenter = true;
        _lastRecenterTime = Time.time;
        _hasTarget = false;

        if (resetRootOnRecenter)
        {
            drone.position = Vector3.zero;
        }

        if (logRecenters)
        {
            Debug.Log($"MapboxDroneController: recentered at lat={latLon.x:F7}, lon={latLon.y:F7}");
        }
    }

    float GetRecenterThresholdMeters()
    {
        if (useTileBasedThreshold)
        {
            float scale = Mathf.Max(map.WorldRelativeScale, 0.0001f);
            float metersPerTile = map.UnityTileSize / scale;
            return Mathf.Max(1f, metersPerTile * tilesBeforeRecenter);
        }
        return Mathf.Max(1f, recenterDistanceMeters);
    }

    static double HaversineMeters(Vector2d a, Vector2d b)
    {
        const double radius = 6371000.0;
        double dLat = Mathf.Deg2Rad * (b.x - a.x);
        double dLon = Mathf.Deg2Rad * (b.y - a.y);
        double lat1 = Mathf.Deg2Rad * a.x;
        double lat2 = Mathf.Deg2Rad * b.x;

        double sinLat = Math.Sin(dLat * 0.5);
        double sinLon = Math.Sin(dLon * 0.5);
        double h = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        return 2.0 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    void OnDrawGizmosSelected()
    {
        if (!drawBufferGizmo || map == null)
        {
            return;
        }

        float thresholdMeters = GetRecenterThresholdMeters();
        float radius = thresholdMeters * Mathf.Max(map.WorldRelativeScale, 0.0001f);
        Gizmos.color = bufferGizmoColor;
        Vector3 origin = drone != null ? drone.position : transform.position;
        Gizmos.DrawWireSphere(origin, radius);
    }

    void UpdateMapCenter(Vector2d latLon)
    {
        map.UpdateMap(latLon, map.Zoom);
    }

    void TryConfigureMeshTerrainFactory()
    {
        // Removed: runtime terrain factory injection is not reliable across Mapbox SDK versions.
        // Configure MeshTerrainFactory in the AbstractMap inspector instead.
    }
}
