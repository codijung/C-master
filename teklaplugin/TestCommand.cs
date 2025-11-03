using System;
using System.IO;
using System.Windows.Forms;            // 파일 선택창 + 메시지 박스
using Newtonsoft.Json;
using ColumnRebar.Calculator;          // 계산 모듈 DTO
using ColumnRebar.Tekla;               // TeklaBuilder

public class TestCommand
{
    public static void RunSmokeFromJsonDialog()
    {
        try
        {
            // ① 파일 선택창 띄우기
            var dialog = new OpenFileDialog
            {
                Title = "Tekla용 Rebar JSON 파일 선택",
                Filter = "JSON 파일 (*.json)|*.json",
                InitialDirectory = @"C:\autotekla\exchange",   // 기본 위치 (원하면 수정 가능)
                Multiselect = false
            };

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                MessageBox.Show("❌ 파일 선택이 취소되었습니다.", "취소",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string path = dialog.FileName;

            // ② 파일 읽기 및 역직렬화
            var json = File.ReadAllText(path);
            var plan = JsonConvert.DeserializeObject<ColumnRebarPlan>(json);
            if (plan == null)
                throw new Exception("JSON 파싱 실패 (ColumnRebarPlan 구조 불일치)");

            // ③ TeklaBuilder 실행
            var builder = new TeklaBuilder();
            builder.Build(plan, concreteGrade: "C30/37", rebarGrade: "SD400");

            MessageBox.Show($"✅ Tekla 모델 생성 성공!\n파일: {Path.GetFileName(path)}",
                "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("❌ 오류 발생:\n" + ex.Message,
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
