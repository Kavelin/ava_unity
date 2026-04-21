using UnityEngine;
using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using TMPro;

#if UNITY_WEBGL
using NativeWebSocket;
#endif
public class WSSClient : MonoBehaviour
{
    public GameObject drone;
    private string serverIp = "127.0.0.1";
    private int port = 8000;
    public string messageIn = "";
    public TextMeshProUGUI messageInText;

#if UNITY_WEBGL
    WebSocket websocket;

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
            Debug.Log("Connected to drone data stream");
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

    void ParseAndMove(string data) {
        try {
            if (drone == null)
            {
                Debug.LogError("ERROR: Drone GameObject is not assigned!");
                return;
            }

            string[] v = data.Split(' ');
            if (v.Length < 6) 
            {
                Debug.LogWarning($"Incomplete data received ({v.Length} fields): {data}");
                return;
            }

            float x = float.Parse(v[0]) / 100f;
            float y = float.Parse(v[1]) / 100f;
            float z = float.Parse(v[2]) / 100f;
            float roll = float.Parse(v[3]);
            float pitch = float.Parse(v[4]);
            float yaw = float.Parse(v[5]);

            // Debug first few updates
            if (Time.frameCount % 60 == 0) // Log every 60 frames at 60 FPS = 1 second
            {
                Debug.Log($"[Drone] Pos: ({x:F2}, {y:F2}, {z:F2}) Rot: ({roll:F2}°, {pitch:F2}°, {yaw:F2}°)");
            }

            drone.transform.position = new Vector3(x, z, y); // swap y and z for unity coordinate system
            // Invert pitch to match WebGL/Unity coordinate convention when device reports nose-down as positive
            drone.transform.rotation = Quaternion.Euler(-pitch, yaw, roll);
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