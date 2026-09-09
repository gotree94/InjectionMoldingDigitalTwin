using UnityEngine;

namespace InjectionMoldingDT.Twin
{
    /// <summary>
    /// 비전 검사 스테이션 시각화.
    /// - 검사 활성 시 하이라이트 라이트가 켜지고, 판정에 따라 색상(OK=green/NG=red) 표시
    /// - DigitalTwinController로부터 검사 상태를 받아 연출만 담당
    ///
    /// 씬 구성 예:
    ///   [VisionStation]  카메라/가드 프레임 + SpotLight 하이라이트 + 이 스크립트
    ///   - highlightLight/frameRenderer Inspector 지정
    /// </summary>
    public class VisionInspection : MonoBehaviour
    {
        [Header("연출")]
        public Light highlightLight;
        public Renderer frameRenderer;
        public Material okMaterial;
        public Material ngMaterial;
        public Material idleMaterial;

        [Header("표시 텍스트")]
        public TMPro.TextMeshPro label; // (옵션) 판정 라벨

        private bool _active;
        private bool _ok;
        private bool _ng;
        private int _code;

        public void SetInspection(bool active, bool ok, bool ng, int code)
        {
            _active = active;
            _ok = ok;
            _ng = ng;
            _code = code;
        }

        private void Update()
        {
            Material target = null;
            if (_active)
            {
                if (_ng)
                    target = ngMaterial != null ? ngMaterial : null;
                else if (_ok)
                    target = okMaterial != null ? okMaterial : null;
                else
                    target = idleMaterial != null ? idleMaterial : null;
            }
            else
            {
                target = idleMaterial != null ? idleMaterial : null;
            }

            if (frameRenderer != null && target != null)
            {
                frameRenderer.material = target;
            }

            if (highlightLight != null)
            {
                highlightLight.intensity = _active ? 2.0f : 0.3f;
                highlightLight.color = _ng ? Color.red : (_ok ? Color.green : Color.white);
            }

            if (label != null)
            {
                label.text = _ng ? string.Format("NG ({0})", _code) : (_ok ? "OK" : "---");
            }
        }
    }
}