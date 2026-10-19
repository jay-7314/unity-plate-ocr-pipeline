using UnityEngine;
using Unity.InferenceEngine;

public class ModelLoadTest : MonoBehaviour
{
    [SerializeField] ModelAsset modelAsset;
    Worker worker;

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);
        worker = new Worker(model, BackendType.GPUCompute);
        Debug.Log($"모델 로드 성공 / 입력: {model.inputs[0].name} {model.inputs[0].shape}");
    }

    void OnDestroy()
    {
        worker?.Dispose();
    }
}