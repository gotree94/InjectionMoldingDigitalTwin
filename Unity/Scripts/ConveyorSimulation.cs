using UnityEngine;

namespace InjectionMoldingDT.Twin
{
    /// <summary>
    /// 컨베이어 벨트 시각화.
    /// - 벨트 표면 텍스처가 구동 상태에 맞춰 스크롤된다
    /// - DigitalTwinController가 PLC의 Y0(ConvMotor) 상태로 SetBeltActive() 호출
    ///
    /// 씬 구성 예:
    ///   [Conveyor]  Plane/Cube 스트립(Material 수신용 Renderer) + 이 스크립트
    ///   - 벨트 텍스처는 Inspector에서 지정 (NoScale 텍스처 권장)
    /// </summary>
    public class ConveyorSimulation : MonoBehaviour
    {
        [Header("벨트")]
        [Tooltip("스크롤할 벨트 재질 배열 (여러 개면 모두 스크롤)")]
        public Material[] beltMaterials = new Material[0];
        [Tooltip("스크롤 속도 (m/s 당 재질 UV 이동 비율)")]
        public float scrollPerMeter = 0.5f;
        [Tooltip("롤러 회전 (컨베이어 가로축 기준)")]
        public Transform[] rollers;

        private bool _beltActive;
        private float _accumulatedDistance;

        private Renderer _renderer;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
        }

        /// <summary>벨트 구동 상태 설정 (PLC Y0 반영).</summary>
        public void SetBeltActive(bool active)
        {
            _beltActive = active;
        }

        private void Update()
        {
            if (_beltActive)
            {
                // DeltaTime 기반 벨트 이동 누적 (품질당 속도는 트윈 컨트롤러와 별개로 가벼운 연출)
                _accumulatedDistance += Time.deltaTime * 0.25f;
                float offset = _accumulatedDistance * scrollPerMeter;

                ApplyScroll(offset);

                if (rollers != null)
                {
                    foreach (Transform roller in rollers)
                    {
                        if (roller != null)
                        {
                            roller.Rotate(Vector3.forward, -20f * Time.deltaTime, Space.Self);
                        }
                    }
                }
            }
        }

        private void ApplyScroll(float offset)
        {
            if (_renderer != null && _renderer.material != null)
            {
                Vector2 o = _renderer.material.mainTextureOffset;
                o.x = offset;
                _renderer.material.mainTextureOffset = o;
            }

            if (beltMaterials != null)
            {
                foreach (var mat in beltMaterials)
                {
                    if (mat != null)
                    {
                        Vector2 o = mat.mainTextureOffset;
                        o.x = offset;
                        mat.mainTextureOffset = o;
                    }
                }
            }
        }
    }
}