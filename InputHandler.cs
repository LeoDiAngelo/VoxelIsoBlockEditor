using System.Windows;
using System.Windows.Input;
using WpfPoint = System.Windows.Point;

namespace IsoBlockCharacterEditor;

internal interface IVoxelEditorInputTarget
{
    void FocusViewport();
    void RotateCameraByMouseDelta(double dx, double dy);
    void HandleLeftClick(WpfPoint point);
    void HandleRightClick(WpfPoint point);
    void HandleMouseWheel(int delta);
}

internal sealed class InputHandler
{
    private readonly UIElement _element;
    private readonly IVoxelEditorInputTarget _target;

    private bool _rightDown;
    private bool _rightDragged;
    private bool _middleDown;
    private WpfPoint _lastMouse;
    private WpfPoint _rightDownPoint;

    public InputHandler(UIElement element, IVoxelEditorInputTarget target)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void Attach()
    {
        _element.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        _element.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
        _element.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        _element.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), true);
    }

    public void ClearCaptureState()
    {
        _rightDown = false;
        _rightDragged = false;
        _middleDown = false;
        Mouse.Capture(null);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _target.FocusViewport();
        _lastMouse = e.GetPosition(_element);

        if (e.ChangedButton == MouseButton.Right)
        {
            _rightDown = true;
            _rightDragged = false;
            _rightDownPoint = _lastMouse;
            Mouse.Capture(_element, CaptureMode.Element);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            _middleDown = true;
            Mouse.Capture(_element, CaptureMode.Element);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            _target.HandleLeftClick(_lastMouse);
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        WpfPoint p = e.GetPosition(_element);
        double dx = p.X - _lastMouse.X;
        double dy = p.Y - _lastMouse.Y;

        if (_rightDown)
        {
            if (Math.Abs(p.X - _rightDownPoint.X) > 3 || Math.Abs(p.Y - _rightDownPoint.Y) > 3)
                _rightDragged = true;

            if (_rightDragged)
                _target.RotateCameraByMouseDelta(dx, dy);

            e.Handled = true;
        }
        else if (_middleDown)
        {
            _target.RotateCameraByMouseDelta(dx, dy);
            e.Handled = true;
        }

        _lastMouse = p;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        WpfPoint p = e.GetPosition(_element);
        bool wasRightClick = _rightDown && e.ChangedButton == MouseButton.Right && !_rightDragged &&
                             Math.Abs(p.X - _rightDownPoint.X) < 4 && Math.Abs(p.Y - _rightDownPoint.Y) < 4;

        if (e.ChangedButton == MouseButton.Right)
            _rightDown = false;
        if (e.ChangedButton == MouseButton.Middle)
            _middleDown = false;

        if (!_rightDown && !_middleDown)
            Mouse.Capture(null);

        if (wasRightClick)
            _target.HandleRightClick(p);

        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _target.HandleMouseWheel(e.Delta);
        e.Handled = true;
    }
}
