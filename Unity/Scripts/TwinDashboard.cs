using UnityEngine;
using InjectionMoldingDT.PLC;

namespace InjectionMoldingDT.Twin
{
    /// <summary>
    /// KPI 대시보드 (OnGUI 기반 - 별도 UI 패키지 없이 동작).
    /// DigitalTwinController의 최신 스냅샷을 읽어 생산 지표를 표시한다.
    /// 불량률 / 품질률 / 결함 유형 분포 / 사이클 타임 / 알람 상태.
    /// </summary>
    public class TwinDashboard : MonoBehaviour
    {
        [Header("참조")]
        public DigitalTwinController twinController;

        [Header("화면")]
        public int left = 16;
        public int top = 16;
        public float scale = 1.25f;

        private GUIStyle _style;
        private GUIStyle _titleStyle;
        private GUIStyle _alertStyle;
        private Rect _panelRect;

        private void Start()
        {
            if (twinController == null)
            {
                twinController = FindObjectOfType<DigitalTwinController>();
            }
        }

        private void OnGUI()
        {
            if (twinController == null || twinController.Snap == null) return;

            PlcDataSnapshot s = twinController.Snap;

            EnsureStyles();

            const float w = 420f;
            float h = 260f;
            _panelRect = new Rect(left, top, w * scale, h * scale);

            GUI.Box(_panelRect, GUIContent.none);

            GUILayout.BeginArea(new Rect(left + 10, top + 6, w * scale - 20, h * scale - 12));
            GUILayout.BeginVertical();

            GUILayout.Label("사출 공장 디지털 트윈 - KPI", _titleStyle);

            string mode = twinController.mode == TwinMode.Live ? "LIVE (PLC 연동)" : "SIMULATION";
            GUILayout.Label("Mode: " + mode + "   |   " +
                            (twinController.mode == TwinMode.Live
                                ? (s.HasAlarm ? "알람코드 " + s.AlarmCode : "정상 운전")
                                : "시뮬레이션 운전 중"));

            GUILayout.Space(6);

            GUILayout.Label(string.Format("총 생산 : {0,5} pcs", s.TotalCount));
            GUILayout.Label(string.Format("양품    : {0,5} pcs", s.OkCount));
            GUILayout.Label(string.Format("불량    : {0,5} pcs   (불량률 {1:F1}%)", s.NgCount, s.NgRate));

            GUILayout.Space(4);

            GUILayout.Label(string.Format("사이클 타임 : {0:F1} s   ({1} x0.1s)",
                s.CycleTimeDec1 * 0.1f, s.CycleTimeDec1));
            GUILayout.Label(string.Format("샷 카운터(금형): {0}   |   금형온도 {1:F1} ℃",
                s.ShotCount, s.MoldTemp * 0.1f));

            GUILayout.Space(6);

            GUILayout.Label("결함 유형 분포");
            GUILayout.Label(string.Format("  상처/스크래치 : {0}    이물/카본 : {1}", s.NgType1Count, s.NgType2Count));
            GUILayout.Label(string.Format("  버어/플래시   : {0}    변형/위프 : {1}", s.NgType3Count, s.NgType4Count));
            GUILayout.Label(string.Format("  기타 결함     : {0}", s.NgOtherCount));

            GUILayout.Space(6);

            if (s.HasAlarm)
            {
                GUILayout.Label("!! 알람 발생 - 코드 " + s.AlarmCode + " (" + AlarmText(s.AlarmCode) + ")", _alertStyle);
            }
            else
            {
                GUILayout.Label("상태: 정상", _style);
            }

            if (s.StepEject)  GUILayout.Label("[공정] 이젝션 중");
            if (s.StepCountWait) GUILayout.Label("[공정] 카운팅 대기");
            if (s.StepVision) GUILayout.Label("[공정] 비전 검사");
            if (s.StepSortNg) GUILayout.Label("[공정] 불량 분류 중");
            if (s.StepSortOk) GUILayout.Label("[공정] 양품 회수");

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private string AlarmText(int code)
        {
            switch (code)
            {
                case 0: return "정상";
                case 1: return "비상정지";
                case 2: return "가드 도어";
                case 3: return "컨베이어 과부하";
                case 4: return "불량 박스 풀";
                case 5: return "카운팅 센서 이상";
                case 6: return "이젝션 타임아웃";
                default: return "미확인";
            }
        }

        private void EnsureStyles()
        {
            if (_style != null) return;

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * scale)
            };
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(17 * scale),
                fontStyle = FontStyle.Bold
            };
            _alertStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14 * scale),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.red }
            };
        }
    }
}