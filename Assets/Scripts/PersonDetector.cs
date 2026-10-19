using UnityEngine;
using Unity.InferenceEngine;

public class PersonDetector : MonoBehaviour
{
    [SerializeField] ModelAsset modelAsset;
    [SerializeField] WebCamDisplay webCamDisplay;
    [SerializeField] RectTransform display;   // RawImage
    [SerializeField] RectTransform box;       // 감지 박스
    [SerializeField] RectTransform pointer;   // 포인터(동그라미)
    [SerializeField, Range(0.1f, 0.9f)] float threshold = 0.5f;
    [SerializeField] int lostFrameLimit = 20;                    // 놓쳐도 유지할 프레임 수
    [SerializeField, Range(0.05f, 1f)] float smoothing = 0.3f;   // 작을수록 부드럽게(느리게) 따라감

    const int InputSize = 640;
    const int Anchors = 8400;

    // 다른 스크립트(번호판 OCR)가 읽어가는 값
    public bool Detected { get; private set; }      // 이번 추론에서 실제로 잡혔는지
    public Vector4 LastBox { get; private set; }    // 마지막 박스 (cx, cy, w, h), 640 기준

    Worker worker;
    Tensor<float> input;

    bool tracking;
    int lostFrames;
    Vector2 pointerPos;
    float fps;
    float inferenceMs;
    float detectFps;
    float lastDetectTime;

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);
        worker = new Worker(model, BackendType.GPUCompute);
        input = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
        Show(false);
    }

    void Update()
    {
        fps = Mathf.Lerp(fps, 1f / Time.unscaledDeltaTime, 0.1f);
        var cam = webCamDisplay.Cam;
        if (cam == null || !cam.didUpdateThisFrame) return;

        float t0 = Time.realtimeSinceStartup;
        TextureConverter.ToTensor(cam, input);
        worker.Schedule(input);

        var output = worker.PeekOutput() as Tensor<float>;
        float[] data = output.DownloadToArray();
        inferenceMs = (Time.realtimeSinceStartup - t0) * 1000f;
        float now = Time.realtimeSinceStartup;
        detectFps = Mathf.Lerp(detectFps, 1f / (now - lastDetectTime), 0.1f);
        lastDetectTime = now;

        // 점수가 가장 높은 후보 1개 선택 (모델 출력 한 줄: cx, cy, w, h, 점수)
        int best = -1;
        float bestScore = threshold;
        for (int i = 0; i < Anchors; i++)
        {
            float score = data[4 * Anchors + i];
            if (score > bestScore) { bestScore = score; best = i; }
        }

        Detected = best >= 0;

        if (best < 0)
        {
            // 놓친 경우: 마지막 위치를 유지하다가 제한을 넘으면 숨김
            if (!tracking) return;
            lostFrames++;
            if (lostFrames >= lostFrameLimit)
            {
                tracking = false;
                Show(false);
            }
            return;
        }

        float cx = data[0 * Anchors + best];
        float cy = data[1 * Anchors + best];
        float bw = data[2 * Anchors + best];
        float bh = data[3 * Anchors + best];
        LastBox = new Vector4(cx, cy, bw, bh);

        float w = display.rect.width;
        float h = display.rect.height;

        Vector2 center = new Vector2((cx / InputSize - 0.5f) * w, (0.5f - cy / InputSize) * h);
        box.anchoredPosition = center;
        box.sizeDelta = new Vector2(bw / InputSize * w, bh / InputSize * h);

        // 처음 잡힐 때는 바로 이동, 이후에는 부드럽게 따라감
        pointerPos = tracking ? Vector2.Lerp(pointerPos, center, smoothing) : center;
        pointer.anchoredPosition = pointerPos;

        tracking = true;
        lostFrames = 0;
        Show(true);
    }

    void Show(bool visible)
    {
        box.gameObject.SetActive(visible);
        pointer.gameObject.SetActive(visible);
    }

    void OnGUI()
    {
        GUI.skin.label.fontSize = 28;
        GUI.Label(new Rect(10, 10, 1000, 40),
            $"렌더 {fps:F0} FPS  |  인식 {detectFps:F0}회/초  |  추론 {inferenceMs:F1} ms  |  감지 {(tracking ? "O" : "X")}");
    }

    void OnDestroy()
    {
        worker?.Dispose();
        input?.Dispose();
    }
}