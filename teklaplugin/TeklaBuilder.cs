// TeklaBuilder.cs
// -----------------------------------------------------------------------------
// ColumnRebarPlan(계산 결과 DTO) -> Tekla 모델 객체 Insert
// 1) WorkPlane = Local CS
// 2) 콘크리트 기둥 생성 (RECT profile)
// 3) 수직근: RebarGroup (각 좌표에 한 가닥씩 직선 바)
// 4) 후프: SingleRebar (사각형 폴리곤 + 훅 각도/길이)
// 5) Commit & WorkPlane 복귀
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Model;
using Tekla.Structures.Geometry3d;
using ColumnRebar.Calculator;   // RebarCalculator 프로젝트의 DTO들

namespace ColumnRebar.Tekla
{
    public class TeklaBuilder
    {
        private readonly Model _model;

        public TeklaBuilder()
        {
            _model = new Model();
            if (!_model.GetConnectionStatus())
                throw new InvalidOperationException("Tekla Structures 모델에 연결되지 않았습니다.");
        }

        // ---------------------------
        // Public entry
        // ---------------------------
        public void Build(ColumnRebarPlan plan, string concreteGrade = "C30/37",
                          string rebarGrade = "SD400", string columnClass = "3",
                          string rebarClass = "11")
        {
            // 0) WorkPlane = Local CS
            var wph = _model.GetWorkPlaneHandler();
            var backup = wph.GetCurrentTransformationPlane();
            wph.SetCurrentTransformationPlane(ToPlane(plan.CS));

            try
            {
                // 1) 콘크리트 기둥
                var column = InsertConcreteColumn(plan, concreteGrade, columnClass);

                // 2) 수직근
                InsertVerticalBars(plan, column, rebarGrade, rebarClass);

                // 3) 후프(외곽 + 쌍연결)
                InsertHoops(plan, column, rebarGrade, rebarClass);

                _model.CommitChanges();
            }
            finally
            {
                // WorkPlane 복귀
                var wph2 = _model.GetWorkPlaneHandler();
                wph2.SetCurrentTransformationPlane(backup);
            }
        }

        // ---------------------------
        // 1) Concrete Column
        // ---------------------------
        private Part InsertConcreteColumn(ColumnRebarPlan plan, string concreteGrade, string columnClass)
        {
            var beam = new Beam(new Point(0, 0, 0), new Point(0, 0, plan.Height));
            // 프로파일: RECT{W}*{D}  (정수 표기가 관례)
            beam.Profile.ProfileString = $"RECT{ToInt(plan.Sec.Width)}*{ToInt(plan.Sec.Depth)}";
            beam.Material.MaterialString = concreteGrade;
            beam.Finish = string.Empty;
            beam.Class = columnClass;
            if (!beam.Insert())
                throw new InvalidOperationException("콘크리트 기둥 삽입 실패");
            return beam;
        }

        // ---------------------------
        // 2) Vertical Bars (RebarGroup 1가닥씩)
        // ---------------------------
        private void InsertVerticalBars(ColumnRebarPlan plan, Part father, string grade, string klass)
        {
            foreach (var vb in plan.VerticalBars)
            {
                var rg = new RebarGroup
                {
                    Father = father,
                    Name = vb.Id,                 // 수직근 ID를 Name에 기입(추적용)
                    Class = klass,
                    Grade = grade,
                    Size = $"D{ToInt(vb.Dia)}",   // 지름
                    StartPoint = new Point(vb.X, vb.Y, vb.Z0),
                    EndPoint = new Point(vb.X, vb.Y, vb.Z1),
                    SpacingType = RebarGroup.SpacingTypeEnum.SPACING_TYPE_EXACT_SPACINGS
                };

                // 단일 가닥으로 만들기 위해 OnPlaneOffsets에 0.0만 추가
                rg.OnPlaneOffsets.Add(0.0);
                rg.FromPlaneOffset = 0.0;

                if (!rg.Insert())
                    throw new InvalidOperationException($"수직근 삽입 실패: {vb.Id}");
            }
        }

        // ---------------------------
        // 3) Hoops (SingleRebar)
        //   - OUTER_A / OUTER_B : 외곽 (같은 z에서 두 가닥)
        //   - PAIR               : 쌍연결
        // ---------------------------
        private void InsertHoops(ColumnRebarPlan plan, Part father, string grade, string klass)
        {
            foreach (var h in plan.Hoops)
            {
                var sr = new SingleRebar
                {
                    Father = father,
                    Name = h.Kind,                 // "OUTER_A", "OUTER_B", "PAIR"
                    Class = klass,
                    Grade = grade,
                    Size = $"D{ToInt(h.Dia)}"
                };

                // 폴리곤(센터라인) 작성: 로컬 평면 좌표 + z
                var poly = new Polygon();
                foreach (var (x, y) in h.PolygonXY)
                    poly.Points.Add(new Point(x, y, h.Z));
                sr.Polygons.Add(poly);

                // 훅 각도/길이
                sr.StartHook.Angle = h.StartHookDeg;
                sr.EndHook.Angle = h.EndHookDeg;
                sr.StartHook.Length = h.HookLen;
                sr.EndHook.Length = h.HookLen;

                // 필요시 HookType / Radius / BendingRadius 등 프로젝트 표준 추가 설정

                if (!sr.Insert())
                    throw new InvalidOperationException($"후프 삽입 실패: {h.Kind} @ Z={h.Z}");
            }
        }

        // ---------------------------
        // Helpers
        // ---------------------------
        private static TransformationPlane ToPlane(LocalCS cs)
        {
            var o = new Point(cs.Origin.X, cs.Origin.Y, cs.Origin.Z);
            var x = new Vector(cs.U.X, cs.U.Y, cs.U.Z);
            var y = new Vector(cs.V.X, cs.V.Y, cs.V.Z);
            return new TransformationPlane(o, x, y); // z는 자동 직교 완성
        }

        private static int ToInt(double mm)
        {
            // Tekla 프로파일/사이즈 표기 관례: 소수 버림
            return (int)Math.Round(mm, MidpointRounding.AwayFromZero);
        }
    }
}
