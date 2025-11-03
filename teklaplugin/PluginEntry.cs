using System.Collections.Generic;
using Tekla.Structures.Plugins;

public class PluginEntry : PluginBase
{
    public override List<InputDefinition> DefineInput()
    {
        // 모델 선택 입력이 필요 없으니 비워둠
        return new List<InputDefinition>();
    }

    public override bool Run(List<InputDefinition> input)
    {
        // 파일 선택창 열고 JSON → Tekla 삽입
        TestCommand.RunSmokeFromJsonDialog();
        return true;
    }
}
