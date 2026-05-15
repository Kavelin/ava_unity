using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using System.Threading;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
public class TCPClientUnity : MonoBehaviour
{
    TcpClient client;
    NetworkStream stream;
    Thread clientThread;
    public string messageOut = "";
    public string messageIn = "";
    public GameObject drone;
    public string serverIp = "127.0.0.1";
    public int serverPort = 5760;
    public bool useMavlinkParser = true;
    public bool sendPing = false;
    public bool sendMavlinkHeartbeat = true;
    public bool requestDataStreams = true;
    public bool requestMessageIntervals = true;
    [Min(0.1f)] public float gpsIntervalHz = 5f;
    [Min(0.1f)] public float attitudeIntervalHz = 5f;
    [Min(0f)] public float pingIntervalSeconds = 0.1f;
    public bool useStatusPolling = false;
    public bool logDataStatus = true;
    [Min(0f)] public float statusLogIntervalSeconds = 2f;
    [Min(0f)] public float noDataWarningSeconds = 3f;
    public bool logMavlinkMessages = false;
    [Min(0f)] public float gpsLogIntervalSeconds = 1f;
    MapboxDroneController _mapCtrl;
    readonly List<byte> _rxBuffer = new List<byte>(8192);
    readonly object _gpsLock = new object();
    bool _gpsDirty;
    double _gpsLat;
    double _gpsLon;
    double _gpsAlt;
    float _rollDeg;
    float _pitchDeg;
    float _yawDeg;
    static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    long _bytesTotal;
    long _lastDataTicks;
    double _lastPingSeconds;
    float _lastStatusLogTime;
    float _lastGpsLogTime;
    byte _txSeq;
    void Start()
    {
        clientThread = new Thread(new ThreadStart(ConnectToServer));
        clientThread.IsBackground = true;
        clientThread.Start();
        _mapCtrl = FindObjectOfType<MapboxDroneController>();
        if (_mapCtrl == null) Debug.LogWarning("TCPClientUnity: MapboxDroneController not found in scene at Start.");
        if (useStatusPolling)
        {
            // Optional: poll HTTP status endpoint when Python server is running
            StartCoroutine(PollServerStatus());
        }
    }
    void ConnectToServer()
    {
        try
        {
            client = new TcpClient(serverIp, serverPort);
            stream = client.GetStream();
            byte[] ping = Encoding.ASCII.GetBytes("MSG_OUT");
            if (sendPing && !useMavlinkParser)
            {
                stream.Write(ping, 0, ping.Length);
                _lastPingSeconds = _stopwatch.Elapsed.TotalSeconds;
            }

            if (useMavlinkParser)
            {
                if (sendMavlinkHeartbeat)
                {
                    SendMavlinkHeartbeat();
                }
                if (requestDataStreams)
                {
                    SendMavlinkRequestDataStream(6, 5); // MAV_DATA_STREAM_POSITION at 5 Hz
                    SendMavlinkRequestDataStream(10, 5); // MAV_DATA_STREAM_EXTRA1 (attitude)
                }
                if (requestMessageIntervals)
                {
                    SendMavlinkSetMessageInterval(33, gpsIntervalHz); // GLOBAL_POSITION_INT
                    SendMavlinkSetMessageInterval(24, gpsIntervalHz); // GPS_RAW_INT
                    SendMavlinkSetMessageInterval(30, attitudeIntervalHz); // ATTITUDE
                }
            }
            Debug.Log($"TCPClientUnity: Connected to {serverIp}:{serverPort}");
            byte[] buffer = new byte[512];
            while (true)
            {
                if (sendPing && !useMavlinkParser)
                {
                    double nowSec = _stopwatch.Elapsed.TotalSeconds;
                    if (nowSec - _lastPingSeconds >= pingIntervalSeconds)
                    {
                        stream.Write(ping, 0, ping.Length);
                        _lastPingSeconds = nowSec;
                    }
                }
                else if (useMavlinkParser && sendMavlinkHeartbeat)
                {
                    double nowSec = _stopwatch.Elapsed.TotalSeconds;
                    if (nowSec - _lastPingSeconds >= 1.0)
                    {
                        SendMavlinkHeartbeat();
                        _lastPingSeconds = nowSec;
                    }
                }

                int bytes = stream.Read(buffer, 0, buffer.Length);

                if (bytes > 0)
                {
                    Interlocked.Add(ref _bytesTotal, bytes);
                    Interlocked.Exchange(ref _lastDataTicks, Stopwatch.GetTimestamp());
                    if (useMavlinkParser)
                    {
                        ProcessMavlinkBytes(buffer, bytes);
                    }
                    else
                    {
                        messageIn = Encoding.ASCII.GetString(buffer, 0, bytes);
                    }
                }

                Thread.Sleep(4);
            }
        }
        catch (Exception e) { Debug.Log("Socket error: " + e.Message); }
    }
    void Update()
    {
        if (logDataStatus)
        {
            if (Time.time - _lastStatusLogTime >= statusLogIntervalSeconds)
            {
                _lastStatusLogTime = Time.time;
                long bytesTotal = Interlocked.Read(ref _bytesTotal);
                long lastTicks = Interlocked.Read(ref _lastDataTicks);
                double secondsSinceData = lastTicks == 0
                    ? double.PositiveInfinity
                    : (Stopwatch.GetTimestamp() - lastTicks) / (double)Stopwatch.Frequency;

                if (secondsSinceData >= noDataWarningSeconds)
                {
                    Debug.LogWarning($"TCPClientUnity: no data received for {secondsSinceData:F1}s (total bytes {bytesTotal}). Check Mission Planner TCP output mode/port.");
                }
                else
                {
                    Debug.Log($"TCPClientUnity: receiving data (total bytes {bytesTotal}).");
                }
            }
        }

        if (useMavlinkParser)
        {
            bool hasGps = false;
            double lat = 0.0;
            double lon = 0.0;
            double alt = 0.0;
            float roll = 0f;
            float pitch = 0f;
            float yaw = 0f;
            lock (_gpsLock)
            {
                if (_gpsDirty)
                {
                    hasGps = true;
                    lat = _gpsLat;
                    lon = _gpsLon;
                    alt = _gpsAlt;
                    roll = _rollDeg;
                    pitch = _pitchDeg;
                    yaw = _yawDeg;
                    _gpsDirty = false;
                }
            }

            if (hasGps)
            {
                if (_mapCtrl == null) _mapCtrl = UnityEngine.Object.FindObjectOfType<MapboxDroneController>();
                if (_mapCtrl != null) _mapCtrl.ApplyGpsFix(lat, lon, alt, roll, pitch, yaw);

                if (logMavlinkMessages && Time.time - _lastGpsLogTime >= gpsLogIntervalSeconds)
                {
                    _lastGpsLogTime = Time.time;
                    Debug.Log($"TCPClientUnity: GPS lat={lat:F7} lon={lon:F7} alt={alt:F1}");
                }
            }
        }

        if (!string.IsNullOrEmpty(messageIn))
        {
            // Try JSON GPS
            try {
                var obj = UnityEngine.JsonUtility.FromJson<SimpleGps>(messageIn);
                if (obj != null && !double.IsNaN(obj.lat) && !double.IsNaN(obj.lon))
                {
                    if (_mapCtrl == null) _mapCtrl = UnityEngine.Object.FindObjectOfType<MapboxDroneController>();
                    if (_mapCtrl != null) _mapCtrl.ApplyGpsFix(obj.lat, obj.lon, obj.alt, obj.roll, obj.pitch, obj.yaw);
                    messageIn = "";
                    return;
                }
            } catch (Exception) {}

            string[] values = messageIn.Split(' ');
            if (values.Length >= 3 && double.TryParse(values[0], out double a0) && double.TryParse(values[1], out double a1) && double.TryParse(values[2], out double a2))
            {
                float roll = values.Length > 3 && float.TryParse(values[3], out float r) ? r : 0f;
                float pitch = values.Length > 4 && float.TryParse(values[4], out float p) ? p : 0f;
                float yaw = values.Length > 5 && float.TryParse(values[5], out float yv) ? yv : 0f;
                if (_mapCtrl == null) _mapCtrl = UnityEngine.Object.FindObjectOfType<MapboxDroneController>();
                if (_mapCtrl != null) _mapCtrl.ApplyGpsFix(a0, a1, a2, roll, pitch, yaw);
            }
            else
            {
                Debug.LogWarning("TCPClientUnity: Unrecognized message format: " + messageIn);
            }
        }
    }
    void OnApplicationQuit()
    {
        stream?.Close();
        client?.Close();
    }

    System.Collections.IEnumerator PollServerStatus()
    {
        using (var uwr = new UnityEngine.Networking.UnityWebRequest("http://127.0.0.1:8000/status", "GET"))
        {
            uwr.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
        }

        while (true)
        {
            using (var req = UnityEngine.Networking.UnityWebRequest.Get("http://127.0.0.1:8000/status"))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var json = req.downloadHandler.text;
                        var status = JsonUtility.FromJson<StatusResponse>(json);
                        if (status != null && status.latest_vehicle_data != null)
                        {
                            var v = status.latest_vehicle_data;
                            if (_mapCtrl == null) _mapCtrl = UnityEngine.Object.FindObjectOfType<MapboxDroneController>();
                            if (_mapCtrl != null && v.lat != null && v.lon != null)
                            {
                                double lat = v.lat.Value;
                                double lon = v.lon.Value;
                                double alt = v.alt ?? 0.0;
                                _mapCtrl.ApplyGpsFix(lat, lon, alt, (float)(v.roll ?? 0.0), (float)(v.pitch ?? 0.0), (float)(v.yaw ?? 0.0));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("TCPClientUnity: error parsing /status response: " + e.Message);
                    }
                }
            }

            yield return new WaitForSeconds(0.25f);
        }
    }

    void ProcessMavlinkBytes(byte[] data, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _rxBuffer.Add(data[i]);
        }

        int idx = 0;
        while (idx < _rxBuffer.Count)
        {
            byte stx = _rxBuffer[idx];
            if (stx != 0xFE && stx != 0xFD)
            {
                idx++;
                continue;
            }

            bool v2 = stx == 0xFD;
            int headerLen = v2 ? 10 : 6;
            if (_rxBuffer.Count - idx < headerLen + 2)
            {
                break; // not enough for header + checksum
            }

            int payloadLen = _rxBuffer[idx + 1];
            int signatureLen = 0;
            if (v2)
            {
                byte incompatFlags = _rxBuffer[idx + 2];
                if ((incompatFlags & 0x01) != 0)
                {
                    signatureLen = 13;
                }
            }

            int frameLen = headerLen + payloadLen + 2 + signatureLen;
            if (_rxBuffer.Count - idx < frameLen)
            {
                break; // wait for more bytes
            }

            int msgId = 0;
            if (v2)
            {
                msgId = _rxBuffer[idx + 7] | (_rxBuffer[idx + 8] << 8) | (_rxBuffer[idx + 9] << 16);
            }
            else
            {
                msgId = _rxBuffer[idx + 5];
            }

            int payloadStart = idx + headerLen;
            if (logMavlinkMessages)
            {
                Debug.Log($"TCPClientUnity: MAVLink msg id {msgId}, payload {payloadLen} bytes");
            }

            if (msgId == 33) // GLOBAL_POSITION_INT
            {
                if (payloadLen >= 16)
                {
                    int lat = ReadInt32(payloadStart + 4);
                    int lon = ReadInt32(payloadStart + 8);
                    int alt = ReadInt32(payloadStart + 12);
                    SetGps(lat / 1e7, lon / 1e7, alt / 1000.0);
                }
            }
            else if (msgId == 24) // GPS_RAW_INT
            {
                if (payloadLen >= 20)
                {
                    int lat = ReadInt32(payloadStart + 8);
                    int lon = ReadInt32(payloadStart + 12);
                    int alt = ReadInt32(payloadStart + 16);
                    SetGps(lat / 1e7, lon / 1e7, alt / 1000.0);
                }
            }
            else if (msgId == 30) // ATTITUDE
            {
                if (payloadLen >= 16)
                {
                    float roll = ReadFloat(payloadStart + 4);
                    float pitch = ReadFloat(payloadStart + 8);
                    float yaw = ReadFloat(payloadStart + 12);
                    SetAttitude(Mathf.Rad2Deg * roll, Mathf.Rad2Deg * pitch, Mathf.Rad2Deg * yaw);
                }
            }

            idx += frameLen;
        }

        if (idx > 0)
        {
            _rxBuffer.RemoveRange(0, idx);
        }
    }

    int ReadInt32(int offset)
    {
        return _rxBuffer[offset]
            | (_rxBuffer[offset + 1] << 8)
            | (_rxBuffer[offset + 2] << 16)
            | (_rxBuffer[offset + 3] << 24);
    }

    float ReadFloat(int offset)
    {
        byte[] tmp = new byte[4];
        tmp[0] = _rxBuffer[offset];
        tmp[1] = _rxBuffer[offset + 1];
        tmp[2] = _rxBuffer[offset + 2];
        tmp[3] = _rxBuffer[offset + 3];
        return BitConverter.ToSingle(tmp, 0);
    }

    void SetGps(double lat, double lon, double altMeters)
    {
        lock (_gpsLock)
        {
            _gpsLat = lat;
            _gpsLon = lon;
            _gpsAlt = altMeters;
            _gpsDirty = true;
        }
    }

    void SetAttitude(float rollDeg, float pitchDeg, float yawDeg)
    {
        lock (_gpsLock)
        {
            _rollDeg = rollDeg;
            _pitchDeg = pitchDeg;
            _yawDeg = yawDeg;
        }
    }

    void SendMavlinkHeartbeat()
    {
        // MAVLink v1 heartbeat (msgid 0), extra CRC 50
        byte[] payload = new byte[9];
        // custom_mode (uint32)
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 0;
        payload[3] = 0;
        // type (MAV_TYPE_GCS = 6)
        payload[4] = 6;
        // autopilot (MAV_AUTOPILOT_INVALID = 8)
        payload[5] = 8;
        // base_mode
        payload[6] = 0;
        // system_status
        payload[7] = 0;
        // mavlink_version
        payload[8] = 3;

        SendMavlinkV1(0, payload, 50);
    }

    void SendMavlinkRequestDataStream(byte streamId, ushort rateHz)
    {
        // MAVLink v1 request_data_stream (msgid 66), extra CRC 148
        byte[] payload = new byte[6];
        payload[0] = 1; // target_system
        payload[1] = 1; // target_component
        payload[2] = streamId; // req_stream_id
        payload[3] = (byte)(rateHz & 0xFF);
        payload[4] = (byte)((rateHz >> 8) & 0xFF);
        payload[5] = 1; // start

        SendMavlinkV1(66, payload, 148);
    }

    void SendMavlinkSetMessageInterval(int messageId, float hz)
    {
        if (hz <= 0f)
        {
            return;
        }

        float intervalUs = 1000000f / hz;
        SendMavlinkCommandLong(511, messageId, intervalUs, 0f, 0f, 0f, 0f, 0f);
    }

    void SendMavlinkCommandLong(ushort command, float p1, float p2, float p3, float p4, float p5, float p6, float p7)
    {
        // MAVLink v1 COMMAND_LONG (msgid 76), extra CRC 152
        byte[] payload = new byte[33];
        WriteFloat(payload, 0, p1);
        WriteFloat(payload, 4, p2);
        WriteFloat(payload, 8, p3);
        WriteFloat(payload, 12, p4);
        WriteFloat(payload, 16, p5);
        WriteFloat(payload, 20, p6);
        WriteFloat(payload, 24, p7);
        payload[28] = (byte)(command & 0xFF);
        payload[29] = (byte)((command >> 8) & 0xFF);
        payload[30] = 1; // target_system
        payload[31] = 1; // target_component
        payload[32] = 0; // confirmation

        SendMavlinkV1(76, payload, 152);
    }

    void SendMavlinkV1(byte msgId, byte[] payload, byte extraCrc)
    {
        if (stream == null)
        {
            return;
        }

        int payloadLen = payload != null ? payload.Length : 0;
        byte[] frame = new byte[6 + payloadLen + 2];
        frame[0] = 0xFE; // STX
        frame[1] = (byte)payloadLen;
        frame[2] = _txSeq++;
        frame[3] = 255; // sysid (GCS)
        frame[4] = 190; // compid (GCS)
        frame[5] = msgId;

        if (payloadLen > 0)
        {
            Buffer.BlockCopy(payload, 0, frame, 6, payloadLen);
        }

        ushort crc = 0xFFFF;
        crc = CrcAccumulate(frame[1], crc);
        crc = CrcAccumulate(frame[2], crc);
        crc = CrcAccumulate(frame[3], crc);
        crc = CrcAccumulate(frame[4], crc);
        crc = CrcAccumulate(frame[5], crc);
        for (int i = 0; i < payloadLen; i++)
        {
            crc = CrcAccumulate(frame[6 + i], crc);
        }
        crc = CrcAccumulate(extraCrc, crc);

        int crcIndex = 6 + payloadLen;
        frame[crcIndex] = (byte)(crc & 0xFF);
        frame[crcIndex + 1] = (byte)((crc >> 8) & 0xFF);

        try
        {
            stream.Write(frame, 0, frame.Length);
        }
        catch (Exception e)
        {
            Debug.LogWarning("TCPClientUnity: MAVLink send failed: " + e.Message);
        }
    }

    static ushort CrcAccumulate(byte b, ushort crc)
    {
        byte tmp = (byte)(b ^ (byte)(crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)(((crc >> 8) & 0xFF) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    static void WriteFloat(byte[] buffer, int offset, float value)
    {
        byte[] b = BitConverter.GetBytes(value);
        Buffer.BlockCopy(b, 0, buffer, offset, 4);
    }

    [Serializable]
    private class StatusResponse
    {
        public int connected_websockets;
        public bool vehicle_connected;
        public LatestVehicle latest_vehicle_data;
    }

    [Serializable]
    private class LatestVehicle
    {
        public double? lat;
        public double? lon;
        public double? alt;
        public double? n;
        public double? e;
        public double? d;
        public double? roll;
        public double? pitch;
        public double? yaw;
    }
}