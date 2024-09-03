﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Caliburn.Micro;
using Gemini.Framework;
using OngekiFumenEditor.Base;
using OngekiFumenEditor.Base.OngekiObjects;
using OngekiFumenEditor.Base.OngekiObjects.Beam;
using OngekiFumenEditor.Kernel.Graphics;
using OngekiFumenEditor.Kernel.Graphics.Performence;
using OngekiFumenEditor.Kernel.Scheduler;
using OngekiFumenEditor.Modules.FumenVisualEditor.Graphics;
using OngekiFumenEditor.Modules.FumenVisualEditor.Graphics.Drawing;
using OngekiFumenEditor.Modules.FumenVisualEditor.Graphics.Drawing.Editors;
using OngekiFumenEditor.Properties;
using OngekiFumenEditor.UI.Controls;
using OngekiFumenEditor.Utils;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using static OngekiFumenEditor.Kernel.Graphics.IDrawingContext;
using static OngekiFumenEditor.Modules.FumenVisualEditor.Graphics.Drawing.Editors.DrawXGridHelper;
using Color = System.Drawing.Color;
using Vector4 = System.Numerics.Vector4;

namespace OngekiFumenEditor.Modules.FumenVisualEditor.ViewModels;

public partial class FumenVisualEditorViewModel : PersistedDocument, ISchedulable, IFumenEditorDrawingContext
{
    private static Dictionary<string, IFumenEditorDrawingTarget[]> drawTargets = new();
    private IPerfomenceMonitor actualPerformenceMonitor;

    private readonly List<CacheDrawXLineResult> cachedMagneticXGridLines = new();

    private Func<double, FumenVisualEditorViewModel, double>
        convertToY = TGridCalculator.ConvertTGridUnitToY_DesignMode;

    private string displayFPS = "";
    private readonly Dictionary<IFumenEditorDrawingTarget, IEnumerable<OngekiTimelineObjectBase>> drawMap = new();
    private IFumenEditorDrawingTarget[] drawTargetOrder;
    private readonly IPerfomenceMonitor dummyPerformenceMonitor = new DummyPerformenceMonitor();
    private bool enablePlayFieldDrawing;

    private bool isDisplayFPS;
    private DrawJudgeLineHelper judgeLineHelper;
    private DrawPlayableAreaHelper playableAreaHelper;
    private DrawPlayerLocationHelper playerLocationHelper;
    private Vector4 playFieldBackgroundColor;
    private int renderViewHeight;

    private int renderViewWidth;
    private DrawSelectingRangeHelper selectingRangeHelper;

    private readonly StringBuilder stringBuilder = new(2048);

    private DrawTimeSignatureHelper timeSignatureHelper;

    private float viewHeight;
    private float viewWidth;

    private readonly List<(TGrid minTGrid, TGrid maxTGrid)> visibleTGridRanges = new();
    private DrawXGridHelper xGridHelper;
    public IEnumerable<CacheDrawXLineResult> CachedMagneticXGridLines => cachedMagneticXGridLines;

    public bool IsDisplayFPS
    {
        get => isDisplayFPS;
        set
        {
            Set(ref isDisplayFPS, value);
            PerfomenceMonitor = value ? actualPerformenceMonitor : dummyPerformenceMonitor;
        }
    }

    public string DisplayFPS
    {
        get => displayFPS;
        set
        {
            displayFPS = value;
            NotifyOfPropertyChange(() => DisplayFPS);
        }
    }

    public float ViewWidth
    {
        get => viewWidth;
        set
        {
            Set(ref viewWidth, value);
            RecalcViewProjectionMatrix();
        }
    }

    public float ViewHeight
    {
        get => viewHeight;
        set
        {
            Set(ref viewHeight, value);
            RecalcViewProjectionMatrix();
        }
    }

    public TimeSpan CurrentPlayTime { get; private set; } = TimeSpan.FromSeconds(0);

    public Matrix4 ViewMatrix { get; private set; }
    public Matrix4 ProjectionMatrix { get; private set; }
    public Matrix4 ViewProjectionMatrix { get; private set; }

    public FumenVisualEditorViewModel Editor => this;

    public VisibleRect Rect { get; set; }

    public IPerfomenceMonitor PerfomenceMonitor { get; private set; } = new DummyPerformenceMonitor();

    public void OnRenderSizeChanged(GLWpfControl glView, SizeChangedEventArgs sizeArg)
    {
        Log.LogDebug($"new size: {sizeArg.NewSize} , glView.RenderSize = {glView.RenderSize}");

        var dpiX = VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleX;
        var dpiY = VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleY;

        ViewWidth = (float) sizeArg.NewSize.Width;
        ViewHeight = (float) sizeArg.NewSize.Height;
        renderViewWidth = (int) (sizeArg.NewSize.Width * dpiX);
        renderViewHeight = (int) (sizeArg.NewSize.Height * dpiY);
    }

    public async void PrepareRender(GLWpfControl openGLView)
    {
        Log.LogDebug("ready.");
        await IoC.Get<IDrawingManager>().CheckOrInitGraphics();

        var dpiX = VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleX;
        var dpiY = VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleY;

        ViewWidth = (float) openGLView.ActualWidth;
        ViewHeight = (float) openGLView.ActualHeight;

        renderViewWidth = (int) (openGLView.ActualWidth * dpiX);
        renderViewHeight = (int) (openGLView.ActualHeight * dpiY);

        playFieldBackgroundColor = Color.FromArgb(EditorGlobalSetting.Default.PlayFieldBackgroundColor).ToVector4();
        enablePlayFieldDrawing = EditorGlobalSetting.Default.EnablePlayFieldDrawing;

        drawTargets = IoC.GetAll<IFumenEditorDrawingTarget>()
            .SelectMany(target => target.DrawTargetID.Select(supportId => (supportId, target)))
            .GroupBy(x => x.supportId).ToDictionary(x => x.Key, x => x.Select(x => x.target).ToArray());

        ResortRenderOrder();

        timeSignatureHelper = new DrawTimeSignatureHelper();
        xGridHelper = new DrawXGridHelper();
        judgeLineHelper = new DrawJudgeLineHelper();
        selectingRangeHelper = new DrawSelectingRangeHelper();
        playableAreaHelper = new DrawPlayableAreaHelper();
        playerLocationHelper = new DrawPlayerLocationHelper();

        actualPerformenceMonitor = IoC.Get<IPerfomenceMonitor>();
        IsDisplayFPS = IsDisplayFPS;

        openGLView.Render += Render;
    }

    public void Render(TimeSpan ts)
    {
        PerfomenceMonitor.PostUIRenderTime(ts);
        PerfomenceMonitor.OnBeforeRender();

#if DEBUG
        GLUtility.CheckError();
#endif

        CleanRender();
        GL.Viewport(0, 0, renderViewWidth, renderViewHeight);

        hits.Clear();

        var fumen = Fumen;
        if (fumen is null)
            return;

        var tGrid = GetCurrentTGrid();

        var curY = ConvertToY(tGrid.TotalUnit);
        var minY = (float) (curY - Setting.JudgeLineOffsetY);
        var maxY = minY + ViewHeight;

        //计算可以显示的TGrid范围以及像素范围
        visibleTGridRanges.Clear();
        if (IsDesignMode)
        {
            var minTGrid = TGridCalculator.ConvertYToTGrid_DesignMode(minY, this) ?? TGrid.Zero;
            var maxTGrid = TGridCalculator.ConvertYToTGrid_DesignMode(maxY, this);

            if (maxTGrid is null || minTGrid is null)
                return;
            visibleTGridRanges.Add((minTGrid, maxTGrid));
        }
        else
        {
            var scale = Setting.VerticalDisplayScale;
            var ranges =
                Fumen.Soflans.GetVisibleRanges_PreviewMode(curY, ViewHeight, Setting.JudgeLineOffsetY, Fumen.BpmList,
                    scale);

            foreach (var x in ranges)
            {
                if (x.maxTGrid is null || x.minTGrid is null)
                    return;
                visibleTGridRanges.Add((x.minTGrid, x.maxTGrid));
            }
        }

        Rect = new VisibleRect(new Vector2(ViewWidth, minY), new Vector2(0, minY + ViewHeight));

        RecalculateMagaticXGridLines();

        foreach (var (minTGrid, maxTGrid) in visibleTGridRanges)
            playableAreaHelper.DrawPlayField(this, minTGrid, maxTGrid);
        playableAreaHelper.Draw(this);
        timeSignatureHelper.DrawLines(this);

        xGridHelper.DrawLines(this, CachedMagneticXGridLines);

        //todo 可以把GroupBy()给优化掉
        var renderObjects =
            GetDisplayableObjects(fumen, visibleTGridRanges)
                .Distinct()
                .OfType<OngekiTimelineObjectBase>()
                .GroupBy(x => x.IDShortName);

        foreach (var objGroup in renderObjects)
        {
            if (GetDrawingTarget(objGroup.Key) is not IFumenEditorDrawingTarget[] drawingTargets)
                continue;

            foreach (var drawingTarget in drawingTargets)
                if (!drawMap.TryGetValue(drawingTarget, out var enums))
                    drawMap[drawingTarget] = objGroup;
                else
                    drawMap[drawingTarget] = enums.Concat(objGroup);
        }

        if (IsPreviewMode)
        {
            //特殊处理：子弹和Bell
            foreach (var drawingTarget in GetDrawingTarget(Bullet.CommandName))
                drawMap[drawingTarget] = Fumen.Bullets;
            foreach (var drawingTarget in GetDrawingTarget(Bell.CommandName))
                drawMap[drawingTarget] = Fumen.Bells;
        }

        var prevOrder = int.MinValue;
        foreach (var drawingTarget in drawTargetOrder.Where(x => CheckDrawingVisible(x.Visible)))
        {
            //check render order
            var order = drawingTarget.CurrentRenderOrder;
            if (prevOrder > order)
            {
                ResortRenderOrder();
                CleanRender();
                break;
            }

            prevOrder = order;

            if (drawMap.TryGetValue(drawingTarget, out var drawingObjs))
            {
                drawingTarget.Begin(this);
                foreach (var obj in drawingObjs.OrderBy(x => x.TGrid))
                    drawingTarget.Post(obj);
                drawingTarget.End();
            }
        }

        drawMap.Clear();

        timeSignatureHelper.DrawTimeSigntureText(this);
        xGridHelper.DrawXGridText(this, CachedMagneticXGridLines);
        judgeLineHelper.Draw(this);
        playerLocationHelper.Draw(this);
        selectingRangeHelper.Draw(this);


        PerfomenceMonitor.OnAfterRender();
    }

    public bool CheckDrawingVisible(DrawingVisible visible)
    {
        return visible.HasFlag(EditorObjectVisibility == Visibility.Visible
            ? DrawingVisible.Design
            : DrawingVisible.Preview);
    }

    public double ConvertToY(double tGridUnit)
    {
        return convertToY(tGridUnit, this);
    }

    public bool CheckVisible(TGrid tGrid)
    {
        foreach (var (minTGrid, maxTGrid) in visibleTGridRanges)
            if (minTGrid <= tGrid && tGrid <= maxTGrid)
                return true;
        return false;
    }

    public bool CheckRangeVisible(TGrid minTGrid, TGrid maxTGrid)
    {
        foreach (var visibleRange in visibleTGridRanges)
        {
            var result = !(minTGrid > visibleRange.maxTGrid || maxTGrid < visibleRange.minTGrid);
            if (result)
                return true;
        }

        return false;
    }

    public string SchedulerName => "Fumen Previewer Performance Statictis";

    public TimeSpan ScheduleCallLoopInterval => TimeSpan.FromSeconds(1);

    public void OnSchedulerTerm()
    {
    }

    public Task OnScheduleCall(CancellationToken cancellationToken)
    {
        if (IsDisplayFPS)
        {
            stringBuilder.Clear();

            PerfomenceMonitor?.FormatStatistics(stringBuilder);
#if DEBUG
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"View: {ViewWidth}x{ViewHeight}");
            stringBuilder.AppendLine($"VisibleYRange: [{Rect.MinY}, {Rect.MaxY}]");
            stringBuilder.AppendLine("VisibleTGridRanges:");
            foreach (var tGridRange in visibleTGridRanges.ToArray())
                stringBuilder.AppendLine($"*   {tGridRange.minTGrid}  -  {tGridRange.maxTGrid}");
#endif

            DisplayFPS = stringBuilder.ToString();

            PerfomenceMonitor?.Clear();
        }

        return Task.CompletedTask;
    }

    protected override void OnViewLoaded(object v)
    {
        base.OnViewLoaded(v);
        InitExtraMenuItems();
    }

    private void RecalcViewProjectionMatrix()
    {
        var xOffset = 0;

        var y = (float) convertToY(GetCurrentTGrid().TotalUnit, this);

        ProjectionMatrix =
            Matrix4.CreateOrthographic(ViewWidth, ViewHeight, -1, 1);
        ViewMatrix =
            Matrix4.CreateTranslation(new Vector3(-ViewWidth / 2 + xOffset,
                -y - ViewHeight / 2 + (float) Setting.JudgeLineOffsetY, 0));

        ViewProjectionMatrix = ViewMatrix * ProjectionMatrix;
    }

    private void ResortRenderOrder()
    {
        drawTargetOrder = drawTargets.Values.SelectMany(x => x).OrderBy(x => x.CurrentRenderOrder).Distinct().ToArray();
    }

    public IFumenEditorDrawingTarget[] GetDrawingTarget(string name)
    {
        return drawTargets.TryGetValue(name, out var drawingTarget) ? drawingTarget : default;
    }

    private IEnumerable<IDisplayableObject> GetDisplayableObjects(OngekiFumen fumen,
        IEnumerable<(TGrid min, TGrid max)> visibleRanges)
    {
        var containBeams = fumen.Beams.Any();

        var objects = visibleRanges.SelectMany(x =>
        {
            var (min, max) = x;
            var r = Enumerable.Empty<IDisplayableObject>()
                .Concat(fumen.Flicks.BinaryFindRange(min, max))
                .Concat(fumen.MeterChanges.Skip(1)) //not show first meter
                .Concat(fumen.BpmList.Skip(1)) //not show first bpm
                .Concat(fumen.ClickSEs.BinaryFindRange(min, max))
                .Concat(fumen.LaneBlocks.GetVisibleStartObjects(min, max))
                .Concat(fumen.Comments.BinaryFindRange(min, max))
                .Concat(fumen.Soflans.GetVisibleStartObjects(min, max))
                .Concat(fumen.EnemySets.BinaryFindRange(min, max))
                .Concat(fumen.Lanes.GetVisibleStartObjects(min, max))
                .Concat(fumen.Taps.BinaryFindRange(min, max))
                .Concat(fumen.Holds.GetVisibleStartObjects(min, max))
                .Concat(fumen.SvgPrefabs);

            if (containBeams)
            {
                var leadInTGrid = TGridCalculator.ConvertAudioTimeToTGrid(
                    TGridCalculator.ConvertTGridToAudioTime(min, this) -
                    TGridCalculator.ConvertFrameToAudioTime(BeamStart.LEAD_IN_DURATION_FRAME), this);
                var leadOutTGrid = TGridCalculator.ConvertAudioTimeToTGrid(
                    TGridCalculator.ConvertTGridToAudioTime(max, this) +
                    TimeSpan.FromMilliseconds(BeamStart.LEAD_OUT_DURATION), this);

                r = r.Concat(fumen.Beams.GetVisibleStartObjects(leadInTGrid, leadOutTGrid));
            }

            return r;
        });

        /*
         * 这里考虑到有spd<1的子弹/Bell会提前出现的情况，因此得分状态分别去选择
         */
        var objs = Enumerable.Empty<IDisplayableObject>();
        if (Editor.IsPreviewMode)
        {
            /*
            var r = fumen.Bells
                .AsEnumerable<IBulletPalleteReferencable>()
                .Concat(fumen.Bullets);

            objs = objs.Concat(r);
            */
        }
        else
        {
            foreach (var item in visibleRanges)
            {
                var (min, max) = item;
                var blts = fumen.Bullets.BinaryFindRange(min, max);
                var bels = fumen.Bells.BinaryFindRange(min, max);

                objs = objs.Concat(bels);
                objs = objs.Concat(blts);
            }
        }

        return objects.Concat(objs).SelectMany(x => x.GetDisplayableObjects());
    }

    private void CleanRender()
    {
        if (IsDesignMode || !enablePlayFieldDrawing)
            GL.ClearColor(16 / 255.0f, 16 / 255.0f, 16 / 255.0f, 1);
        else
            GL.ClearColor(playFieldBackgroundColor.X, playFieldBackgroundColor.Y, playFieldBackgroundColor.Z,
                playFieldBackgroundColor.W);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public void OnLoaded(ActionExecutionContext e)
    {
    }

    private void RecalculateMagaticXGridLines()
    {
        //todo 可以优化
        cachedMagneticXGridLines.Clear();

        var xOffset = (float) Setting.XOffset;
        var width = ViewWidth;
        if (width == 0)
            return;
        var xUnitSpace = (float) Setting.XGridUnitSpace;
        var maxDisplayXUnit = Setting.XGridDisplayMaxUnit;

        var unitSize = (float) XGridCalculator.CalculateXUnitSize(maxDisplayXUnit, width, xUnitSpace);
        var totalUnitValue = 0f;

        var baseX = width / 2 + xOffset;

        var limitLength = width + Math.Abs(xOffset);

        for (var totalLength = baseX + unitSize; totalLength - xOffset < limitLength; totalLength += unitSize)
        {
            totalUnitValue += xUnitSpace;

            cachedMagneticXGridLines.Add(new CacheDrawXLineResult
            {
                X = totalLength,
                XGridTotalUnit = totalUnitValue,
                XGridTotalUnitDisplay = totalUnitValue.ToString()
            });

            cachedMagneticXGridLines.Add(new CacheDrawXLineResult
            {
                X = baseX - (totalLength - baseX),
                XGridTotalUnit = -totalUnitValue,
                XGridTotalUnitDisplay = (-totalUnitValue).ToString()
            });
        }

        cachedMagneticXGridLines.Add(new CacheDrawXLineResult
        {
            X = baseX,
            XGridTotalUnit = 0f
        });
    }

    public void OnSizeChanged(ActionExecutionContext e)
    {
        var scrollViewer = e.Source as AnimatedScrollViewer;
        scrollViewer?.InvalidateMeasure();
    }
}