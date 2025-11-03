// 필수 네임스페이스
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

#region JSON 데이터 구조 정의 (DTO)
public class PointDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public class Chain
{
    public int ChainId { get; set; }
    public int PointCount { get; set; }
    public List<PointDto> Vertices { get; set; }
}

public class ChainData
{
    public int TotalChains { get; set; }
    public List<Chain> Chains { get; set; }
}
#endregion

#region Point3d 비교자 클래스
public class Point3dComparer : IEqualityComparer<Point3d>
{
    private readonly Tolerance _tolerance;
    private readonly double _precisionFactor;

    public Point3dComparer(Tolerance tolerance)
    {
        _tolerance = tolerance;
        _precisionFactor = Math.Pow(10, -Math.Log10(_tolerance.EqualPoint));
    }

    public bool Equals(Point3d p1, Point3d p2)
    {
        return p1.IsEqualTo(p2, _tolerance);
    }

    public int GetHashCode(Point3d p)
    {
        long x = (long)Math.Round(p.X * _precisionFactor);
        long y = (long)Math.Round(p.Y * _precisionFactor);
        long z = (long)Math.Round(p.Z * _precisionFactor);
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + x.GetHashCode();
            hash = hash * 23 + y.GetHashCode();
            hash = hash * 23 + z.GetHashCode();
            return hash;
        }
    }
}
#endregion

#region 메인 로직 클래스
public class LineOrderProcessor
{
    private static Editor _editor;
    private static Tolerance _customTolerance;

    private class ChainBuildResult
    {
        public bool Success { get; private set; }
        public Point3d FailurePoint { get; private set; }
        public string FailureReason { get; private set; }

        private ChainBuildResult(bool success, Point3d point, string reason)
        {
            Success = success; FailurePoint = point; FailureReason = reason;
        }

        public static ChainBuildResult CreateSuccess()
        {
            return new ChainBuildResult(true, Point3d.Origin, string.Empty);
        }

        public static ChainBuildResult CreateFailure(Point3d point, string reason)
        {
            _editor.WriteMessage($"\n{reason} (점: {point.X:F2}, {point.Y:F2})");
            return new ChainBuildResult(false, point, reason);
        }
    }


    [CommandMethod("OVERKILL_AND_CREATE_CHAINS")]
    public static void OverkillAndCreateChains()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        Database db = doc.Database;
        _editor = doc.Editor;

        // ★★★★★ 새로 추가된 부분: OVERKILL 자동 실행 ★★★★★
        _editor.WriteMessage("\n겹치는 선 정리를 위해 OVERKILL 명령을 실행합니다...");
        // SendStringToExecute를 사용하여 커맨드라인 버전의 OVERKILL을 실행합니다.
        // "_.-OVERKILL" : 대화상자 없는 버전 실행
        // "ALL" : 모든 객체 선택
        // "\"\"" : 객체 선택 완료 (Enter)
        // "\"\"" : 설정 대화상자 확인 (Enter)
        // 마지막 공백(" ")은 Enter키와 같음
        doc.SendStringToExecute("_.-OVERKILL ALL \"\" \"\" ", true, false, false);
        _editor.WriteMessage("\nOVERKILL 완료. 체인 탐색을 시작합니다.");
        // ★★★★★ 추가된 부분 종료 ★★★★★

        PromptDoubleOptions pdo = new PromptDoubleOptions("\n연결 허용 오차(Tolerance)를 입력하세요");
        pdo.DefaultValue = 1.0;
        pdo.AllowNegative = false;
        pdo.AllowZero = false;

        PromptDoubleResult pdr = _editor.GetDouble(pdo);
        if (pdr.Status != PromptStatus.OK)
        {
            _editor.WriteMessage("\n작업이 취소되었습니다.");
            return;
        }
        _customTolerance = new Tolerance(pdr.Value, pdr.Value);

        PromptSelectionResult selRes = _editor.GetSelection(new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE") }));
        if (selRes.Status == PromptStatus.Cancel)
        {
            _editor.WriteMessage("\n사용자에 의해 작업이 취소되었습니다.");
            return;
        }
        if (selRes.Status != PromptStatus.OK) return;

        var allChains = new List<List<Point3d>>();
        var failureMarkers = new List<ChainBuildResult>();
        var allLines = new List<Line>();
        var connectivityMap = new Dictionary<Point3d, List<Line>>(new Point3dComparer(_customTolerance));
        var visitedLines = new HashSet<ObjectId>();

        try
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 1. '연결 정보 지도' 제작
                foreach (ObjectId id in selRes.Value.GetObjectIds())
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Line line)
                    {
                        allLines.Add(line);
                        if (!connectivityMap.ContainsKey(line.StartPoint)) connectivityMap[line.StartPoint] = new List<Line>();
                        connectivityMap[line.StartPoint].Add(line);
                        if (!connectivityMap.ContainsKey(line.EndPoint)) connectivityMap[line.EndPoint] = new List<Line>();
                        connectivityMap[line.EndPoint].Add(line);
                    }
                }
                if (allLines.Count == 0) return;

                // 2. 체인 탐색
                _editor.WriteMessage("\n\n===== '닫힌 체인 전용' 탐색 시작 =====");
                foreach (var startLine in allLines)
                {
                    if (visitedLines.Contains(startLine.ObjectId)) continue;

                    var currentChainPoints = new LinkedList<Point3d>();
                    currentChainPoints.AddLast(startLine.StartPoint);
                    currentChainPoints.AddLast(startLine.EndPoint);
                    var linesInThisChain = new HashSet<ObjectId> { startLine.ObjectId };

                    ChainBuildResult forwardResult = TryFindClosedLoop(currentChainPoints, connectivityMap, linesInThisChain, true);
                    ChainBuildResult backwardResult = ChainBuildResult.CreateSuccess();

                    if (forwardResult.Success)
                    {
                        backwardResult = TryFindClosedLoop(currentChainPoints, connectivityMap, linesInThisChain, false);
                    }

                    // 3. 결과 처리
                    if (forwardResult.Success && backwardResult.Success)
                    {
                        allChains.Add(currentChainPoints.ToList());
                        visitedLines.UnionWith(linesInThisChain);
                        _editor.WriteMessage($"\n>> {allChains.Count}번 닫힌 체인 생성 성공 (점 {currentChainPoints.Count}개)");
                    }
                    else
                    {
                        visitedLines.Add(startLine.ObjectId);
                        if (!forwardResult.Success) failureMarkers.Add(forwardResult);
                        if (!backwardResult.Success) failureMarkers.Add(backwardResult);
                    }
                }
                tr.Commit();
            }

            CreateVisualDebugMarkers(db, allChains, failureMarkers);
            CreateJsonAndRunExe(allChains);
        }
        catch (System.Exception sysEx)
        {
            _editor.WriteMessage($"\n작업 중 예기치 않은 오류 발생: {sysEx.Message}\n{sysEx.StackTrace}");
        }
    }

    private static ChainBuildResult TryFindClosedLoop(LinkedList<Point3d> chain, Dictionary<Point3d, List<Line>> map, HashSet<ObjectId> linesInThisChain, bool forward)
    {
        while (true)
        {
            Point3d searchPoint = forward ? chain.Last.Value : chain.First.Value;
            Point3d otherEndOfChain = forward ? chain.First.Value : chain.Last.Value;

            if (chain.Count > 2 && searchPoint.IsEqualTo(otherEndOfChain, _customTolerance))
            {
                return ChainBuildResult.CreateSuccess();
            }

            if (!map.ContainsKey(searchPoint))
            {
                return ChainBuildResult.CreateFailure(searchPoint, "경고: 열린 체인 발견 (연결선 없음)");
            }

            var allConnectedLines = map[searchPoint];
            var nextPotentialLines = allConnectedLines
                .Where(l => !linesInThisChain.Contains(l.ObjectId))
                .ToList();

            if (allConnectedLines.Count > 2)
            {
                return ChainBuildResult.CreateFailure(searchPoint, "경고: 분기점 발견 (선 3개 이상)");
            }

            if (nextPotentialLines.Count == 0)
            {
                return ChainBuildResult.CreateFailure(searchPoint, "경고: 열린 체인 발견 (새 선 없음)");
            }
            else if (nextPotentialLines.Count == 1)
            {
                var nextLine = nextPotentialLines[0];
                linesInThisChain.Add(nextLine.ObjectId);
                Point3d nextPoint = nextLine.StartPoint.IsEqualTo(searchPoint, _customTolerance)
                                 ? nextLine.EndPoint
                                 : nextLine.StartPoint;
                if (forward)
                    chain.AddLast(nextPoint);
                else
                    chain.AddFirst(nextPoint);
            }
            else
            {
                return ChainBuildResult.CreateFailure(searchPoint, "경고: 분기점 발견 (Y자 경로)");
            }
        }
    }

    private static void CreateVisualDebugMarkers(Database db, List<List<Point3d>> allChains, List<ChainBuildResult> failures)
    {
        _editor.WriteMessage("\n도면에 시각적 디버그 정보를 표시합니다...");
        using (Transaction writeTr = db.TransactionManager.StartTransaction())
        {
            CreateOrGetLayer(writeTr, db, "DEBUG_MARKERS", 2);
            BlockTable bt = writeTr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            BlockTableRecord ms = writeTr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

            double textHeight = 50.0;
            double offset = textHeight * 0.5;
            double circleRadius = textHeight * 0.7;

            // 1. 성공한 체인 표시 (노란색 원 + 빨간색 텍스트)
            int chainCounter = 1;
            foreach (var chain in allChains)
            {
                bool isClosed = chain.Count > 1 && chain.First().IsEqualTo(chain.Last(), _customTolerance);
                for (int i = 0; i < chain.Count; i++)
                {
                    if (isClosed && i == chain.Count - 1) continue;
                    Point3d point = chain[i];
                    int pointCounter = i + 1;

                    Circle debugCircle = new Circle
                    {
                        Center = point,
                        Radius = circleRadius,
                        Layer = "DEBUG_MARKERS",
                        ColorIndex = 2 // Yellow
                    };
                    ms.AppendEntity(debugCircle);
                    writeTr.AddNewlyCreatedDBObject(debugCircle, true);

                    DBText dbText = new DBText
                    {
                        Position = new Point3d(point.X + offset, point.Y + offset, point.Z),
                        Height = textHeight,
                        ColorIndex = 1, // Red
                        Layer = "DEBUG_MARKERS"
                    };
                    string coords = $"({point.X:F2}, {point.Y:F2}, {point.Z:F2})";
                    dbText.TextString = $"{chainCounter}-{pointCounter}\n{coords}";
                    ms.AppendEntity(dbText);
                    writeTr.AddNewlyCreatedDBObject(dbText, true);
                }
                chainCounter++;
            }

            // 2. 실패한 지점 표시 (자홍색 텍스트)
            foreach (var failure in failures)
            {
                Point3d point = failure.FailurePoint;
                DBText failText = new DBText
                {
                    Position = new Point3d(point.X + offset, point.Y - offset, point.Z), // 오른쪽 아래에 표시
                    Height = textHeight * 0.8, // 약간 작게
                    ColorIndex = 6, // 6 = Magenta (자홍색)
                    Layer = "DEBUG_MARKERS"
                };
                string coords = $"({point.X:F2}, {point.Y:F2})";
                failText.TextString = $"{failure.FailureReason}\n{coords}";

                ms.AppendEntity(failText);
                writeTr.AddNewlyCreatedDBObject(failText, true);
            }

            writeTr.Commit();
        }
        _editor.WriteMessage("\n정보 표시 완료.");
    }

    private static void CreateJsonAndRunExe(List<List<Point3d>> allChains)
    {
        var chainData = new ChainData { Chains = new List<Chain>() };
        int chainIdCounter = 1;
        foreach (var pointList in allChains)
        {
            chainData.Chains.Add(new Chain
            {
                ChainId = chainIdCounter++,
                PointCount = pointList.Count,
                Vertices = pointList.Select(p => new PointDto { X = p.X, Y = p.Y, Z = p.Z }).ToList()
            });
        }
        chainData.TotalChains = chainData.Chains.Count;

        string jsonOutput = JsonConvert.SerializeObject(chainData, Formatting.Indented);
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string jsonFilePath = Path.Combine(desktopPath, "autocad_chain_data.json");
        File.WriteAllText(jsonFilePath, jsonOutput);

        _editor.WriteMessage($"\n--- 총 {chainData.TotalChains}개의 체인 정보를 JSON 파일로 저장했습니다. ---");
        _editor.WriteMessage($"\n경로: {jsonFilePath}");

        string assemblyLocation = Assembly.GetExecutingAssembly().Location;
        string assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
        string exePath = Path.Combine(assemblyDirectory, "teklaplugin.exe");

        if (File.Exists(exePath))
        {
            Process.Start(exePath, $"\"{jsonFilePath}\"");
            _editor.WriteMessage($"\nteklapulgin.exe를 실행했습니다.");
        }
        else
        {
            _editor.WriteMessage($"\n오류: TeklaProcessor.exe 파일을 찾을 수 없습니다. 경로: {exePath}");
        }
    }

    private static void CreateOrGetLayer(Transaction tr, Database db, string layerName, short colorIndex)
    {
        LayerTable lt = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
        if (!lt.Has(layerName))
        {
            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord
            {
                Name = layerName,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
            };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }
}
#endregion