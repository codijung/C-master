// RebarCalculator.cs
// -----------------------------------------------------------------------------
// 입력(계산창 In DTO) -> 계산 -> 출력(Rebar Plan DTO)
// - 피복: 40mm 고정
// - 후프 두께(=지름) t_h, 주근 반지름 r_v, nx/ny, 높이, 단면 from 일람
// - 수직근: 네 모서리 포함, 외곽 균등 간격 배치(중복 제거)
// - 외곽 후프: 모든 수직근을 감싸는 사각 폴리곤, 각 z층마다 2가닥(시작 45/끝 90 & 시작 90/끝 45)
// - 쌍연결 후프: JSON으로 지정된 수직근 쌍(일직선)별로 폴리곤 생성, 층마다 훅 각도 교차(45↔90)
// - 엔드 구간 길이: max(450, 장변길이, 높이/6) (단, H/2로 캡), EXACT_SPACINGS 배열 생성
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using static System.Collections.Specialized.BitVector32;

namespace ColumnRebar.Calculator
{
    // ---------------------------
    // 공통 기하/스펙 DTO
    // ---------------------------

    public record Vec3(double X, double Y, double Z);
    public record LocalCS(Vec3 Origin, Vec3 U, Vec3 V, Vec3 K);     // 로컬 좌표계 (Tekla WorkPlane에서 그대로 사용)
    public record Section(double Width, double Depth);               // 단면 (mm)

    public record RebarSpec(
        double HoopDia,     // t_h : 후프 지름(mm)
        double VertRadius,  // r_v : 주근 반지름(mm)
        int Nx,             // 가로 수직근 개수 (상/하변)
        int Ny              // 세로 수직근 개수 (좌/우변)
    );

    public record SpacingRule(                                  // 띠철근 스페이싱 규칙
        int BottomCount, double BottomStep,                     // 하단 집중 개수/간격
        double MidStep,                                         // 중앙 등간격
        int TopCount, double TopStep                            // 상단 집중 개수/간격
    );

    public enum AltStartAngle { Start45, Start90 }              // 교차 훅 시작 기준

    public record PairHoopSpec(string A, string B);             // 쌍연결 후프 대상 수직근 ID 쌍

    // ---------------------------
    // 입력/출력 DTO
    // ---------------------------

    public record ColumnInput(                                  // 계산창 입력
        string ColumnId,
        LocalCS CS,
        Section Sec,
        double Height,
        RebarSpec Rebar,
        SpacingRule HoopSpacing,
        AltStartAngle PairHoopAltStart,
        double PairHoopClearanceExtra,                          // c_pair (권장 5mm)
        IReadOnlyList<PairHoopSpec> PairHoops                   // 쌍연결 후프 목록 (없으면 빈 리스트)
    );

    // 수직근 한 가닥(센터 경로: z=0~H)
    public record VerticalBar(string Id, double X, double Y, double Z0, double Z1, double Dia);

    // 폴리곤 + z 레벨 + 훅 각도/길이를 갖는 단일 후프 인스턴스
    public record HoopInstance(
        string Kind,                                            // "OUTER_A"/"OUTER_B"/"PAIR"
        IReadOnlyList<(double x, double y)> PolygonXY,          // 로컬 평면 폴리곤(센터라인, z=0 평면)
        double Z,                                               // 삽입 z 레벨
        double Dia,                                             // 후프 지름
        double StartHookDeg, double EndHookDeg,                 // 훅 각도(도)
        double HookLen                                          // 훅 길이(mm) - 프로젝트 표준(지름×배수 등) 외부에서 결정/주입 가능
    );

    // 최종 산출물: 수직근 좌표 + 외곽/쌍 후프 인스턴스
    public record ColumnRebarPlan(
        string ColumnId,
        LocalCS CS,
        Section Sec,
        double Height,
        double Cover,                                           // 40 고정
        IReadOnlyList<VerticalBar> VerticalBars,
        IReadOnlyList<HoopInstance> Hoops,
        IReadOnlyList<double> OuterHoopExactSpacings,           // 참고용(검증/로그)
        double EndZoneLength                                    // 계산된 엔드구간 길이(mm)
    );

    // ---------------------------
    // 계산기
    // ---------------------------

    public static class RebarCalculator
    {
        public const double COVER = 40.0;      // 피복 40mm 고정
        private const double EPS = 1e-6;

        // 메인 진입점: 입력 -> 계산 -> 플랜 반환
        public static ColumnRebarPlan Calculate(ColumnInput input, Func<double, double>? hookLengthRule = null)
        {
            // 0) 단면/스펙 로컬 변수
            var (W, D) = (input.Sec.Width, input.Sec.Depth);
            var H = input.Height;
            var (t_h, r_v, nx, ny) = (input.Rebar.HoopDia, input.Rebar.VertRadius, input.Rebar.Nx, input.Rebar.Ny);

            if (nx < 2 || ny < 2)
                throw new ArgumentException("nx, ny는 각각 2 이상이어야 합니다.");

            // 1) 오프셋 및 내부 반치수 계산
            // 후프 센터 오프셋 = 40 + t_h/2
            // 주근 센터 오프셋 = 40 + t_h + r_v
            double offsetHoop = COVER + t_h * 0.5;
            double offsetVert = COVER + t_h + r_v;

            double w2Hoop = W * 0.5 - offsetHoop;
            double d2Hoop = D * 0.5 - offsetHoop;
            if (w2Hoop <= 0 || d2Hoop <= 0)
                throw new ArgumentException("외곽 후프 내부 치수가 0 이하입니다. (피복/후프 지름/단면 확인)");

            double w2Vert = W * 0.5 - offsetVert;
            double d2Vert = D * 0.5 - offsetVert;
            if (w2Vert <= 0 || d2Vert <= 0)
                throw new ArgumentException("수직근 배치 가능 영역이 0 이하입니다. (피복/후프/주근/단면 확인)");

            // 2) 수직근 좌표(외곽 균등 + 코너 포함, 중복 제거)
            var vbars = BuildPerimeterVerticalBars(input.ColumnId, w2Vert, d2Vert, nx, ny, r_v, H);

            // 3) 엔드 구간 길이 & 외곽 스페이싱(참조용 배열)
            double endLen = EndZoneLength(W, D, H);
            var spacingsRef = BuildExactSpacings(H, input.HoopSpacing.BottomCount, input.HoopSpacing.BottomStep,
                                                 input.HoopSpacing.MidStep, input.HoopSpacing.TopCount, input.HoopSpacing.TopStep,
                                                 endLen);

            // 4) 외곽 후프: 각 z 레벨마다 2가닥 (Start 45 / 90 교차 동시)
            var outerBasePoly = OuterHoopPolygon(w2Hoop, d2Hoop);
            var zLayers = BuildZLayersFromSpacings(spacingsRef); // 누적된 z 레벨들
            double hookLenDefault = hookLengthRule?.Invoke(t_h) ?? DefaultHookLength(t_h);

            var hoops = new List<HoopInstance>();
            foreach (var z in zLayers)
            {
                // A: Start 45, End 90
                hoops.Add(new HoopInstance(
                    Kind: "OUTER_A",
                    PolygonXY: outerBasePoly,
                    Z: z, Dia: t_h,
                    StartHookDeg: 45.0, EndHookDeg: 90.0,
                    HookLen: hookLenDefault
                ));
                // B: Start 90, End 45
                hoops.Add(new HoopInstance(
                    Kind: "OUTER_B",
                    PolygonXY: outerBasePoly,
                    Z: z, Dia: t_h,
                    StartHookDeg: 90.0, EndHookDeg: 45.0,
                    HookLen: hookLenDefault
                ));
            }

            // 5) 쌍연결 후프: 대상 수직근 ID 쌍별 -> 폴리곤 생성 -> 층마다 훅 교차(45<->90)
            var barIndex = vbars.ToDictionary(b => b.Id, b => (b.X, b.Y));
            int layerIdx = 0;
            foreach (var pair in input.PairHoops ?? Array.Empty<PairHoopSpec>())
            {
                if (!barIndex.TryGetValue(pair.A, out var A) || !barIndex.TryGetValue(pair.B, out var B))
                    throw new ArgumentException($"쌍연결 후프 대상 수직근 ID 미존재: {pair.A} or {pair.B}");

                var pairPoly = BuildPairHoopPolygon(A, B, t_h, r_v, input.PairHoopClearanceExtra);
                foreach (var z in zLayers)
                {
                    var (sd, ed) = HookAngles(layerIdx, input.PairHoopAltStart);
                    hoops.Add(new HoopInstance(
                        Kind: "PAIR",
                        PolygonXY: pairPoly,
                        Z: z, Dia: t_h,
                        StartHookDeg: sd, EndHookDeg: ed,
                        HookLen: hookLenDefault
                    ));
                    layerIdx++;
                }
            }

            return new ColumnRebarPlan(
                ColumnId: input.ColumnId,
                CS: input.CS,
                Sec: input.Sec,
                Height: H,
                Cover: COVER,
                VerticalBars: vbars,
                Hoops: hoops,
                OuterHoopExactSpacings: spacingsRef,
                EndZoneLength: endLen
            );
        }

        // ---------------------------
        // 수직근 배치
        // ---------------------------

        private static List<VerticalBar> BuildPerimeterVerticalBars(string columnId, double w2, double d2, int nx, int ny, double r_v, double H)
        {
            var pts = new List<(double x, double y)>();

            // 가로 간격 (상/하변)
            double sx = (2.0 * w2) / (nx - 1);
            // 세로 간격 (좌/우변)
            double sy = (2.0 * d2) / (ny - 1);

            // 상변(+y), 하변(-y)
            for (int i = 0; i < nx; i++)
            {
                double x = -w2 + i * sx;
                pts.Add((x, +d2));
                pts.Add((x, -d2));
            }

            // 좌변(-x), 우변(+x)
            for (int j = 0; j < ny; j++)
            {
                double y = -d2 + j * sy;
                pts.Add((-w2, y));
                pts.Add((+w2, y));
            }

            // 중복 제거(꼭짓점)
            var dedup = new List<(double x, double y)>();
            foreach (var p in pts)
            {
                bool exists = dedup.Any(q => Math.Abs(q.x - p.x) <= EPS && Math.Abs(q.y - p.y) <= EPS);
                if (!exists) dedup.Add(p);
            }

            // 아이디 부여: V01, V02, ...
            var result = new List<VerticalBar>(dedup.Count);
            for (int i = 0; i < dedup.Count; i++)
            {
                string id = $"V{(i + 1).ToString().PadLeft(2, '0')}";
                result.Add(new VerticalBar(id, dedup[i].x, dedup[i].y, 0.0, H, Dia: r_v * 2.0));
            }

            // (선택) 최소 피치 검증 로직을 추가하려면 여기서 dedup 정렬 후 인접 거리 검사 가능
            return result;
        }

        // ---------------------------
        // 외곽 후프
        // ---------------------------

        private static List<(double x, double y)> OuterHoopPolygon(double w2Hoop, double d2Hoop)
        {
            return new List<(double x, double y)>
            {
                (+w2Hoop, +d2Hoop),
                (-w2Hoop, +d2Hoop),
                (-w2Hoop, -d2Hoop),
                (+w2Hoop, -d2Hoop)
            };
        }

        // EXACT_SPACINGS -> 누적 z 레벨
        private static List<double> BuildZLayersFromSpacings(IReadOnlyList<double> spacings)
        {
            double acc = 0.0;
            var zs = new List<double>(spacings.Count);
            foreach (var s in spacings)
            {
                acc += s;
                zs.Add(acc);
            }
            return zs;
        }

        // 엔드 구간 길이: max(450, 장변, 높이/6), 단 H/2로 캡
        public static double EndZoneLength(double width, double depth, double height)
        {
            double longSide = Math.Max(width, depth);
            double byHeight = height / 6.0;
            double L = Math.Max(450.0, Math.Max(longSide, byHeight));
            return Math.Min(L, height * 0.5);
        }

        // EXACT_SPACINGS 배열 생성 (하단→중앙→상단)
        public static List<double> BuildExactSpacings(
            double height,
            int bottomCount, double bottomStep,
            double midStep,
            int topCount, double topStep,
            double endLen)
        {
            var spacings = new List<double>();
            double used = 0.0;

            // 하단 엔드
            for (int i = 0; i < bottomCount; i++)
            {
                if (used + bottomStep > height + EPS) break;
                spacings.Add(bottomStep);
                used += bottomStep;
                if (used > endLen - EPS && bottomStep > 0) { /* 목표 endLen을 넘겨도 EXACT 합이 맞으면 승인 */ }
            }

            // 중앙
            if (midStep > 0)
            {
                while (true)
                {
                    double next = used + midStep;
                    double topNeeded = topCount * topStep;
                    if (next + topNeeded > height + EPS) break;
                    spacings.Add(midStep);
                    used = next;
                }
            }

            // 상단 엔드
            for (int i = 0; i < topCount; i++)
            {
                if (used + topStep > height + EPS) break;
                spacings.Add(topStep);
                used += topStep;
            }

            // 마지막 높이 초과 방지
            while (Sum(spacings) > height + EPS && spacings.Count > 0)
            {
                spacings.RemoveAt(spacings.Count - 1);
            }

            return spacings;
        }

        private static double Sum(IReadOnlyList<double> a)
        {
            double s = 0; for (int i = 0; i < a.Count; i++) s += a[i]; return s;
        }

        // ---------------------------
        // 쌍연결 후프
        // ---------------------------

        // 두 수직근을 감싸는 직사각형(센터) 폴리곤 (수평/수직 자동 판정)
        private static List<(double x, double y)> BuildPairHoopPolygon(
            (double x, double y) A,
            (double x, double y) B,
            double t_h, double r_v,
            double c_pair)
        {
            double margin = r_v + t_h * 0.5 + c_pair;

            // 수평 (y가 같다)
            if (Math.Abs(A.y - B.y) <= EPS)
            {
                double y0 = A.y;
                double xL = Math.Min(A.x, B.x) - margin;
                double xR = Math.Max(A.x, B.x) + margin;
                double yU = y0 + margin;
                double yD = y0 - margin;

                return new List<(double x, double y)>
                {
                    (xR, yU), (xL, yU),
                    (xL, yD), (xR, yD)
                };
            }
            // 수직 (x가 같다)
            else if (Math.Abs(A.x - B.x) <= EPS)
            {
                double x0 = A.x;
                double yD = Math.Min(A.y, B.y) - margin;
                double yU = Math.Max(A.y, B.y) + margin;
                double xR = x0 + margin;
                double xL = x0 - margin;

                return new List<(double x, double y)>
                {
                    (xR, yU), (xL, yU),
                    (xL, yD), (xR, yD)
                };
            }
            else
            {
                throw new ArgumentException("쌍연결 후프는 같은 x 또는 같은 y에 있는 수직근 쌍이어야 합니다.");
            }
        }

        // 층 index에 따라 훅 각도 교차
        private static (double startDeg, double endDeg) HookAngles(int layerIndex, AltStartAngle first)
        {
            bool even = (layerIndex % 2 == 0);
            if (first == AltStartAngle.Start45)
                return even ? (45.0, 90.0) : (90.0, 45.0);
            else
                return even ? (90.0, 45.0) : (45.0, 90.0);
        }

        // (선택) 프로젝트 표준 훅 길이 규칙: 예) 12 * 지름
        private static double DefaultHookLength(double dia) => 12.0 * dia;
    }
}
