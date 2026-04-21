using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using System.Threading;
public class TCPClientUnity : MonoBehaviour
{
    TcpClient client;
    NetworkStream stream;
    Thread clientThread;
    public string messageOut = "";
    public string messageIn = "";
    public GameObject drone;
    void Start()
    {
        clientThread = new Thread(new ThreadStart(ConnectToServer));
        clientThread.IsBackground = true;
        clientThread.Start();
    }
    void ConnectToServer()
    {
        try
        {
            client = new TcpClient("127.0.0.1", 1234);
            stream = client.GetStream();
            byte[] ping = Encoding.ASCII.GetBytes("MSG_OUT");
            stream.Write(ping, 0, ping.Length);
            Debug.Log("Connected to Python server");
            byte[] buffer = new byte[512];
            while (true)
            {
                while (true)
                {
                    stream.Write(ping, 0, ping.Length);

                    int bytes = stream.Read(buffer, 0, buffer.Length);

                    if (bytes > 0)
                    {
                        messageIn = Encoding.ASCII.GetString(buffer, 0, bytes);
                    }

                    Thread.Sleep(4);
                }
            }
        }
        catch (Exception e) { Debug.Log("Socket error: " + e.Message); }
    }
    void Update()
    {
        if (!string.IsNullOrEmpty(messageIn))
        {
            string[] values = messageIn.Split(' '); float x = float.Parse(values[0]) / 100.0f; float y = float.Parse(values[1]) / 100.0f; float z = float.Parse(values[2]) / 100.0f;
            float roll = float.Parse(values[3]);
            float pitch = float.Parse(values[4]); float yaw = float.Parse(values[5]); drone.transform.position = new Vector3(x, z, y); // switch y and z :( 
            drone.transform.rotation = Quaternion.Euler(pitch, yaw, roll);
        }
    }
    void OnApplicationQuit()
    {
        stream?.Close();
        client?.Close();
    }
}