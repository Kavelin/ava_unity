using UnityEngine;
using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using TMPro;

#if UNITY_WEBGL
using NativeWebSocket;
#endif
public partial class WSSClient : MonoBehaviour
{
    public GameObject drone;
    private string serverIp = "127.0.0.1";
    private int port = 8000;
    public string messageIn = "";
    public TextMeshProUGUI messageInText;

#if UNITY_WEBGL
    WebSocket websocket;
    MapboxDroneController _mapCtrl;

    async void Start()
    {
        // Connect to FastAPI WebSocket server at /ws endpoint
        websocket = new WebSocket($"ws://{serverIp}:{port}/ws");

        websocket.OnMessage += (bytes) => messageIn = Encoding.UTF8.GetString(bytes);
        websocket.OnError += (error) => Debug.LogError($"WebSocket error: {error}");
        websocket.OnClose += (code) => Debug.Log($"WebSocket closed with code: {code}");

        try
        {
            await websocket.Connect();
            Debug.Log("WSSClient: Connected to drone data stream");
            // Cache Mapbox controller once at startup
            _mapCtrl = FindObjectOfType<MapboxDroneController>();
            if (_mapCtrl == null) Debug.LogWarning("WSSClient: MapboxDroneController not found in scene at Start.");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to connect: {e}");
        }
    }
    void Update()
    {
        if (!string.IsNullOrEmpty(messageIn)) 
        {
            if (messageInText) messageInText.text = "msgin: " + messageIn;
            ParseAndMove(messageIn);
            messageIn = ""; // Clear after parsing
        }
    }

    void ParseAndMove(string data)
    {
        try
        {
            if (drone == null)
            {
                Debug.LogError("ERROR: Drone GameObject is not assigned!");
                return;
            }

            // Try JSON first
            try
            {
                var obj = JsonUtility.FromJson<SimpleGps>(data);
                if (obj != null && !double.IsNaN(obj.lat) && !double.IsNaN(obj.lon))
                {
                    CallMapController(obj.lat, obj.lon, obj.alt, obj.roll, obj.pitch, obj.yaw);
                    return;
                }
            }
            catch (Exception) { }

            // Try space-separated: lat lon alt [roll pitch yaw]
            string[] parts = data.Split(' ');
            if (parts.Length >= 3 && double.TryParse(parts[0], out double a0) && double.TryParse(parts[1], out double a1) && double.TryParse(parts[2], out double a2))
            {
                float roll = parts.Length > 3 && float.TryParse(parts[3], out float r) ? r : 0f;
                float pitch = parts.Length > 4 && float.TryParse(parts[4], out float p) ? p : 0f;
                float yaw = parts.Length > 5 && float.TryParse(parts[5], out float yv) ? yv : 0f;
                CallMapController(a0, a1, a2, roll, pitch, yaw);
                return;
            }

            Debug.LogWarning($"Unrecognized drone data format: {data}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error parsing drone data: {e.Message}\nData: {data}");
        }
    }

    private async void OnApplicationQuit() {

        if (websocket != null) await websocket.Close();

    }
#endif
}

[System.Serializable]
public class SimpleGps
{
    public double lat = double.NaN;
    public double lon = double.NaN;
    public double alt = 0.0;
    public float roll = 0f;
    public float pitch = 0f;
    public float yaw = 0f;
}

partial class WSSClient
{
    void CallMapController(double lat, double lon, double alt, float roll, float pitch, float yaw)
    {
        try
        {
            if (_mapCtrl == null)
            {
                _mapCtrl = FindObjectOfType<MapboxDroneController>();
            }

            if (_mapCtrl != null)
            {
                _mapCtrl.ApplyGpsFix(lat, lon, alt, roll, pitch, yaw);
            }
            else
            {
                Debug.LogWarning("WSSClient: MapboxDroneController not found when trying to apply GPS fix.");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Error calling MapboxDroneController: " + e.Message);
        }
    }
}