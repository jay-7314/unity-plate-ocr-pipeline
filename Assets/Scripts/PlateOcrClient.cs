using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

// 감지된 번호판을 잘라서 TCP로 Python OCR 서버에 보내고, 읽은 번호를 화면에 표시한다.
public class PlateOcrClient : MonoBehaviour
{
    [SerializeField] PersonDetector detector;
    [SerializeField] WebCamDisplay webCamDisplay;
    [SerializeField] string host = "127.0.0.1";
    [SerializeField] int port = 5005;
    [SerializeField] float sendInterval = 0.5f;               // 전송 간격(초)
    [SerializeField, Range(0f, 0.5f)] float padding = 0.1f;   // 박스 주변 여유
    [SerializeField] int voteCount = 10;                      // 최근 몇 번의 결과로 다수결할지
    [SerializeField] float clearAfterLost = 2f;               // 번호판이 사라진 뒤 글자를 지우기까지(초)

    const int InputSize = 640;

    // 네트워크 스레드와 공유하는 값
    readonly object lockObj = new object();
    byte[] pending;           // 보낼 이미지 (1칸짜리 대기열)
    string newResult;         // 새로 도착한 결과
    bool hasNewResult;
    volatile string status = "서버 연결 중";
    volatile bool running;
    Thread netThread;

    // 메인 스레드 전용
    readonly List<string> votes = new List<string>();
    string shownPlate = "";
    float lastSendTime;
    float lastSeenTime;

    void Start()
    {
        running = true;
        netThread = new Thread(NetLoop) { IsBackground = true };
        netThread.Start();
    }

    void Update()
    {
        // 새 OCR 결과 반영
        string result = null;
        lock (lockObj)
        {
            if (hasNewResult) { result = newResult; hasNewResult = false; }
        }
        if (!string.IsNullOrEmpty(result))
        {
            votes.Add(result);
            if (votes.Count > voteCount) votes.RemoveAt(0);
            shownPlate = BestPlate(votes);
        }

        // 번호판이 한동안 안 보이면 글자 지우기
        if (detector.Detected) lastSeenTime = Time.time;
        else if (shownPlate != "" && Time.time - lastSeenTime > clearAfterLost)
        {
            votes.Clear();
            shownPlate = "";
        }
    }

    void LateUpdate()
    {
        var cam = webCamDisplay.Cam;
        if (cam == null || !detector.Detected) return;
        if (Time.time - lastSendTime < sendInterval) return;

        lock (lockObj)
        {
            if (pending != null) return;   // 아직 이전 이미지를 처리 중
        }

        byte[] jpg = CropPlate(cam, detector.LastBox);
        if (jpg == null) return;

        lastSendTime = Time.time;
        lock (lockObj) { pending = jpg; }
    }

    // 640x640 좌표의 박스를 웹캠 원본 해상도로 환산해서 잘라낸다
    byte[] CropPlate(WebCamTexture cam, Vector4 b)
    {
        float cx = b.x / InputSize * cam.width;
        float cy = b.y / InputSize * cam.height;
        float bw = b.z / InputSize * cam.width * (1f + padding * 2f);
        float bh = b.w / InputSize * cam.height * (1f + padding * 2f);

        int x = Mathf.Clamp(Mathf.RoundToInt(cx - bw / 2f), 0, cam.width - 1);
        int yTop = Mathf.Clamp(Mathf.RoundToInt(cy - bh / 2f), 0, cam.height - 1);
        int w = Mathf.Min(Mathf.RoundToInt(bw), cam.width - x);
        int h = Mathf.Min(Mathf.RoundToInt(bh), cam.height - yTop);
        if (w < 20 || h < 10) return null;   // 너무 작으면 읽기 어렵다

        int yBottom = cam.height - (yTop + h);   // GetPixels는 아래쪽이 원점
        Color[] pixels = cam.GetPixels(x, yBottom, w, h);
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.SetPixels(pixels);
        tex.Apply();
        byte[] jpg = tex.EncodeToJPG(90);
        Destroy(tex);
        return jpg;
    }

    // 앞뒤 숫자(가운데 글자 제외)로 먼저 다수결을 낸 뒤,
    // 같은 숫자를 가진 결과들 중 ?가 아닌 한글을 다수결로 고른다.
    // (한글은 가끔만 읽혀도 ?에 밀리지 않게 하려는 것)
    static string BestPlate(List<string> list)
    {
        var keyCount = new Dictionary<string, int>();
        string bestKey = "";
        int bestN = 0;
        foreach (var s in list)
        {
            if (s.Length < 7) continue;
            string key = s.Remove(s.Length - 5, 1);
            keyCount.TryGetValue(key, out int n);
            n++;
            keyCount[key] = n;
            if (n > bestN) { bestN = n; bestKey = key; }
        }
        if (bestKey == "") return "";

        var midCount = new Dictionary<char, int>();
        char bestMid = '?';
        int bestM = 0;
        foreach (var s in list)
        {
            if (s.Length < 7) continue;
            if (s.Remove(s.Length - 5, 1) != bestKey) continue;
            char m = s[s.Length - 5];
            if (m == '?') continue;
            midCount.TryGetValue(m, out int c);
            c++;
            midCount[m] = c;
            if (c > bestM) { bestM = c; bestMid = m; }
        }
        return bestKey.Insert(bestKey.Length - 4, bestMid.ToString());
    }

    // ---------- 네트워크 스레드 ----------
    void NetLoop()
    {
        while (running)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    status = "서버 연결 중";
                    client.Connect(host, port);
                    status = "서버 연결됨";
                    var stream = client.GetStream();

                    while (running)
                    {
                        byte[] data = null;
                        lock (lockObj) { data = pending; }
                        if (data == null) { Thread.Sleep(10); continue; }

                        WriteInt(stream, data.Length);
                        stream.Write(data, 0, data.Length);

                        int len = ReadInt(stream);
                        byte[] buf = ReadExact(stream, len);
                        string text = System.Text.Encoding.UTF8.GetString(buf);

                        lock (lockObj)
                        {
                            newResult = text;
                            hasNewResult = true;
                            pending = null;
                        }
                    }
                }
            }
            catch (Exception)
            {
                status = "서버 연결 안 됨 (python ocr_server.py 실행 확인)";
                lock (lockObj) { pending = null; }
                Thread.Sleep(1000);
            }
        }
    }

    static void WriteInt(NetworkStream s, int v)
    {
        s.Write(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }, 0, 4);
    }

    static int ReadInt(NetworkStream s)
    {
        byte[] b = ReadExact(s, 4);
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    static byte[] ReadExact(NetworkStream s, int n)
    {
        byte[] buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, got, n - got);
            if (r <= 0) throw new System.IO.IOException("연결 종료");
            got += r;
        }
        return buf;
    }

    // ---------- 화면 표시 ----------
    void OnGUI()
    {
        GUI.skin.label.fontSize = 28;
        GUI.Label(new Rect(10, 50, 1200, 40), "번호판: " + (shownPlate == "" ? "-" : shownPlate));
        GUI.Label(new Rect(10, 90, 1200, 40), "OCR 서버: " + status);
    }

    void OnDestroy()
    {
        running = false;
    }
}