using System.Collections.Generic;
using UnityEngine;
using InjectionMoldingDT.PLC;

namespace InjectionMoldingDT.Twin
{
    /// <summary>운전 모드: 시뮬레이션(PLC 없음) 또는 라이브(실 PLC 연동).</summary>
    public enum TwinMode
    {
        Simulation,
        Live
    }

    public enum DefectType
    {
        None = 0,
        Scratch = 1,       // 상처/스크래치
        Contamination = 2, // 이물/카본
        Flash = 3,         // 버어/플래시
        Warp = 4,          // 변형/위프
        Other = 5          // 기타
    }

    /// <summary>
    /// 디지털 트윈 메인 컨트롤러.
    ///
    /// - Simulation 모드: PLC 로직을 흉내 내며 공정 시나리오를 진행
    ///   (사출 → 이젝션 → 카운팅 → 비전검사 → 양품/불량 분류)
    /// - Live 모드: PlcConnectionManager의 폴링 스냅샷을 그대로 반영
    ///
    /// 시뮬레이션은 00_디바이스_할당표 및 PLC 레더 프로그램과 동일한
    /// 디바이스 상태(PlcDataSnapshot)를 갱신하므로, 트윈 씬은 두 모드에서
    /// 동일한 코드 경로로 데이터를 소비한다.
    /// </summary>
    public class DigitalTwinController : MonoBehaviour
    {
        [Header("모드")]
        [Tooltip("Simulation = PLC 없이 자체 시나리오 / Live = 실 PLC 연동")]
        public TwinMode mode = TwinMode.Simulation;

        [Header("PLC (Live 모드)")]
        public PlcConnectionManager plcManager;

        [Header("컨베이어 레이아웃 (Simulation)")]
        public float conveyorStartX = -2.0f;
        public float conveyorEndX = 9.0f;
        public float sensorX = 0.6f;
        public float visionX = 4.0f;
        public float rejectX = 6.5f;
        public float rejectDuration = 0.5f;

        [Header("시뮬레이션 파라미터")]
        public float cycleTimeSec = 12.0f;
        public float ejectTimeSec = 1.0f;
        public float beltSpeed = 1.0f;
        [Range(0f, 1f)] public float defectRate = 0.08f;

        [Header("프리팹")]
        public GameObject productPrefab;
        public Transform productParent;

        /// <summary>현재 최신 스냅샷 (표시/동기화용).</summary>
        public PlcDataSnapshot Snap { get; private set; }

        /// <summary>스냅샷 갱신 이벤트.</summary>
        public event System.Action<PlcDataSnapshot> OnSnapshotUpdated;

        private readonly List<ProductSimulation> _products = new List<ProductSimulation>();
        private PlcDataSnapshot _sim;

        private float _nextInjTime;
        private bool _ejecting;
        private float _ejectTimer;
        private bool _rejectActive;
        private float _rejectTimer;
        private float _productSpawnInterval;

        private void Awake()
        {
            if (mode == TwinMode.Live && plcManager == null)
            {
                plcManager = FindObjectOfType<PlcConnectionManager>();
            }
        }

        private void Start()
        {
            if (mode == TwinMode.Live)
            {
                if (plcManager != null) plcManager.StartPolling();
                _sim = new PlcDataSnapshot();
            }
            else
            {
                _sim = new PlcDataSnapshot
                {
                    RunFlag = true,
                    AutoAllowed = true,
                    ConvMotor = true,
                    StepIdle = true,
                    LampRun = true
                };
                _sim.ConvSpeed = (int)(beltSpeed * 100f);
                _nextInjTime = Time.time + 1.5f;
            }

            Snap = _sim;
        }

        private void Update()
        {
            if (mode == TwinMode.Live)
            {
                UpdateLive();
            }
            else
            {
                UpdateSimulation();
            }

            SyncTwins(_sim);
            Snap = _sim;
            OnSnapshotUpdated?.Invoke(_sim);
        }

        //--------------------------------------------------------------
        // Live 모드: PLC 폴링 스냅샷 반영
        //--------------------------------------------------------------
        private void UpdateLive()
        {
            if (plcManager != null && plcManager.HasValidData)
            {
                _sim = plcManager.GetLatestSnapshot();
            }
        }

        //--------------------------------------------------------------
        // Simulation 모드: 공정 시나리오 진행
        //--------------------------------------------------------------
        private void UpdateSimulation()
        {
            // 컨베이어는 항상 운전 상태
            _sim.ConvMotor = true;

            // 1) 사출 → 이젝션
            if (!_ejecting)
            {
                if (Time.time >= _nextInjTime)
                {
                    _sim.InjComplete = true;
                    _sim.StepIdle = false;
                    _sim.StepEject = true;
                    _sim.ShotCount++;
                    _ejecting = true;
                    _ejectTimer = ejectTimeSec;
                }
            }
            else
            {
                _ejectTimer -= Time.deltaTime;
                if (_ejectTimer <= 0f)
                {
                    _sim.InjComplete = false;
                    _sim.EjectHome = true;
                    _sim.StepEject = false;
                    _sim.StepCountWait = true;
                    _ejecting = false;

                    SpawnProduct();
                    _nextInjTime = Time.time + Mathf.Max(0.5f, cycleTimeSec - ejectTimeSec);
                }
            }

            _sim.StepCountWait = !HasActiveProduct() ? _sim.StepCountWait : false;

            // 2) 제품 이동
            MoveProducts();

            // 3) 리젝트 블로우 타이머
            if (_rejectActive)
            {
                _rejectTimer -= Time.deltaTime;
                if (_rejectTimer <= 0f)
                {
                    _rejectActive = false;
                    _sim.RejectBlow = false;
                }
            }

            // 4) 사이클 타임 표시 [0.1초]
            _sim.CycleTimeDec1 = (int)(cycleTimeSec * 10f);
        }

        private bool HasActiveProduct()
        {
            for (int i = 0; i < _products.Count; i++)
            {
                if (!_products[i].Sorted) return true;
            }
            return false;
        }

        private void SpawnProduct()
        {
            if (productPrefab == null) return;

            GameObject go = Instantiate(productPrefab,
                new Vector3(conveyorStartX, 0.5f, 0f),
                Quaternion.identity,
                productParent != null ? productParent : transform);

            ProductSimulation ps = go.GetComponent<ProductSimulation>();
            if (ps == null)
            {
                ps = go.AddComponent<ProductSimulation>();
            }
            ps.Setup(conveyorStartX, beltSpeed);
            _products.Add(ps);
        }

        private void MoveProducts()
        {
            for (int i = _products.Count - 1; i >= 0; i--)
            {
                ProductSimulation p = _products[i];
                if (p == null) { _products.RemoveAt(i); continue; }

                if (!p.Sorted) p.Advance(beltSpeed * Time.deltaTime);
                float x = p.ConveyorPos; // 컨베이어 진행 거리

                // (a) 카운팅 센서 통과
                if (!p.Counted && x >= sensorX)
                {
                    p.Counted = true;
                    _sim.TotalCount++;
                    _sim.StepCountWait = false;
                    _sim.StepVision = true;
                    _sim.CountSensor = true;
                }
                else if (p.Counted && _sim.CountSensor && x >= sensorX + 0.2f)
                {
                    _sim.CountSensor = false; // 센서 OFF (제품 통과 완료)
                }

                // (b) 비전 검사
                if (!p.Inspected && x >= visionX)
                {
                    p.Inspected = true;
                    Inspect(p);
                }

                // (c) 불량 리젝트 (리젝터 위치 도달 시)
                if (p.IsDefect && !p.Sorted && x >= rejectX)
                {
                    p.Sorted = true;
                    _sim.RejectBlow = true;
                    _rejectActive = true;
                    _rejectTimer = rejectDuration;
                    _sim.StepVision = false;
                    _sim.StepSortNg = true;
                    p.DiverToReject();
                }

                // (d) 정상 제품 - 컨베이어 끝 도달
                if (!p.IsDefect && !p.Sorted && x >= conveyorEndX - 0.3f)
                {
                    p.Sorted = true;
                    _sim.StepVision = false;
                    _sim.StepSortOk = true;
                    p.ArriveGoodEnd();
                }

                // (e) 정렬 완료된 제품은 잠시 후 제거
                if (p.Sorted && p.CanRemove)
                {
                    _products.RemoveAt(i);
                    Destroy(p.gameObject, 0.1f);
                    _sim.StepSortOk = false;
                    _sim.StepSortNg = false;
                    _sim.StepIdle = true;
                }
            }
        }

        private void Inspect(ProductSimulation p)
        {
            _sim.VisionDone = true;

            if (Random.value < defectRate)
            {
                p.IsDefect = true;
                p.Defect = RandomDefectType();
                _sim.NgCount++;
                _sim.VisionCode = (int)p.Defect;

                switch (p.Defect)
                {
                    case DefectType.Scratch: _sim.NgType1Count++; break;
                    case DefectType.Contamination: _sim.NgType2Count++; break;
                    case DefectType.Flash: _sim.NgType3Count++; break;
                    case DefectType.Warp: _sim.NgType4Count++; break;
                    default: _sim.NgOtherCount++; break;
                }
                _sim.VisionNg = true;
            }
            else
            {
                p.IsDefect = false;
                p.Defect = DefectType.None;
                _sim.OkCount++;
                _sim.VisionCode = 0;
                _sim.VisionOk = true;
            }
        }

        private DefectType RandomDefectType()
        {
            int r = Random.Range(1, 6);
            return (DefectType)r;
        }

        //--------------------------------------------------------------
        // 트윈 시각 오브젝트와 상태 동기화 (두 모드 공용)
        //--------------------------------------------------------------
        private void SyncTwins(PlcDataSnapshot snap)
        {
            ConveyorSimulation conveyor = GetConveyor();
            if (conveyor != null)
            {
                conveyor.SetBeltActive(snap.ConvMotor);
            }

            VisionInspection vision = GetVision();
            if (vision != null)
            {
                vision.SetInspection(snap.StepVision && snap.RunFlag, snap.VisionOk, snap.VisionNg, snap.VisionCode);
            }
        }

        private ConveyorSimulation _cachedConveyor;
        private ConveyorSimulation GetConveyor()
        {
            if (_cachedConveyor == null)
            {
                _cachedConveyor = FindObjectOfType<ConveyorSimulation>();
            }
            return _cachedConveyor;
        }

        private VisionInspection _cachedVision;
        private VisionInspection GetVision()
        {
            if (_cachedVision == null)
            {
                _cachedVision = FindObjectOfType<VisionInspection>();
            }
            return _cachedVision;
        }

        /// <summary>시뮬레이션 모드에서 현재 진행 중 스냅샷 (디버그용).</summary>
        public PlcDataSnapshot GetSnap() => _sim;
    }
}