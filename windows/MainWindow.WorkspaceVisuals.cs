using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private SpriteVisual? _workspaceBlurVisual;
    private CompositionEffectFactory? _workspaceBlurFactory;
    private CompositionEffectBrush? _workspaceBlurBrush;
    private CompositionBackdropBrush? _workspaceBackdropBrush;
    private CompositionRoundedRectangleGeometry? _workspaceClipGeometry;
    private CompositionGeometricClip? _workspaceClip;

    private void WorkspaceSurface_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (WorkspaceSurface.ActualWidth <= 0 || WorkspaceSurface.ActualHeight <= 0) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(WorkspaceSurface);
            _workspaceClipGeometry ??= visual.Compositor.CreateRoundedRectangleGeometry();
            _workspaceClipGeometry.Size = new((float)WorkspaceSurface.ActualWidth, (float)WorkspaceSurface.ActualHeight);
            _workspaceClipGeometry.CornerRadius = new(28);
            _workspaceClip ??= visual.Compositor.CreateGeometricClip(_workspaceClipGeometry);
            visual.Clip = _workspaceClip;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException) { }
    }

    private void UpdateWorkspaceBackdrop(bool show)
    {
        var enabled = show && !PerformanceMode.IsOn && !_chromeHighContrast && _uiSettings.AdvancedEffectsEnabled;
        try
        {
            if (enabled && _workspaceBlurVisual is null)
            {
                var compositor = ElementCompositionPreview.GetElementVisual(WorkspaceBlurHost).Compositor;
                _workspaceBlurFactory = compositor.CreateEffectFactory(new GaussianBlurEffect
                {
                    BlurAmount = 14, BorderMode = EffectBorderMode.Hard,
                    Source = new CompositionEffectSourceParameter("Backdrop")
                });
                _workspaceBackdropBrush = compositor.CreateBackdropBrush();
                _workspaceBlurBrush = _workspaceBlurFactory.CreateBrush();
                _workspaceBlurBrush.SetSourceParameter("Backdrop", _workspaceBackdropBrush);
                _workspaceBlurVisual = compositor.CreateSpriteVisual();
                _workspaceBlurVisual.RelativeSizeAdjustment = Vector2.One;
                _workspaceBlurVisual.Brush = _workspaceBlurBrush;
                ElementCompositionPreview.SetElementChildVisual(WorkspaceBlurHost, _workspaceBlurVisual);
            }
            if (_workspaceBlurVisual is not null) _workspaceBlurVisual.IsVisible = enabled;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            DisposeWorkspaceBackdrop();
        }
    }

    private void DisposeWorkspaceBackdrop()
    {
        ElementCompositionPreview.SetElementChildVisual(WorkspaceBlurHost, null);
        _workspaceBlurVisual?.Dispose(); _workspaceBlurVisual = null;
        _workspaceBlurBrush?.Dispose(); _workspaceBlurBrush = null;
        _workspaceBackdropBrush?.Dispose(); _workspaceBackdropBrush = null;
        _workspaceBlurFactory?.Dispose(); _workspaceBlurFactory = null;
    }

    private void DisposeWorkspaceClip()
    {
        ElementCompositionPreview.GetElementVisual(WorkspaceSurface).Clip = null;
        _workspaceClip?.Dispose(); _workspaceClip = null;
        _workspaceClipGeometry?.Dispose(); _workspaceClipGeometry = null;
    }
}
