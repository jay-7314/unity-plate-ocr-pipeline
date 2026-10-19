using UnityEngine;
using UnityEngine.UI;

public class WebCamDisplay : MonoBehaviour
{
    [SerializeField] RawImage display;
    [SerializeField] int requestWidth = 1920;    // 요청 해상도 (웹캠이 지원하지 않으면 가장 가까운 값으로 열림)
    [SerializeField] int requestHeight = 1080;
    [SerializeField] int requestFps = 30;

    WebCamTexture cam;
    bool sized;

    public WebCamTexture Cam => cam;

    void Start()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("웹캠을 찾을 수 없습니다.");
            return;
        }
        cam = new WebCamTexture(WebCamTexture.devices[0].name, requestWidth, requestHeight, requestFps);
        display.texture = cam;
        cam.Play();
    }

    void Update()
    {
        // 웹캠이 실제로 열린 뒤 한 번만: 실제 해상도 확인 + 화면 비율 맞추기
        if (sized || cam == null || cam.width <= 16) return;
        sized = true;
        Debug.Log($"웹캠 실제 해상도: {cam.width}x{cam.height} (요청 {requestWidth}x{requestHeight})");

        var rt = display.rectTransform;
        if (rt.anchorMin == rt.anchorMax)   // 고정 크기 칸일 때만 가로 길이를 영상 비율에 맞춤
        {
            float aspect = (float)cam.width / cam.height;
            rt.sizeDelta = new Vector2(rt.sizeDelta.y * aspect, rt.sizeDelta.y);
        }
    }

    void OnDestroy()
    {
        if (cam != null) cam.Stop();
    }
}