using System;
using System.Threading;
using UnityEngine;

namespace InjectionMoldingDT.PLC
{
    /// <summary>
    /// PLC에서 폴링으로 수집한 최신 상태 스냅샷.
    /// 모든 값은 00_디바이스_할당표.md 와 1:1 대응된다.
    /// </summary>
    [Serializable]
    public sealed class PlcDataSnapshot
    {
        // 입력 (X0~XD)
        public bool InjComplete;    // X0  사출 완료
        public bool EjectHome;      // X1  이젝션 완료
        public bool CountSensor;    // X2  카운팅 센서
        public bool VisionOk;       // X3  비전 OK
        public bool VisionNg;       // X4  비전 NG
        public bool VisionDone;     // X5  비전 검사 완료
        public bool BinFull;        // X6  불량 박스 풀
        public bool EStop;          // X7  비상정지 (NC)
        public bool GuardDoor;      // X8  가드 도어
        public bool ConvOverload;   // X9  컨베이어 과부하
        public bool ModeManual;     // XA  수동/자동
        public bool StartBtn;       // XB  스타트
        public bool StopBtn;        // XC  스톱
        public bool ResetBtn;       // XD  리셋

        // 출력 (Y0~Y6)
        public bool ConvMotor;      // Y0  컨베이어 모터
        public bool RejectBlow;     // Y1  리젝트 에어 블로우
        public bool InjStart;       // Y2  사출기 기동
        public bool LampRun;        // Y3  운전 램프
        public bool LampAlarm;      // Y4  알람 램프
        public bool Buzzer;         // Y5  부저
        public bool EjectReq;       // Y6  이젝션 지령

        // 내부 릴레이 (M)
        public bool RunFlag;        // M0  운전 중
        public bool AutoAllowed;    // M1  자동 모드 허가
        public bool AlarmFlag;      // M2  알람
        public bool SeqHold;        // M3  시퀀스 홀드
        public bool StepIdle;       // M10 대기
        public bool StepEject;      // M11 이젝션
        public bool StepCountWait;  // M12 카운팅 대기
        public bool StepVision;     // M13 비전 대기
        public bool StepSortOk;     // M14 분류(양품)
        public bool StepSortNg;     // M15 분류(불량)
        public bool VisionResultLatch; // M20 불량 래치
        public bool RejectFire;     // M22 리젝트 발사

        // 카운트/파라미터 (D)
        public int TotalCount;      // D0
        public int OkCount;         // D1
        public int NgCount;         // D2
        public int CycleTimeDec1;   // D10 (0.1초 단위)
        public int TargetQty;       // D11
        public int ConvSpeed;       // D12 [%]
        public int MoldTemp;        // D20 [x0.1℃]
        public int InjPressure;     // D21 [x0.1MPa]
        public int InjSpeed;        // D22 [x0.1 mm/s]
        public int ShotCount;       // D23
        public int VisionCode;      // D100 (0=OK, 1~9=결함)
        public int NgType1Count;    // D101
        public int NgType2Count;    // D102
        public int NgType3Count;    // D103
        public int NgType4Count;    // D104
        public int NgOtherCount;    // D105
        public int AlarmCode;       // D900

        /// <summary>결함 유형별 합 (D101~D105 합산).</summary>
        public int DefectTotal => NgType1Count + NgType2Count + NgType3Count + NgType4Count + NgOtherCount;

        /// <summary>불량률[%] (총 생산 > 0 일 때).</summary>
        public float NgRate => TotalCount > 0 ? (NgCount * 100f) / TotalCount : 0f;

        /// <summary>품질률[%].</summary>
        public float QualityRate => TotalCount > 0 ? (OkCount * 100f) / TotalCount : 0f;

        // 알람 활성 여부 편의 속성
        public bool HasAlarm => AlarmFlag || AlarmCode != 0 || LampAlarm;
    }

    /// <summary>
    /// 실(實) PLC와의 연결을 담당하는 유니티 컴포넌트.
    /// 백그라운드 스레드에서 MC Protocol 배치 읽기로 상태를 폴링하여
    /// <see cref="PlcDataSnapshot"/> 최신값으로 갱신한다.
    ///
    /// 사용:
    ///   1. 씬에 빈 GameObject 생성 후 이 컴포넌트 부착
    ///   2. Inspector에서 IP/Port 폴링주기 설정
    ///   3. 각 트윈 컴포넌트에서 `GetLatestSnapshot()` 호출
    /// </summary>
    public class PlcConnectionManager : UnityEngine.MonoBehaviour, IDisposable
    {
        [Header("PLC 연결")]
        [UnityEngine.Tooltip("미쓰비시 PLC IP 주소")]
        public string plcIp = "192.168.3.39";
        [UnityEngine.Tooltip("MC Protocol 포트 (기본 5001)")]
        public int plcPort = 5001;
        [UnityEngine.Tooltip("통신 타임아웃 [ms]")]
        public int timeoutMs = 2000;
        [UnityEngine.Tooltip("폴링 주기 [ms]")]
        public int pollingIntervalMs = 25;

        [Header("상태")]
        [UnityEngine.ReadOnly] public bool isConnected;

        private McProtocolClient _client;
        private Thread _pollThread;
        private volatile bool _running;

        private readonly object _lock = new object();
        private PlcDataSnapshot _snapshot = new PlcDataSnapshot();
        private bool _hasValidData;

        public event System.Action<PlcDataSnapshot> OnPolled;

        /// <summary>최신 스냅샷 (쓰레드 안전).</summary>
        public PlcDataSnapshot GetLatestSnapshot()
        {
            lock (_lock) { return _snapshot; }
        }

        /// <summary>PLC로부터 한 번이라도 정상 수신이 되었는지.</summary>
        public bool HasValidData
        {
            get { lock (_lock) { return _hasValidData; } }
        }

        private void OnEnable()
        {
            isConnected = false;
        }

        public void StartPolling()
        {
            if (_running) return;

            if (_client == null)
            {
                _client = new McProtocolClient(plcIp, plcPort, timeoutMs);
            }

            _running = true;
            _pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "McProtocolPoller"
            };
            _pollThread.Start();
        }

        public void StopPolling()
        {
            _running = false;
            if (_pollThread != null && _pollThread.IsAlive)
            {
                _pollThread.Join(1000);
            }
            _pollThread = null;
        }

        private void PollLoop()
        {
            int failCount = 0;
            while (_running)
            {
                bool ok = false;
                try
                {
                    if (!_client.IsConnected)
                    {
                        if (!_client.Connect())
                        {
                            failCount++;
                            UnityEngine.Debug.LogWarning("[PLC] 연결 실패: " + _client.LastError);
                            Thread.Sleep(500);
                            continue;
                        }
                    }

                    var frame = PollOnce();
                    if (frame != null)
                    {
                        lock (_lock)
                        {
                            _snapshot = frame;
                            _hasValidData = true;
                        }
                        isConnected = true;
                        failCount = 0;
                        OnPolled?.Invoke(frame);
                        ok = true;
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    UnityEngine.Debug.LogWarning("[PLC] 폴링 오류: " + ex.Message);
                }

                if (!ok)
                {
                    isConnected = false;
                    _client.Disconnect();
                }

                Thread.Sleep(pollingIntervalMs);

                if (failCount > 10)
                {
                    UnityEngine.Debug.LogWarning("[PLC] 10회 연속 실패 - 재연결 대기 중...");
                    Thread.Sleep(1000);
                    failCount = 0;
                }
            }
        }

        /// <summary>한 번의 폴링 사이클. 연속 영역을 배치 읽기로 수집한다.</summary>
        private PlcDataSnapshot PollOnce()
        {
            var snap = new PlcDataSnapshot();

            // 입력 X0~XD (14점)
            bool[] x = _client.ReadBits(PlcDevice.X, 0, 14);
            snap.InjComplete   = x[0];
            snap.EjectHome     = x[1];
            snap.CountSensor   = x[2];
            snap.VisionOk      = x[3];
            snap.VisionNg      = x[4];
            snap.VisionDone    = x[5];
            snap.BinFull       = x[6];
            snap.EStop         = x[7];
            snap.GuardDoor     = x[8];
            snap.ConvOverload  = x[9];
            snap.ModeManual    = x[10];
            snap.StartBtn      = x[11];
            snap.StopBtn       = x[12];
            snap.ResetBtn      = x[13];

            // 출력 Y0~Y6 (7점)
            bool[] y = _client.ReadBits(PlcDevice.Y, 0, 7);
            snap.ConvMotor    = y[0];
            snap.RejectBlow   = y[1];
            snap.InjStart     = y[2];
            snap.LampRun      = y[3];
            snap.LampAlarm    = y[4];
            snap.Buzzer       = y[5];
            snap.EjectReq     = y[6];

            // 내부 릴레이 M0~M44 (45점) - 운전/스텝/분류 플래그
            bool[] m = _client.ReadBits(PlcDevice.M, 0, 45);
            snap.RunFlag       = m[0];
            snap.AutoAllowed   = m[1];
            snap.AlarmFlag     = m[2];
            snap.SeqHold       = m[3];
            snap.StepIdle      = m[10];
            snap.StepEject     = m[11];
            snap.StepCountWait = m[12];
            snap.StepVision    = m[13];
            snap.StepSortOk    = m[14];
            snap.StepSortNg    = m[15];
            snap.VisionResultLatch = m[20];
            snap.RejectFire    = m[22];

            // 워드 D0~D105 (106점, 연속 1회 읽기로 최적화)
            ushort[] d = _client.ReadWords(PlcDevice.D, 0, 106);
            snap.TotalCount    = d[0];
            snap.OkCount       = d[1];
            snap.NgCount       = d[2];
            snap.CycleTimeDec1 = d[10];
            snap.TargetQty     = d[11];
            snap.ConvSpeed     = d[12];
            snap.MoldTemp      = d[20];
            snap.InjPressure   = d[21];
            snap.InjSpeed      = d[22];
            snap.ShotCount     = d[23];
            snap.VisionCode    = d[100];
            snap.NgType1Count  = d[101];
            snap.NgType2Count  = d[102];
            snap.NgType3Count  = d[103];
            snap.NgType4Count  = d[104];
            snap.NgOtherCount  = d[105];

            // 알람 코드 D900 (단건 읽기)
            ushort[] a = _client.ReadWords(PlcDevice.D, 900, 1);
            snap.AlarmCode = a[0];

            return snap;
        }

        private void OnDisable()
        {
            StopPolling();
        }

        public void Dispose()
        {
            StopPolling();
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
        }
    }
}