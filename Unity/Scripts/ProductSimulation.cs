using UnityEngine;

namespace InjectionMoldingDT.Twin
{
    /// <summary>
    /// 컨베이어 위 제품(사출물) 시뮬레이션.
    /// - 컨베이어 진행 거리(ConveyorPos)를 추적하여 카운팅/비전/리젝트 이벤트에 사용됨
    /// - 불량 제품은 리젝터 위치에서 옆(불량 박스)으로 분기되어 낙하
    /// - 정상 제품은 컨베이어 끝에서 아래(슈트)로 낙하
    ///
    /// 씬 구성 예:
    ///   [Product 프리팹] 3D 큐브/사출물 모델 (크기 approx 0.1m) + 이 스크립트
    ///   - 불량 박스 위치: transform.position + Vector3(-1.2f, 0, -1.0f) 주변에 배치
    ///   - 정상 슈트: 컨베이어 끝 아래에 배치
    /// </summary>
    public class ProductSimulation : MonoBehaviour
    {
        [Header("이동 연출")]
        public float startY = 0.5f;        // 컨베이어 위 제품 중심 높이
        public float rejectDropDepth = 0.9f; // 불량 박스로 낙하 깊이
        public float goodDropDepth = 0.9f;   // 정상 슈트로 낙하 깊이
        public float divergeSpeed = 1.5f;    // 불량 분기 이동 속도 (m/s)
        public float dropSpeed = 0.8f;       // 낙하 속도 (m/s)

        // 트래킹 상태 (DigitalTwinController가 갱신)
        public bool Counted { get; set; }
        public bool Inspected { get; set; }
        public bool IsDefect { get; set; }
        public bool Sorted { get; set; }
        public bool CanRemove { get; private set; }
        public float ConveyorPos { get; set; }
        public DefectType Defect { get; set; }

        private float _beltSpeed;
        private bool _divergeStarted;
        private bool _dropStarted;

        public void Setup(float startX, float beltSpeed)
        {
            ConveyorPos = startX;
            _beltSpeed = beltSpeed;
            CanRemove = false;
            IsDefect = false;
            Inspected = false;
            Sorted = false;

            if (transform.childCount > 0)
            {
                // 자체 3D 모델 보유 시 (보통 프리팹이 큐브 1개)
                transform.position = new Vector3(startX, startY, 0f);
            }
        }

        /// <summary>컨베이어 방향(x 축)으로 진행.</summary>
        public void Advance(float delta)
        {
            if (Sorted) return;

            ConveyorPos += delta * _beltSpeed;
            transform.position = new Vector3(ConveyorPos, startY, 0f);
        }

        /// <summary>불량 - 리젝터에서 불량 박스로 분기.</summary>
        public void DiverToReject()
        {
            _divergeStarted = true;
            _dropStarted = false;
        }

        /// <summary>정상 - 컨베이어 끝에서 정상 슈트로.</summary>
        public void ArriveGoodEnd()
        {
            _dropStarted = true;
            _divergeStarted = false;
        }

        private void Update()
        {
            if (!Sorted) return;

            // 불량 분기: x 진행 멈추고 z(-) 방향으로 이동하면서 낙하
            if (_divergeStarted && !_dropStarted)
            {
                Vector3 pos = transform.position;
                pos.x = Mathf.Lerp(pos.x, rejectX(), Time.deltaTime * divergeSpeed * 3f);
                pos.z = Mathf.Lerp(pos.z, rejectZ(), Time.deltaTime * divergeSpeed);
                pos.y -= dropSpeed * Time.deltaTime;
                transform.position = pos;

                if (pos.y <= startY - rejectDropDepth && Mathf.Abs(pos.z - rejectZ()) < 0.05f)
                {
                    _dropStarted = true;
                    CanRemove = true;
                }
            }
            // 정상/불량 공통: 바닥 낙하 완료
            else if (_dropStarted)
            {
                Vector3 pos = transform.position;
                pos.y -= dropSpeed * Time.deltaTime;
                transform.position = pos;

                if (pos.y <= startY - goodDropDepth)
                {
                    CanRemove = true;
                }
            }
        }

        private float rejectX() => ConveyorPos - 0.1f;

        private float rejectZ()
        {
            // 불량 박스는 컨베이어 옆(-z)에 위치한다고 가정
            return -1.0f;
        }
    }
}